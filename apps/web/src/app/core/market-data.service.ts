import { Injectable, Signal, computed, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';

import type { InstrumentKey, OrderRecord } from './models';
import type { StreamState } from './models/trading.model';
import type { Tick } from './models/market-data.model';

/** One live subscription: which link it runs through, and how many components want it. */
interface StreamSubscription {
  readonly brokerLinkId: string;
  readonly instrument: InstrumentKey;
  readonly count: number;
}

/** Refcount key. The link is part of the identity, not decoration — see `refCounts`. */
function streamKey(brokerLinkId: string, instrument: InstrumentKey): string {
  return `${brokerLinkId}\u0000${instrument}`;
}

/**
 * SignalR wrapper that exposes SIGNALS, never Observables — every consumer in
 * this app (watchlist rows, the order ticket's live price, the order blotter)
 * is a template reading a signal directly, and using RxJS here would just
 * mean every one of them wrapping the result straight back into `toSignal`.
 * One conversion point, here, instead of N of them at the call sites.
 *
 * Subscriptions are REFERENCE-COUNTED: two components watching the same
 * instrument (a watchlist row and an open order ticket) share one server
 * subscription, and unsubscribing is a no-op until the last watcher goes
 * away. This is what lets every feature call `subscribe`/the returned
 * cleanup function locally, in its own `effect`, without coordinating with
 * every other feature that might also care about the same symbol.
 */
@Injectable({ providedIn: 'root' })
export class MarketDataService {
  private hub: signalR.HubConnection | undefined;

  /**
   * Reference counts keyed by LINK **and** instrument: the hub subscribes per
   * broker link (`MarketDataHub.Subscribe(brokerLinkId, instruments)`), so the
   * same symbol watched through two linked accounts is two upstream
   * subscriptions and must not share one count.
   */
  private readonly refCounts = new Map<string, StreamSubscription>();

  private readonly _connectionState = signal<StreamState>('disconnected');
  readonly connectionState = this._connectionState.asReadonly();

  private readonly _ticks = signal<ReadonlyMap<InstrumentKey, Tick>>(new Map());
  private readonly _lastTickAt = signal<ReadonlyMap<InstrumentKey, number>>(new Map());

  /** Most recent order update pushed over the socket; the blotter reconciles against REST, this just wakes it up sooner. */
  private readonly _orderUpdate = signal<OrderRecord | undefined>(undefined);
  readonly orderUpdate = this._orderUpdate.asReadonly();

  /** True once >10s have passed with the hub connected but no tick for a WATCHED instrument. Read by `stale-banner`. */
  readonly isAnyWatchedInstrumentStale = computed(() => {
    const lastAt = this._lastTickAt();
    const now = Date.now();
    for (const { instrument } of this.refCounts.values()) {
      const at = lastAt.get(instrument);
      if (at === undefined || now - at > 10_000) {
        return true;
      }
    }
    return false;
  });

  connect(hubUrl = '/hubs/market-data'): void {
    if (this.hub) {
      return;
    }

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10_000, 30_000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.hub.onreconnecting(() => this._connectionState.set('reconnecting'));
    this.hub.onreconnected(() => {
      this._connectionState.set('connected');
      // A fresh transport lost every server-side subscription; re-declare
      // everything currently referenced rather than trusting the reconnect
      // to have preserved state on the hub.
      this.resubscribeAll();
    });
    this.hub.onclose(() => this._connectionState.set('disconnected'));

    this.hub.on('tick', (tick: Tick) => this.applyTick(tick));
    this.hub.on('orderUpdate', (order: OrderRecord) => this._orderUpdate.set(order));

    this._connectionState.set('connecting');
    this.hub
      .start()
      .then(() => {
        this._connectionState.set('connected');
        // Anything that subscribed DURING the handshake has a refcount but no
        // server-side subscription — `subscribe` can only invoke on a connected
        // hub. Declare the whole set now, exactly as a reconnect does, so the
        // first watcher of an instrument is never the one that silently misses
        // its stream.
        this.resubscribeAll();
      })
      .catch(() => this._connectionState.set('disconnected'));
  }

  disconnect(): void {
    void this.hub?.stop();
    this.hub = undefined;
    this._connectionState.set('disconnected');
  }

  /**
   * Subscribes to one instrument's stream and returns a cleanup function.
   * Call the cleanup in the same place you subscribed (an `effect`'s
   * teardown, a component's `ngOnDestroy`/`DestroyRef`) — never rely on
   * garbage collection to release a broker-side subscription, which is a
   * metered resource per `manifest.marketData.maxStreamSubscriptions`.
   */
  subscribe(brokerLinkId: string, instrument: InstrumentKey): () => void {
    // The socket opens on demand, with the first watcher: a session that never
    // looks at a price never holds one open. `connect` is idempotent, and the
    // `resubscribeAll` in its `start()` handler covers everything that
    // subscribes while this first handshake is still in flight.
    this.connect();

    const key = streamKey(brokerLinkId, instrument);
    const existing = this.refCounts.get(key);
    this.refCounts.set(key, { brokerLinkId, instrument, count: (existing?.count ?? 0) + 1 });

    if (!existing && this.hub?.state === signalR.HubConnectionState.Connected) {
      void this.hub.invoke('Subscribe', brokerLinkId, [instrument]).catch(() => {
        // A failed subscribe leaves the instrument stale rather than
        // throwing into a template's effect; `isAnyWatchedInstrumentStale`
        // and the per-row `connection-status` badge are how this surfaces.
      });
    }

    let released = false;
    return () => {
      if (released) {
        return;
      }
      released = true;
      const remaining = (this.refCounts.get(key)?.count ?? 1) - 1;
      if (remaining <= 0) {
        this.refCounts.delete(key);
        if (this.hub?.state === signalR.HubConnectionState.Connected) {
          void this.hub.invoke('Unsubscribe', brokerLinkId, [instrument]).catch(() => undefined);
        }
      } else {
        this.refCounts.set(key, { brokerLinkId, instrument, count: remaining });
      }
    };
  }

  /** Latest tick for one instrument, or undefined until the first one arrives. */
  tickFor(instrument: InstrumentKey): Signal<Tick | undefined> {
    return computed(() => this._ticks().get(instrument));
  }

  /** Milliseconds since the last tick for this instrument, or undefined if none has arrived yet. */
  ageMsFor(instrument: InstrumentKey): Signal<number | undefined> {
    return computed(() => {
      const at = this._lastTickAt().get(instrument);
      return at === undefined ? undefined : Date.now() - at;
    });
  }

  private applyTick(tick: Tick): void {
    const next = new Map(this._ticks());
    next.set(tick.instrument, tick);
    this._ticks.set(next);

    const nextAges = new Map(this._lastTickAt());
    nextAges.set(tick.instrument, Date.now());
    this._lastTickAt.set(nextAges);
  }

  private resubscribeAll(): void {
    if (!this.hub) {
      return;
    }
    for (const { brokerLinkId, instrument } of this.refCounts.values()) {
      void this.hub.invoke('Subscribe', brokerLinkId, [instrument]).catch(() => undefined);
    }
  }
}

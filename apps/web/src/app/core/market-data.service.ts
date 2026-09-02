import { Injectable, Signal, computed, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';

import type { InstrumentKey, OrderRecord } from './models';
import type { StreamMode, StreamState } from './models/trading.model';
import type { Tick } from './models/market-data.model';

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
  private readonly refCounts = new Map<InstrumentKey, number>();

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
    for (const mic of this.refCounts.keys()) {
      const at = lastAt.get(mic);
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
      .then(() => this._connectionState.set('connected'))
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
  subscribe(instrument: InstrumentKey, mode: StreamMode = 'ltp'): () => void {
    const count = this.refCounts.get(instrument) ?? 0;
    this.refCounts.set(instrument, count + 1);

    if (count === 0 && this.hub?.state === signalR.HubConnectionState.Connected) {
      void this.hub.invoke('Subscribe', [instrument], mode).catch(() => {
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
      const remaining = (this.refCounts.get(instrument) ?? 1) - 1;
      if (remaining <= 0) {
        this.refCounts.delete(instrument);
        if (this.hub?.state === signalR.HubConnectionState.Connected) {
          void this.hub.invoke('Unsubscribe', [instrument]).catch(() => undefined);
        }
      } else {
        this.refCounts.set(instrument, remaining);
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
    for (const instrument of this.refCounts.keys()) {
      void this.hub.invoke('Subscribe', [instrument], 'ltp' satisfies StreamMode).catch(() => undefined);
    }
  }
}

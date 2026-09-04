import { effect, inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import { staleOnBrokerLinkChange } from '../../core/broker-links.store';
import { MarketDataService } from '../../core/market-data.service';
import type { CancelAllRequest, CancelAllResult, ModifyOrderRequest, OrderRecord } from '../../core/models';

interface State {
  readonly orders: readonly OrderRecord[];
  readonly loading: boolean;
  readonly error: string | undefined;
  readonly openOnly: boolean;
  readonly cancellingIds: ReadonlySet<string>;
  readonly cancelAllInFlight: boolean;
  /** Outcome of the last cancel-all, held until dismissed so a partial sweep cannot scroll away. */
  readonly lastSweep: CancelAllResult | undefined;
  readonly loadedAt: number | undefined;
  /** A broker link changed since this list was fetched; the next visit refetches. See `ensureFresh`. */
  readonly stale: boolean;
}

const initialState: State = {
  orders: [],
  loading: false,
  error: undefined,
  openOnly: false,
  cancellingIds: new Set(),
  cancelAllInFlight: false,
  lastSweep: undefined,
  loadedAt: undefined,
  stale: false,
};

/**
 * The order blotter's feature state. Order updates pushed over SignalR
 * (`MarketDataService.orderUpdate`) are merged into the list as they arrive
 * — this is a "wake up sooner" optimisation, NOT a substitute for the REST
 * fetch: the socket can drop a message, so `refresh()` remains the source of
 * truth on load and after any action.
 */
export const OrdersStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(ApiService)) => ({
    refresh: rxMethod<void>(
      pipe(
        // Cleared at request start, not on the response: a link completing
        // mid-flight must survive the answer that predates it.
        tap(() => patchState(store, { loading: true, error: undefined, stale: false })),
        switchMap(() =>
          api.getOrders({ openOnly: store.openOnly() }).pipe(
            tapResponse({
              next: (orders) => patchState(store, { orders, loading: false, loadedAt: Date.now() }),
              error: (err: unknown) =>
                patchState(store, { loading: false, error: err instanceof Error ? err.message : 'Could not load orders.' }),
            }),
          ),
        ),
      ),
    ),
  })),
  // A second withMethods so these can call refresh() off the store the first one added.
  withMethods((store, api = inject(ApiService)) => ({
    setOpenOnly(openOnly: boolean): void {
      patchState(store, { openOnly });
      store.refresh();
    },

    cancel: rxMethod<string>(
      pipe(
        tap((orderId) => patchState(store, { cancellingIds: new Set([...store.cancellingIds(), orderId]) })),
        switchMap((orderId) =>
          api.cancelOrder(orderId).pipe(
            tapResponse({
              next: () => {
                const remaining = new Set(store.cancellingIds());
                remaining.delete(orderId);
                patchState(store, { cancellingIds: remaining });
                store.refresh();
              },
              error: (err: unknown) => {
                const remaining = new Set(store.cancellingIds());
                remaining.delete(orderId);
                // The failure is SURFACED, not swallowed. A cancel that
                // silently does nothing leaves the trader believing they are
                // flat when the order is still working — the single worst
                // thing this screen can get wrong.
                patchState(store, {
                  cancellingIds: remaining,
                  error: err instanceof Error ? err.message : 'The broker refused the cancel. The order is still live.',
                });
                store.refresh();
              },
            }),
          ),
        ),
      ),
    ),

    modify: rxMethod<{ orderId: string; request: ModifyOrderRequest }>(
      pipe(
        tap(() => patchState(store, { error: undefined })),
        switchMap(({ orderId, request }) =>
          api.modifyOrder(orderId, request).pipe(
            tapResponse({
              next: () => store.refresh(),
              error: (err: unknown) =>
                patchState(store, {
                  error: err instanceof Error ? err.message : 'The broker refused the amendment.',
                }),
            }),
          ),
        ),
      ),
    ),

    /**
     * The panic button.
     *
     * `isPartial` is kept on the store rather than shown and forgotten: a
     * sweep that only cleared four of nine orders must keep saying so until
     * the trader acts on it. See `CancelAllResult.isPartial`.
     */
    cancelAll: rxMethod<CancelAllRequest>(
      pipe(
        tap(() => patchState(store, { cancelAllInFlight: true, error: undefined, lastSweep: undefined })),
        switchMap((request) =>
          api.cancelAll(request).pipe(
            tapResponse({
              next: (lastSweep) => {
                patchState(store, { cancelAllInFlight: false, lastSweep });
                store.refresh();
              },
              error: (err: unknown) => {
                patchState(store, {
                  cancelAllInFlight: false,
                  error: err instanceof Error ? err.message : 'Cancel-all failed. Orders may still be live.',
                });
                store.refresh();
              },
            }),
          ),
        ),
      ),
    ),

    dismissSweep(): void {
      patchState(store, { lastSweep: undefined });
    },

    /**
     * Entry point for the blotter screen. Re-pulls only when nothing is
     * cached or a broker link has changed — navigating back to Orders with
     * an intact list costs no request, and the socket keeps it current
     * anyway (see the class doc).
     */
    ensureFresh(): void {
      if (store.loading()) {
        return;
      }
      if (store.loadedAt() === undefined || store.stale()) {
        store.refresh();
      }
    },
  })),
  withHooks({
    onInit(store) {
      const marketData = inject(MarketDataService);
      store.refresh();
      staleOnBrokerLinkChange(() => patchState(store, { stale: true }));
      // Any push over the socket is a cue to re-pull the source of truth —
      // see the class doc for why we never trust the pushed payload alone.
      let lastSeen: string | undefined;
      effect(() => {
        const pushed = marketData.orderUpdate();
        if (pushed && pushed.id !== lastSeen) {
          lastSeen = pushed.id;
          store.refresh();
        }
      });
    },
  }),
);

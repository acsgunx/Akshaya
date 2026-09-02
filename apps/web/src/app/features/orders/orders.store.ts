import { effect, inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import { MarketDataService } from '../../core/market-data.service';
import type { OrderRecord } from '../../core/models';

interface State {
  readonly orders: readonly OrderRecord[];
  readonly loading: boolean;
  readonly error: string | undefined;
  readonly openOnly: boolean;
  readonly cancellingIds: ReadonlySet<string>;
}

const initialState: State = {
  orders: [],
  loading: false,
  error: undefined,
  openOnly: false,
  cancellingIds: new Set(),
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
        tap(() => patchState(store, { loading: true, error: undefined })),
        switchMap(() =>
          api.getOrders({ openOnly: store.openOnly() }).pipe(
            tapResponse({
              next: (orders) => patchState(store, { orders, loading: false }),
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
              error: () => {
                const remaining = new Set(store.cancellingIds());
                remaining.delete(orderId);
                patchState(store, { cancellingIds: remaining });
              },
            }),
          ),
        ),
      ),
    ),
  })),
  withHooks({
    onInit(store) {
      const marketData = inject(MarketDataService);
      store.refresh();
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

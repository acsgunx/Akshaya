import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import { staleOnBrokerLinkChange } from '../../core/broker-links.store';
import type { PortfolioSnapshot } from '../../core/models';

interface State {
  readonly snapshot: PortfolioSnapshot | undefined;
  readonly loading: boolean;
  readonly error: string | undefined;
  readonly lastFetchedAt: number | undefined;
  /**
   * Set when the set of linked accounts moved under us, cleared when a fetch
   * starts. The refetch is deferred to the next visit — see `ensureFresh`.
   */
  readonly stale: boolean;
}

const initialState: State = {
  snapshot: undefined,
  loading: false,
  error: undefined,
  lastFetchedAt: undefined,
  stale: false,
};

/**
 * Feature state for the dashboard's blended, multi-currency snapshot.
 * `snapshot.isPartial` and `snapshot.failedSources` are read directly by the
 * template rather than this store trying to summarise them into a single
 * boolean — see `PortfolioSnapshot`'s own doc comment for why collapsing
 * "which broker failed and how" into a single flag is the exact failure
 * mode the backend type was designed to avoid.
 */
export const DashboardStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(ApiService)) => ({
    refresh: rxMethod<string | undefined>(
      pipe(
        // `stale` is cleared HERE and not in the response handler: a link that
        // completes while this request is in flight must leave the flag set,
        // because the answer coming back predates it.
        tap(() => patchState(store, { loading: true, error: undefined, stale: false })),
        switchMap((displayCurrency) =>
          api.getPortfolio(displayCurrency).pipe(
            tapResponse({
              next: (snapshot) => patchState(store, { snapshot, loading: false, lastFetchedAt: Date.now() }),
              error: (err: unknown) =>
                patchState(store, { loading: false, error: err instanceof Error ? err.message : 'Could not load the portfolio.' }),
            }),
          ),
        ),
      ),
    ),
  })),
  withMethods((store) => ({
    /**
     * Called by every screen that reads this snapshot, on entry. It fetches
     * only when there is nothing cached or a broker link has changed since
     * the cache was filled — plain tab-to-tab navigation costs no request,
     * which is the whole point of a root-provided store.
     */
    ensureFresh(): void {
      if (store.loading()) {
        return;
      }
      if (store.lastFetchedAt() === undefined || store.stale()) {
        store.refresh(store.snapshot()?.displayCurrency);
      }
    },
  })),
  withHooks({
    onInit(store) {
      store.refresh(undefined);
      staleOnBrokerLinkChange(() => patchState(store, { stale: true }));
    },
  }),
);

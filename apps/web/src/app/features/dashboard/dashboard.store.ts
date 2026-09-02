import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import type { PortfolioSnapshot } from '../../core/models';

interface State {
  readonly snapshot: PortfolioSnapshot | undefined;
  readonly loading: boolean;
  readonly error: string | undefined;
  readonly lastFetchedAt: number | undefined;
}

const initialState: State = {
  snapshot: undefined,
  loading: false,
  error: undefined,
  lastFetchedAt: undefined,
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
        tap(() => patchState(store, { loading: true, error: undefined })),
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
  withHooks({
    onInit(store) {
      store.refresh(undefined);
    },
  }),
);

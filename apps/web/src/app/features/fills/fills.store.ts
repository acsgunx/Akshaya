import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import { staleOnBrokerLinkChange } from '../../core/broker-links.store';
import type { TradeQuery, TradeRecord } from '../../core/models';

interface State {
  readonly trades: readonly TradeRecord[];
  readonly loading: boolean;
  readonly error: string | undefined;
  /**
   * Per-link failures. Kept SEPARATE from `error`: a run where four of five
   * accounts answered is a success with a named gap, not a failure, and
   * collapsing the two would either hide the gap or throw away the four.
   */
  readonly warnings: readonly string[];
  readonly isPartial: boolean;
  readonly query: TradeQuery;
  readonly loadedAt: number | undefined;
  /** A broker link changed since these fills were fetched; the next visit refetches. See `ensureFresh`. */
  readonly stale: boolean;
}

const initialState: State = {
  trades: [],
  loading: false,
  error: undefined,
  warnings: [],
  isPartial: false,
  query: {},
  loadedAt: undefined,
  stale: false,
};

/**
 * Executions across every linked account.
 *
 * Separate from the order blotter because it answers a different question.
 * The blotter says what you ASKED for and where it got to; this says what
 * actually happened, chunk by chunk. An order filled in three pieces at
 * three prices shows one average in the blotter and three rows here — and
 * the three rows are what reconciles against a contract note.
 *
 * There is no live socket feed here on purpose. Fills are historical facts;
 * once the exchange has stamped one it does not change, so re-pulling on
 * demand is both sufficient and cheaper than holding a subscription open.
 */
export const FillsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(ApiService)) => ({
    load: rxMethod<TradeQuery>(
      pipe(
        // Cleared at request start, not on the response: a link completing
        // mid-flight must survive the answer that predates it.
        tap((query) => patchState(store, { loading: true, error: undefined, query, stale: false })),
        switchMap((query) =>
          api.getTrades(query).pipe(
            tapResponse({
              next: (result) =>
                patchState(store, {
                  trades: result.trades,
                  warnings: result.warnings,
                  isPartial: result.isPartial,
                  loading: false,
                  loadedAt: Date.now(),
                }),
              error: (err: unknown) =>
                patchState(store, {
                  loading: false,
                  error: err instanceof Error ? err.message : 'Could not load fills.',
                }),
            }),
          ),
        ),
      ),
    ),
  })),
  withMethods((store) => ({
    setRange(from: string | undefined, to: string | undefined): void {
      store.load({ ...store.query(), from, to });
    },

    refresh(): void {
      store.load(store.query());
    },

    /**
     * Entry point for the fills screen. Only re-pulls when nothing is cached
     * or a broker link has changed: fills are historical facts, so re-fetching
     * them on every visit buys nothing and costs a fan-out across brokers.
     */
    ensureFresh(): void {
      if (store.loading()) {
        return;
      }
      if (store.loadedAt() === undefined || store.stale()) {
        // `store.load`, not the `refresh` beside it: sibling methods added in
        // the same `withMethods` block are not on `store` yet.
        store.load(store.query());
      }
    },
  })),
  withHooks({
    onInit(store) {
      store.load({});
      staleOnBrokerLinkChange(() => patchState(store, { stale: true }));
    },
  }),
);

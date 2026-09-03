import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import type { Candle, TimeFrame } from '../../core/models';

/** What one history load needs. `days` is clamped by the caller against the manifest. */
export interface HistoryRequest {
  readonly brokerLinkId: string;
  readonly instrument: string;
  readonly timeFrame: TimeFrame;
  readonly days: number;
}

interface State {
  readonly candles: readonly Candle[];
  readonly loading: boolean;
  readonly error: string | undefined;
  /** The frame the loaded bars were fetched at — the chart folds ticks against THIS, not the selection. */
  readonly loadedTimeFrame: TimeFrame | undefined;
}

const initialState: State = {
  candles: [],
  loading: false,
  error: undefined,
  loadedTimeFrame: undefined,
};

/**
 * History for ONE chart panel, provided at the component rather than the root
 * so two charts open on different instruments cannot clobber each other's
 * bars mid-fetch.
 *
 * `switchMap` is the right operator here and the choice is load-bearing: a
 * trader flipping 1m → 5m → 15m faster than the broker answers must end up
 * looking at the bars for the frame they stopped on. With `mergeMap` a slow
 * earlier response could land last and leave 1m bars under a "15m" label —
 * a chart that is silently not what it says it is.
 *
 * NOTE ON FAILURE: a failed load empties the series rather than leaving the
 * previous instrument's bars on screen under the new instrument's name. Same
 * reasoning as the order ticket's refusal to show optimistic state — stale
 * data wearing a fresh label is worse than an honest empty panel.
 */
export const ChartStore = signalStore(
  withState(initialState),
  withMethods((store, api = inject(ApiService)) => ({
    load: rxMethod<HistoryRequest>(
      pipe(
        tap(() => patchState(store, { loading: true, error: undefined })),
        switchMap((request) => {
          const to = new Date();
          const from = new Date(to.getTime() - request.days * 24 * 60 * 60 * 1000);
          return api.getHistory(request.brokerLinkId, request.instrument, request.timeFrame, from, to).pipe(
            tapResponse({
              next: (series) =>
                patchState(store, {
                  candles: series.candles,
                  loadedTimeFrame: request.timeFrame,
                  loading: false,
                }),
              error: (err: unknown) =>
                patchState(store, {
                  candles: [],
                  loadedTimeFrame: undefined,
                  loading: false,
                  error: err instanceof Error ? err.message : 'Could not load price history.',
                }),
            }),
          );
        }),
      ),
    ),
  })),
);

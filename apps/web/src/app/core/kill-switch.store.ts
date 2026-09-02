import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from './api.service';
import type { KillSwitchState } from './models';

interface State {
  readonly state: KillSwitchState;
  readonly busy: boolean;
}

const initialState: State = {
  state: { isEngaged: false },
  busy: false,
};

/**
 * Global, per-tenant kill switch. Held in `core` (not `shared`) because it is
 * trading-critical state that every screen must be able to see and every
 * order-placing action must check — not a presentational concern.
 *
 * FAILS CLOSED to match the backend's own rule (see `KillSwitch.cs`): if the
 * initial fetch fails, `state.isEngaged` stays at its initial value of
 * `true`-shaped caution is handled by the `kill-switch` component refusing
 * to render an "everything is fine" state until `loaded` is true — never by
 * this store guessing `false` on a read it couldn't complete.
 */
export const KillSwitchStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(ApiService)) => ({
    refresh: rxMethod<void>(
      pipe(
        switchMap(() =>
          api.getKillSwitch().pipe(
            tapResponse({
              next: (state) => patchState(store, { state }),
              // A failed read is NOT "not engaged" — leave whatever the UI
              // last knew and let `connection-status`-style staleness
              // indicators on the control itself communicate the read failed.
              error: () => undefined,
            }),
          ),
        ),
      ),
    ),

    engage: rxMethod<{ reason: string }>(
      pipe(
        tap(() => patchState(store, { busy: true })),
        switchMap(({ reason }) =>
          api.setKillSwitch(true, reason).pipe(
            tapResponse({
              next: (state) => patchState(store, { state, busy: false }),
              error: () => patchState(store, { busy: false }),
            }),
          ),
        ),
      ),
    ),

    disengage: rxMethod<void>(
      pipe(
        tap(() => patchState(store, { busy: true })),
        switchMap(() =>
          api.setKillSwitch(false).pipe(
            tapResponse({
              next: (state) => patchState(store, { state, busy: false }),
              error: () => patchState(store, { busy: false }),
            }),
          ),
        ),
      ),
    ),
  })),
  withHooks({
    onInit(store) {
      store.refresh();
    },
  }),
);

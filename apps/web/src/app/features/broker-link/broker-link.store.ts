import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';
import type { HttpErrorResponse } from '@angular/common/http';

import { ApiService } from '../../core/api.service';
import { toAuthStepView } from '../../core/models';
import type { ApiProblem, AuthCredentials, AuthStepView } from '../../core/models';

interface State {
  readonly step: AuthStepView | undefined;
  readonly loading: boolean;
  readonly error: ApiProblem | undefined;
  /** Opaque state echoed back on `continue` calls — the connector owns its meaning; the wizard never inspects it. */
  readonly flowState: Readonly<Record<string, string>>;
}

const initialState: State = {
  step: undefined,
  loading: false,
  error: undefined,
  flowState: {},
};

/**
 * Drives the `AuthStep` state machine for ONE link-in-progress. Provided at
 * the component level, mirroring `order-ticket.store.ts` — a second wizard
 * instance (opening a link flow for a different broker) must not share this.
 *
 * THIS STORE NEVER BRANCHES ON A BROKER. It has four transitions —
 * completed / redirect / challenge / gateway — because `AuthStepDto` has
 * four cases, full stop. A fifth broker with a login flow that still fits
 * one of these four adds nothing here at all.
 */
export const BrokerLinkStore = signalStore(
  withState(initialState),
  withMethods((store, api = inject(ApiService)) => ({
    begin: rxMethod<{ connectorId: string; credentials: AuthCredentials; nickname?: string; redirectUri?: string }>(
      pipe(
        tap(() => patchState(store, { loading: true, error: undefined })),
        switchMap(({ connectorId, credentials, nickname, redirectUri }) =>
          api.beginLink(connectorId, credentials, nickname, redirectUri).pipe(
            tapResponse({
              next: (wire) => patchState(store, { step: toAuthStepView(wire), loading: false }),
              error: (err: unknown) => patchState(store, { loading: false, error: toProblem(err) }),
            }),
          ),
        ),
      ),
    ),

    continue: rxMethod<{ linkId: string; response: string }>(
      pipe(
        tap(() => patchState(store, { loading: true, error: undefined })),
        switchMap(({ linkId, response }) =>
          api.continueLink(linkId, response, store.flowState()).pipe(
            tapResponse({
              next: (wire) => patchState(store, { step: toAuthStepView(wire), loading: false }),
              error: (err: unknown) => patchState(store, { loading: false, error: toProblem(err) }),
            }),
          ),
        ),
      ),
    ),

    rememberFlowState(partial: Readonly<Record<string, string>>): void {
      patchState(store, { flowState: { ...store.flowState(), ...partial } });
    },

    reset(): void {
      patchState(store, initialState);
    },
  })),
);

function toProblem(err: unknown): ApiProblem {
  const httpErr = err as HttpErrorResponse;
  return (httpErr?.error as ApiProblem) ?? { status: httpErr?.status ?? 0, detail: 'Could not reach the server.' };
}

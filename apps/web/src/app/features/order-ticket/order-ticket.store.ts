import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import type { HttpErrorResponse } from '@angular/common/http';
import type { ApiProblem, InstrumentDefinition, OrderActionResult, OrderEstimate, PlaceOrderRequest } from '../../core/models';

export type OrderTicketPhase = 'form' | 'reviewing' | 'submitting' | 'submitted' | 'failed';

/** What `loadInstrument` needs: the key, and the link to resolve it through. */
export interface InstrumentRequest {
  readonly brokerLinkId: string;
  readonly instrument: string;
}

interface State {
  readonly instrument: InstrumentDefinition | undefined;
  readonly instrumentLoading: boolean;
  readonly instrumentError: string | undefined;

  readonly phase: OrderTicketPhase;

  readonly estimate: OrderEstimate | undefined;
  readonly estimateLoading: boolean;

  /** Populated ONLY once the broker has acknowledged — see NO OPTIMISTIC UI note below. */
  readonly result: OrderActionResult | undefined;
  readonly submitError: ApiProblem | undefined;
}

const initialState: State = {
  instrument: undefined,
  instrumentLoading: false,
  instrumentError: undefined,
  phase: 'form',
  estimate: undefined,
  estimateLoading: false,
  result: undefined,
  submitError: undefined,
};

/**
 * Feature state for ONE order ticket instance. Provided at the component
 * level (not `providedIn: 'root'`) because a ticket's state — which
 * instrument, which draft, whether it's mid-submit — belongs to that one
 * open ticket, not the whole app; opening a second ticket for a different
 * instrument must not share or clobber the first one's in-flight submit.
 *
 * ============================================================================
 * WHY THERE IS NO OPTIMISTIC UI HERE — read this before "fixing" the delay.
 * ============================================================================
 * `submit()` moves the phase straight to `submitting` and stays there,
 * showing nothing that looks like a placed order, until `placeOrder()`
 * actually resolves with the broker's acknowledgement (or a failure). It
 * would be easy to make the ticket feel snappier by immediately showing
 * "Order placed" and reconciling in the background — but a trading order is
 * not a todo-list item. `PlaceOrderRequestDto`'s own doc comment describes
 * exactly the failure this avoids: a slow or dropped acknowledgement is
 * indistinguishable, from the optimistic UI's point of view, from a fast
 * one, and a user who believes an order is live when it was actually
 * rejected (insufficient funds, market closed, risk-gate refusal) can act on
 * that false belief — hedge against a position that doesn't exist, or skip
 * placing the SAME order elsewhere because "it's already in". The half-second
 * of a spinner is a strictly better failure mode than a confident lie.
 */
export const OrderTicketStore = signalStore(
  withState(initialState),
  withMethods((store, api = inject(ApiService)) => ({
    /**
     * Instrument details are resolved THROUGH A LINK: the resolve endpoint reads
     * the instrument master for whichever connector that link belongs to, so the
     * ticket cannot ask this question without saying which broker it means.
     */
    loadInstrument: rxMethod<InstrumentRequest>(
      pipe(
        tap(() => patchState(store, { instrumentLoading: true, instrumentError: undefined })),
        switchMap(({ brokerLinkId, instrument }) =>
          api.getInstrument(brokerLinkId, instrument).pipe(
            tapResponse({
              next: (instrument) => patchState(store, { instrument, instrumentLoading: false }),
              error: (err: unknown) =>
                patchState(store, {
                  instrumentLoading: false,
                  instrumentError: err instanceof Error ? err.message : 'Could not load instrument details.',
                }),
            }),
          ),
        ),
      ),
    ),

    requestEstimate: rxMethod<PlaceOrderRequest>(
      pipe(
        tap(() => patchState(store, { estimateLoading: true, phase: 'reviewing' })),
        switchMap((request) =>
          api.estimateOrder(request).pipe(
            tapResponse({
              next: (estimate) => patchState(store, { estimate, estimateLoading: false }),
              // The manifest says whether estimate is even offered; a failed
              // call here still lets the trader proceed to confirm without
              // a cost estimate rather than blocking the whole ticket.
              error: () => patchState(store, { estimateLoading: false, estimate: undefined }),
            }),
          ),
        ),
      ),
    ),

    backToForm(): void {
      patchState(store, { phase: 'form', estimate: undefined });
    },

    submit: rxMethod<PlaceOrderRequest>(
      pipe(
        tap(() => patchState(store, { phase: 'submitting', submitError: undefined })),
        switchMap((request) =>
          api.placeOrder(request).pipe(
            tapResponse({
              next: (result) => patchState(store, { phase: 'submitted', result }),
              error: (err: unknown) => {
                const httpErr = err as HttpErrorResponse;
                patchState(store, {
                  phase: 'failed',
                  submitError: (httpErr.error as ApiProblem) ?? { status: httpErr.status ?? 0, detail: 'Unknown error.' },
                });
              },
            }),
          ),
        ),
      ),
    ),

    resetTicket(): void {
      patchState(store, { phase: 'form', estimate: undefined, result: undefined, submitError: undefined });
    },
  })),
);

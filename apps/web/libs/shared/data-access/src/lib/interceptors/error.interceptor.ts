import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injector, inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import type { ApiProblem } from '@akshaya/shared/models';

/**
 * Surfaces the canonical RFC 7807 problem body from a failed request as a
 * toast, THEN rethrows unchanged. This interceptor never swallows an error
 * and never retries: retry policy differs by endpoint (an order placement
 * must never be blindly retried — see `PlaceOrderRequestDto`'s idempotency
 * key doc — while a quote fetch usually can be), so that decision stays with
 * the caller. This is purely "make sure a human sees what the broker said".
 */

/**
 * Set on requests that are ambient rather than user-initiated — the build-info
 * check, for example, where a failure's only answer is the missing label
 * itself and a toast would report a problem nobody can act on. The error is
 * still rethrown to the caller unchanged.
 */
export const SKIP_ERROR_TOAST = new HttpContextToken<boolean>(() => false);

export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  // `Injector`, not `MatSnackBar`: this interceptor is registered in the app
  // shell, so injecting the snack bar here would import it — and the CDK
  // overlay under it — into the initial bundle for a surface that only ever
  // appears after a request has already failed. It is fetched on the first
  // failure instead. The rethrow below stays synchronous either way, and
  // every store also keeps its own `error` state, so a screen still shows
  // what went wrong even if the toast never arrives.
  const injector = inject(Injector);

  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && !req.context.get(SKIP_ERROR_TOAST)) {
        const problem = err.error as ApiProblem | undefined;
        const message = problem?.vendorMessage
          ? `${problem.detail ?? problem.title ?? 'Request failed'} — broker said: "${problem.vendorMessage}"`
          : (problem?.detail ?? problem?.title ?? `Request failed (${err.status})`);

        void toast(injector, message);
      }
      return throwError(() => err);
    }),
  );
};

async function toast(injector: Injector, message: string): Promise<void> {
  const { MatSnackBar } = await import('@angular/material/snack-bar');
  injector.get(MatSnackBar).open(message, 'Dismiss', { duration: 8000, panelClass: ['ak-snack-error'] });
}

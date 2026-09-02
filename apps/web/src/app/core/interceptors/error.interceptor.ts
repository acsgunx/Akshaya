import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, throwError } from 'rxjs';

import type { ApiProblem } from '../models/error.model';

/**
 * Surfaces the canonical RFC 7807 problem body from a failed request as a
 * toast, THEN rethrows unchanged. This interceptor never swallows an error
 * and never retries: retry policy differs by endpoint (an order placement
 * must never be blindly retried — see `PlaceOrderRequestDto`'s idempotency
 * key doc — while a quote fetch usually can be), so that decision stays with
 * the caller. This is purely "make sure a human sees what the broker said".
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const snackBar = inject(MatSnackBar);

  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse) {
        const problem = err.error as ApiProblem | undefined;
        const message = problem?.vendorMessage
          ? `${problem.detail ?? problem.title ?? 'Request failed'} — broker said: "${problem.vendorMessage}"`
          : (problem?.detail ?? problem?.title ?? `Request failed (${err.status})`);

        snackBar.open(message, 'Dismiss', { duration: 8000, panelClass: ['ak-snack-error'] });
      }
      return throwError(() => err);
    }),
  );
};

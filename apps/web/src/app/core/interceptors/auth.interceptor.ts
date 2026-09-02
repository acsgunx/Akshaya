import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Attaches credentials (cookie-based session) to every API call. Kept as an
 * interceptor rather than sprinkled through `ApiService` so a future switch
 * to a bearer token is a one-file change.
 *
 * Deliberately does NOT attach anything broker-specific: per the platform's
 * own rule (see `AuthContext`/`AuthCredentials` on the backend), broker
 * credentials never pass through anywhere but the link wizard's one-time
 * begin/continue calls, and are never cached client-side.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  return next(req.clone({ withCredentials: true }));
};

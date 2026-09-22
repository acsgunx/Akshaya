import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { defer, finalize } from 'rxjs';

import { ActivityService } from '../activity.service';

/**
 * Set on a request whose wait is already shown right where the user is
 * looking — type-ahead search spins inside its own field. A page-wide bar
 * pulsing on every pause in typing would only pull the eye away from it.
 */
export const SKIP_ACTIVITY_BAR = new HttpContextToken<boolean>(() => false);

/**
 * Feeds every request into `ActivityService`, so the global activity bar
 * covers calls nobody remembered to wire a spinner to. `defer` so the count
 * starts at subscription (when the request is actually sent), and `finalize`
 * so it ends however the request does — response, error, or a `switchMap`
 * cancelling it mid-flight.
 */
export const activityInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.context.get(SKIP_ACTIVITY_BAR)) {
    return next(req);
  }

  const activity = inject(ActivityService);
  return defer(() => {
    const done = activity.track();
    return next(req).pipe(finalize(done));
  });
};

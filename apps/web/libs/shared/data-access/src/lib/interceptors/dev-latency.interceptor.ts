import { HttpInterceptorFn } from '@angular/common/http';
import { switchMap, timer } from 'rxjs';

/** Milliseconds to hold every API call for. Read per request, so it applies from the next call. */
const STORAGE_KEY = 'akshaya.devLatencyMs';

/**
 * DEV SERVER ONLY (registered behind `isDevMode()` in `app.config.ts`).
 *
 * Against a local API every call answers in a few milliseconds, so none of
 * the loading states in DESIGN.md "Loading and waiting" ever gets a frame on
 * screen — the activity bar alone waits 200ms before it appears. A deployed
 * app talking to real brokers waits seconds. This makes the local app wait
 * like the real one, so those states can be seen and judged:
 *
 *     localStorage.setItem('akshaya.devLatencyMs', '1500')   // then reload
 *     localStorage.removeItem('akshaya.devLatencyMs')
 *
 * Registered after `activityInterceptor`, so the activity bar counts the
 * added wait like any other.
 */
export const devLatencyInterceptor: HttpInterceptorFn = (req, next) => {
  const ms = configuredLatency();
  return ms > 0 ? timer(ms).pipe(switchMap(() => next(req))) : next(req);
};

function configuredLatency(): number {
  try {
    return Math.max(0, Number(localStorage.getItem(STORAGE_KEY)) || 0);
  } catch {
    return 0;
  }
}

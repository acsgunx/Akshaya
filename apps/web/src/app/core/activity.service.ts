import { Injectable, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { distinctUntilChanged, map, switchMap, timer } from 'rxjs';

/**
 * Below this, a wait reads as instant and a bar would only flicker. Most
 * cached-route switches and quick reads land under it and show nothing.
 */
const SHOW_DELAY_MS = 200;

/**
 * How long the bar stays after the work ends. Doubles as its minimum
 * lifetime once shown — so a 210ms request is a visible blip rather than a
 * one-frame flash — and bridges back-to-back work (a route resolves, its
 * screen immediately fetches) into one continuous bar instead of two.
 */
const LINGER_MS = 300;

/**
 * "Is the app waiting on anything right now?" — the one question the global
 * activity bar answers, from the two things that make a user wait without
 * the screen itself changing:
 *
 * - **navigation**: every route is lazy, and most sit behind an async guard,
 *   so a tapped tab does nothing visible until its chunk arrives;
 * - **HTTP**: counted by `activityInterceptor`, so no call site has to
 *   remember to report itself. A request that already has its own indicator
 *   right where the user is looking (type-ahead search) opts out with
 *   `SKIP_ACTIVITY_BAR`.
 *
 * Live prices arrive over SignalR, not HTTP, and there is no HTTP polling
 * anywhere in the app — which is what lets "any request in flight" mean
 * "the user is waiting" rather than "always".
 */
@Injectable({ providedIn: 'root' })
export class ActivityService {
  private readonly router = inject(Router);
  private readonly requests = signal(0);

  /** A navigation is resolving guards or loading a lazy route. */
  readonly navigating = computed(() => this.router.currentNavigation() !== null);

  /** Raw state — true the instant anything is in flight. Drive UI from `visible`. */
  readonly busy = computed(() => this.navigating() || this.requests() > 0);

  /** `busy`, smoothed for display: late to appear, slow to leave. See the constants above. */
  readonly visible = toSignal(
    toObservable(this.busy).pipe(
      switchMap((busy) => timer(busy ? SHOW_DELAY_MS : LINGER_MS).pipe(map(() => busy))),
      distinctUntilChanged(),
    ),
    { initialValue: false },
  );

  /**
   * Counts one unit of work in. Returns the matching "done", which is safe to
   * call more than once — `finalize` fires on complete, error AND
   * unsubscribe, and a count that drifts negative would pin the bar off.
   */
  track(): () => void {
    this.requests.update((n) => n + 1);
    let done = false;
    return () => {
      if (!done) {
        done = true;
        this.requests.update((n) => n - 1);
      }
    };
  }
}

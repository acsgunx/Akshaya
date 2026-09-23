import { Injectable, Signal, computed, signal } from '@angular/core';

/** How often the shared clock advances. One second: every threshold read off it is in whole seconds. */
export const CLOCK_TICK_MS = 1000;

/**
 * The app's one ticking "now", as a signal.
 *
 * THE BUG THIS EXISTS TO PREVENT: an age computed as `Date.now() - lastEventAt`
 * inside a `computed` is frozen, not live. A `computed` re-evaluates only when
 * one of its SIGNAL dependencies changes, and `Date.now()` is not one — so the
 * age it reports is whatever it was when the last event landed (≈0) and never
 * grows while nothing arrives. Every "is this feed still alive?" check built
 * that way therefore reports healthy precisely when the feed is dead, which is
 * the failure mode DESIGN.md says must not happen.
 *
 * The fix is to make the passage of time a signal dependency, which is what
 * `now` is. Anything measuring an age subtracts a raw arrival epoch from this,
 * rather than trusting an age handed to it by whatever recorded the event.
 *
 * ONE timer, app-wide, rather than one per component: a watchlist of thirty
 * rows, each with its own `setInterval`, is thirty wakeups a second to answer
 * the same question. A `computed` over `now` is lazy — it costs nothing until
 * something reads it — and memoises, so a boolean like `isStale` notifies the
 * view when it FLIPS, not once a second.
 */
@Injectable({ providedIn: 'root' })
export class ClockService {
  private readonly _now = signal(Date.now());

  /** Epoch ms, advancing every {@link CLOCK_TICK_MS}. */
  readonly now: Signal<number> = this._now.asReadonly();

  constructor() {
    // Zoneless-safe: writing to a signal schedules change detection through
    // Angular's own scheduler regardless of NgZone, so a bare `setInterval` is
    // sufficient — same reasoning as `VenueStateService`'s clock. Never
    // cleared, deliberately: this is a root singleton that lives as long as
    // the app does, and stopping it would silently freeze every age on screen.
    setInterval(() => this._now.set(Date.now()), CLOCK_TICK_MS);
  }

  /**
   * Milliseconds since `at`, re-read every tick; `undefined` while `at` is.
   *
   * `at` is an ARRIVAL epoch — when the thing actually reached this client —
   * not a timestamp the payload carried. See `MarketDataService.lastTickAtFor`
   * for why the distinction decides whether a live screen gets labelled stale.
   */
  ageMsSince(at: Signal<number | undefined>): Signal<number | undefined> {
    return computed(() => {
      const since = at();
      // Clamped: a clock adjustment must not report a negative age, which
      // would read as "arrived in the future" and pass every freshness check.
      return since === undefined ? undefined : Math.max(0, this.now() - since);
    });
  }

  /**
   * True once `at` is older than `thresholdMs` — and true while `at` is
   * `undefined`, because "nothing has ever arrived" is not fresh.
   */
  isOlderThan(at: Signal<number | undefined>, thresholdMs: number): Signal<boolean> {
    const age = this.ageMsSince(at);
    return computed(() => (age() ?? Number.POSITIVE_INFINITY) > thresholdMs);
  }
}

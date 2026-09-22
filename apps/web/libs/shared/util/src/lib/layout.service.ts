import { BreakpointObserver } from '@angular/cdk/layout';
import { Injectable, Signal, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

/**
 * Below this width the app switches to its compact layout: bottom tab bar
 * instead of the top nav, and card lists instead of the blotter grids.
 *
 * It is EXACTLY the complement of Tailwind's `lg` breakpoint (`--breakpoint-lg`,
 * 64rem) — the query `max-lg:` emits — so a template that branches on
 * `compact()` and a utility written as `lg:flex` / `max-lg:hidden` can never
 * disagree about which side of the line a given width is on. Change one,
 * change both.
 *
 * Why 64rem and not a phone-sized number: the desktop top bar needs roughly
 * 960px before its six links, the kill switch and the account link stop
 * overflowing, and the nine-column order blotter squeezes its instrument
 * column to nothing well before that. A tablet in portrait gets the compact
 * layout, which is what it is better served by.
 */
export const COMPACT_QUERY = '(width < 64rem)';

/**
 * Viewport-dependent layout decisions, as signals.
 *
 * Most responsiveness in this app is plain CSS (Tailwind `lg:` / `max-lg:`
 * variants) and should stay that way. This exists for the cases CSS cannot
 * express: where the compact layout is different MARKUP, not different
 * styling — a grid row versus a card, a virtual-scroll viewport versus a
 * plain list — and rendering both and hiding one would double the DOM and,
 * on the watchlist, every row's live subscription.
 */
@Injectable({ providedIn: 'root' })
export class LayoutService {
  private readonly breakpoints = inject(BreakpointObserver);

  /** True below the `lg` breakpoint — phones and portrait tablets. */
  readonly compact: Signal<boolean> = toSignal(
    this.breakpoints.observe(COMPACT_QUERY).pipe(map((state) => state.matches)),
    // Read synchronously so the first render already picks the right layout,
    // rather than painting the desktop grid on a phone for one frame.
    { initialValue: this.breakpoints.isMatched(COMPACT_QUERY) },
  );
}

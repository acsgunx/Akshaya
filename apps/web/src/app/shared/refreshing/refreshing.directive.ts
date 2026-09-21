import { Directive, input } from '@angular/core';

/**
 * Dims content that is on screen but being replaced — a refresh, a changed
 * filter, a new date range — and marks it `aria-busy`.
 *
 * Without it, a screen only showed that it was loading when it had nothing
 * to show: the first load got a spinner, and every later one left the old
 * rows up, full strength, with no sign anything was happening. After
 * switching the order blotter to "Open only" that is actively wrong — for
 * the length of the request, the list does not match the filter above it.
 *
 * Dimmed, not blanked: the old rows keep the user's place and stay readable
 * (DESIGN.md, "Loading and waiting"). Dimming waits 150ms, so a fast refresh
 * never flickers; brightening back is immediate, because fresh numbers
 * should look fresh the moment they land.
 *
 * Put it on the elements that hold the figures or rows, inside the branch
 * that only renders when there is data — the empty case is
 * `<ak-loading-state>`'s job, and a dimmed spinner helps nobody. There it can
 * simply be bound to the store's `loading()`.
 */
@Directive({
  selector: '[akRefreshing]',
  standalone: true,
  host: {
    class: 'transition-opacity duration-200',
    '[class.opacity-55]': 'akRefreshing()',
    '[class.delay-150]': 'akRefreshing()',
    '[attr.aria-busy]': 'akRefreshing() || null',
  },
})
export class RefreshingDirective {
  readonly akRefreshing = input(false);
}

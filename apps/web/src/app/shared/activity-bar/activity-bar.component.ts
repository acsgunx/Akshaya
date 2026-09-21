import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { ActivityService } from '../../core/activity.service';

/**
 * A 2px indeterminate bar along the top edge of the viewport while the app is
 * waiting on the network or a route — see `ActivityService` for what counts.
 *
 * It is the app-wide answer to "did my tap do anything?", and deliberately
 * the quietest indicator in the app: it never covers content, never moves
 * layout, and never replaces the local indicator a screen owes its own data
 * (`<ak-loading-state>`, a spinning refresh icon, a spinner in a button).
 *
 * Indeterminate rather than a creeping percentage because nothing here knows
 * how long a broker will take, and a bar that fills to 90% and then sits
 * there is a number presented with confidence it has not earned — the same
 * thing DESIGN.md refuses to do with prices.
 *
 * Plain CSS rather than `mat-progress-bar`: this sits in the shell, so it is
 * in the initial bundle, and one keyframe is all it needs.
 */
@Component({
  selector: 'ak-activity-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (activity.visible()) {
      <!-- Below the notch on a phone in landscape; 0 everywhere else. -->
      <div
        class="pointer-events-none fixed inset-x-0 top-[env(safe-area-inset-top)] z-30 h-0.5 overflow-hidden bg-brand/20"
        role="progressbar"
        aria-label="Loading"
      >
        <div class="h-full w-2/5 animate-indeterminate bg-brand motion-reduce:w-full motion-reduce:animate-none"></div>
      </div>
    }
  `,
})
export class ActivityBarComponent {
  protected readonly activity = inject(ActivityService);
}

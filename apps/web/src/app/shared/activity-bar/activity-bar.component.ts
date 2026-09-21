import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { ActivityService } from '../../core/activity.service';

/**
 * A 3px indeterminate bar shown while the app is waiting on the network or a
 * route — see `ActivityService` for what counts.
 *
 * It is the app-wide answer to "did my tap do anything?". It never covers
 * content, never moves layout, and never replaces the local indicator a
 * screen owes its own data (`<ak-loading-state>`, `akRefreshing`,
 * `<ak-refresh-button>`, a spinner in a button).
 *
 * WHERE IT SITS is the parent's call, via classes on the host: the shell
 * lays it over the header's bottom border — directly above the content that
 * is loading, which is Material's placement for a page-level progress bar,
 * and where the eye already is. On the sign-in screens, which have no
 * header, it is pinned to the top of the viewport. At 2px on the viewport's
 * top edge it was too easy to miss against a dark header.
 *
 * Indeterminate rather than a creeping percentage because nothing here knows
 * how long a broker will take, and a bar that fills to 90% and then sits
 * there claims knowledge it does not have — the same thing DESIGN.md refuses
 * to do with prices.
 *
 * Plain CSS rather than `mat-progress-bar`: this is in the shell, so it is in
 * the initial bundle, and one keyframe is all it needs.
 */
@Component({
  selector: 'ak-activity-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pointer-events-none block' },
  template: `
    @if (activity.visible()) {
      <div class="h-[3px] overflow-hidden bg-brand/25" role="progressbar" aria-label="Loading">
        <div class="h-full w-2/5 animate-indeterminate bg-brand motion-reduce:w-full motion-reduce:animate-none"></div>
      </div>
    }
  `,
})
export class ActivityBarComponent {
  protected readonly activity = inject(ActivityService);
}

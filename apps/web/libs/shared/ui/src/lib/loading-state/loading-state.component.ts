import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, signal } from '@angular/core';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

/**
 * Past this, a user starts to wonder whether the app has hung. A broker API
 * that answers in 400ms on a good day can take tens of seconds on a bad one,
 * and a spinner that looks the same at 2s and at 40s says nothing about which
 * kind of day it is.
 */
const SLOW_AFTER_MS = 8000;

/**
 * The one "waiting for this screen's data" surface — the counterpart of
 * `<ak-empty-state>` — so every screen says it the same way.
 *
 * - Centred, with the empty state's footprint, because that is usually what
 *   replaces it. A spinner tucked into the top-left corner that turns into a
 *   message in the middle of the page makes the answer jump, and is easy to
 *   miss while it is up.
 * - `role="status"`, so a screen reader hears what is loading, not silence.
 *   The spinner itself is hidden from it: "progress bar, busy" says less
 *   than the label next to it.
 * - Past `SLOW_AFTER_MS` it adds a line saying so. Inside the live region,
 *   so that is announced too.
 *
 * Use it for a screen or card with NOTHING to show yet. Data already on
 * screen that is being replaced is `akRefreshing`'s job; a busy control (a
 * submit, a cancel) carries its own spinner and an "-ing" label.
 */
@Component({
  selector: 'ak-loading-state',
  standalone: true,
  imports: [MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="flex items-center text-text-secondary"
      [class]="compact() ? 'gap-2 py-3' : 'flex-col justify-center gap-3 px-6 py-12 text-center'"
      role="status"
    >
      <mat-spinner class="shrink-0" [diameter]="compact() ? 20 : 32" aria-hidden="true" />
      <div class="flex min-w-0 flex-col gap-1">
        <span [class]="compact() ? '' : 'text-sm font-medium text-text-primary'">{{ label() }}</span>
        @if (slow()) {
          <span class="max-w-[40ch] text-xs text-text-tertiary">
            Still waiting for a reply — this is taking longer than usual.
          </span>
        }
      </div>
    </div>
  `,
})
export class LoadingStateComponent {
  /** What is loading, as a sentence: "Loading holdings…". */
  readonly label = input.required<string>();
  /** Inline and tighter, for use inside a card rather than as a page's whole body. */
  readonly compact = input(false);

  protected readonly slow = signal(false);

  constructor() {
    const timer = setTimeout(() => this.slow.set(true), SLOW_AFTER_MS);
    inject(DestroyRef).onDestroy(() => clearTimeout(timer));
  }
}

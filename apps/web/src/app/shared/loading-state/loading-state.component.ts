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
 * - `role="status"`, so a screen reader hears what is loading, not silence.
 *   The spinner itself is hidden from it: "progress bar, busy" says less
 *   than the label next to it.
 * - Past `SLOW_AFTER_MS` it adds a second line saying so. Inside the live
 *   region, so that is announced too.
 *
 * Use it for what fills a screen or a card. A control that is busy (a
 * submit, a cancel) carries its own spinner and an "-ing" label instead.
 */
@Component({
  selector: 'ak-loading-state',
  standalone: true,
  imports: [MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex items-center text-text-secondary" [class]="compact() ? 'gap-2 py-3' : 'gap-2.5 p-8'" role="status">
      <mat-spinner class="shrink-0" [diameter]="compact() ? 20 : 28" aria-hidden="true" />
      <div class="flex min-w-0 flex-col gap-0.5">
        <span>{{ label() }}</span>
        @if (slow()) {
          <span class="text-xs text-text-tertiary">Still waiting for a reply — this is taking longer than usual.</span>
        }
      </div>
    </div>
  `,
})
export class LoadingStateComponent {
  /** What is loading, as a sentence: "Loading holdings…". */
  readonly label = input.required<string>();
  /** Tighter, for use inside a card rather than as a page's whole body. */
  readonly compact = input(false);

  protected readonly slow = signal(false);

  constructor() {
    const timer = setTimeout(() => this.slow.set(true), SLOW_AFTER_MS);
    inject(DestroyRef).onDestroy(() => clearTimeout(timer));
  }
}

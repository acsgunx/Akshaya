import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * Sits above any table/chart whose feed has gone quiet. States EXPLICITLY
 * how long ago the last good data arrived — never a bare "stale" label —
 * because "stale since when" is the fact that tells a trader whether to
 * trust the numbers below for a few more seconds or to stop looking at them
 * entirely.
 */
@Component({
  selector: 'ak-stale-banner',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <div class="ak-banner ak-banner--warn" role="alert">
        <mat-icon class="ak-i-sm" aria-hidden="true">warning</mat-icon>
        <span>{{ message() }}</span>
      </div>
    }
  `,
  // Entirely the `.ak-banner--warn` primitive; the component contributes the wording,
  // not the styling.
  styles: `
    :host {
      display: block;
    }
  `,
})
export class StaleBannerComponent {
  /** Epoch ms of the last known-good update. */
  readonly lastUpdatedAt = input<number | undefined>(undefined);
  readonly thresholdMs = input(10_000);
  readonly label = input('data');

  private readonly now = signal(Date.now());

  constructor() {
    setInterval(() => this.now.set(Date.now()), 1000);
  }

  private readonly ageMs = computed(() => {
    const at = this.lastUpdatedAt();
    return at === undefined ? undefined : this.now() - at;
  });

  readonly visible = computed(() => {
    const age = this.ageMs();
    return age === undefined || age > this.thresholdMs();
  });

  readonly message = computed(() => {
    const age = this.ageMs();
    if (age === undefined) {
      return `No ${this.label()} received yet.`;
    }
    const secs = Math.floor(age / 1000);
    if (secs < 60) {
      return `${this.label()} last updated ${secs}s ago — this view may be out of date.`;
    }
    const mins = Math.floor(secs / 60);
    return `${this.label()} last updated ${mins}m ago — this view may be out of date.`;
  });
}

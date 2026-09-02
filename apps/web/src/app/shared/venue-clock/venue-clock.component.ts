import { ChangeDetectionStrategy, Component, Signal, computed, inject, input } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';

import { VenueStateService, VenueSessionState } from '../../core/venue-state.service';
import type { VenueMic } from '../../core/models';

/** One venue's local clock + open/closed badge — used in the dashboard's per-venue strip and the order ticket header. */
@Component({
  selector: 'ak-venue-clock',
  standalone: true,
  imports: [MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="ak-venue-clock" [matTooltip]="state().timeZone" role="status">
      <span class="ak-venue-mic">{{ mic() }}</span>
      <span class="ak-venue-time ak-num">{{ state().localTime }}</span>
      <span class="ak-venue-badge" [class]="'ak-venue-badge--' + state().status">{{ state().label }}</span>
    </span>
  `,
  styles: `
    .ak-venue-clock {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      color: var(--ak-text-secondary);
    }
    .ak-venue-mic {
      font-weight: 600;
      color: var(--ak-text-primary);
    }
    .ak-venue-time {
      font-family: var(--ak-font-mono);
      font-variant-numeric: tabular-nums;
    }
    .ak-venue-badge {
      padding: 1px 6px;
      border-radius: var(--ak-radius-full);
      font-size: 10px;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.02em;
    }
    .ak-venue-badge--open {
      background: color-mix(in srgb, var(--ak-success) 20%, transparent);
      color: var(--ak-success);
    }
    .ak-venue-badge--preOpen {
      background: color-mix(in srgb, var(--ak-warning) 20%, transparent);
      color: var(--ak-warning);
    }
    .ak-venue-badge--closed,
    .ak-venue-badge--afterHours {
      background: var(--ak-surface-3);
      color: var(--ak-text-tertiary);
    }
    .ak-venue-badge--unknown {
      background: var(--ak-surface-3);
      color: var(--ak-text-disabled);
    }
  `,
})
export class VenueClockComponent {
  private readonly venueState = inject(VenueStateService);
  // Cache one underlying signal PER MIC and reuse it across recomputations —
  // `VenueStateService.stateFor` builds a fresh `computed()` on every call,
  // so calling it inline inside our own `computed()` on every tick would
  // create a new (initially-unread) computed each time and silently stop
  // tracking `now` ticking on the service side after the first read.
  private readonly perMic = new Map<VenueMic, Signal<VenueSessionState>>();

  readonly mic = input.required<VenueMic>();

  readonly state: Signal<VenueSessionState> = computed(() => {
    const mic = this.mic();
    let sig = this.perMic.get(mic);
    if (!sig) {
      sig = this.venueState.stateFor(mic);
      this.perMic.set(mic, sig);
    }
    return sig();
  });
}

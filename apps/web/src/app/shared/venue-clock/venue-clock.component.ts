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
      <span class="ak-badge ak-venue-badge" [class]="badgeClass()">{{ state().label }}</span>
    </span>
  `,
  // The open/closed pill is the shared `.ak-badge` primitive; only the two "live" tints
  // and the monospace clock are specific to this component.
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
    }
    .ak-venue-badge {
      padding: 1px 6px;
      font-size: 10px;
      text-transform: uppercase;
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

  /** Open is a success tint, pre-open a warning one; closed/after-hours/unknown stay neutral. */
  readonly badgeClass = computed(() => {
    switch (this.state().status) {
      case 'open':
        return 'ak-badge--ok';
      case 'preOpen':
        return 'ak-badge--warn';
      default:
        return '';
    }
  });

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

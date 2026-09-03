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
    <span class="inline-flex items-center gap-1.5 text-xs text-text-secondary" [matTooltip]="state().timeZone" role="status">
      <span class="font-semibold text-text-primary">{{ mic() }}</span>
      <span class="font-mono tabular-nums">{{ state().localTime }}</span>
      <span
        class="inline-flex shrink-0 items-center rounded-full px-1.5 py-px text-[10px] font-semibold
               uppercase whitespace-nowrap"
        [class]="badgeClass()"
        >{{ state().label }}</span
      >
    </span>
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
        return 'bg-success/20 text-success';
      case 'preOpen':
        return 'bg-warning/20 text-warning';
      default:
        return 'bg-surface-3 text-text-secondary';
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

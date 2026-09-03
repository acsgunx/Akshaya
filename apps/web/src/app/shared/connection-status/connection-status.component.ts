import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';

import type { StreamState } from '../../core/models/trading.model';

export type ConnectionBadgeStatus = 'live' | 'degraded' | 'stale' | 'disconnected';

/**
 * Per-connector connection state, session-expiry countdown and (for
 * gateway-hosted brokers) gateway health, as ONE small badge reused wherever
 * a connector's liveness matters: watchlist rows, the order-ticket header,
 * the dashboard's per-venue strip, the connectors catalogue.
 *
 * DELIBERATELY four states, not two. "Degraded" (connected but behind or
 * partially subscribed) and "stale" (no data within the expected cadence)
 * are surfaced distinctly from a clean "live" — see DESIGN.md "showing
 * degraded and stale state" for why collapsing these to a binary dot is the
 * failure mode this component exists to prevent.
 */
@Component({
  selector: 'ak-connection-status',
  standalone: true,
  imports: [MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      class="inline-flex items-center gap-1.5 text-xs whitespace-nowrap text-text-secondary"
      [matTooltip]="tooltip()"
      role="status"
      [attr.aria-label]="label() + ': ' + tooltip()"
    >
      <!--
        The four states map straight onto the semantic tokens they mean —
        success/warning/danger — with no intermediate alias layer to fall out
        of step with them. "Stale" is deliberately the neutral tertiary dot:
        it is not an error, it is connected-but-nothing-arriving.
      -->
      <span class="size-2 shrink-0 rounded-full" [class]="dotClass()" aria-hidden="true"></span>
      <span>{{ label() }}</span>
      @if (sessionCountdownLabel(); as countdown) {
        <span class="text-warning tabular-nums">· {{ countdown }}</span>
      }
    </span>
  `,
})
export class ConnectionStatusComponent {
  readonly label = input.required<string>();
  readonly streamState = input<StreamState>('disconnected');
  /** True marks the feed connected-but-behind — independent of `streamState`, see class doc. */
  readonly isDataStale = input(false);
  readonly sessionExpiresAt = input<string | undefined>(undefined);
  readonly gatewayRunning = input(true);
  /** Minutes before expiry at which the countdown starts showing — never surprise a trader mid-order. */
  readonly warnWithinMinutes = input(15);

  private readonly now = signal(Date.now());

  constructor() {
    setInterval(() => this.now.set(Date.now()), 1000);
  }

  /**
   * Dot tint per state. The `live` ring is a halo of its own colour, which is
   * what makes a healthy feed read as healthy at a glance on a dense row
   * rather than as just another coloured pixel.
   */
  readonly dotClass = computed(() => {
    switch (this.status()) {
      case 'live':
        return 'bg-success ring-2 ring-success/25';
      case 'degraded':
        return 'bg-warning';
      case 'disconnected':
        return 'bg-danger';
      case 'stale':
        return 'bg-text-tertiary';
    }
  });

  readonly status = computed<ConnectionBadgeStatus>(() => {
    if (this.streamState() === 'disconnected' || !this.gatewayRunning()) {
      return 'disconnected';
    }
    if (this.isDataStale()) {
      return 'stale';
    }
    if (this.streamState() === 'degraded' || this.streamState() === 'reconnecting') {
      return 'degraded';
    }
    return 'live';
  });

  readonly sessionCountdownLabel = computed<string | undefined>(() => {
    const expiresAt = this.sessionExpiresAt();
    if (!expiresAt) {
      return undefined;
    }
    const msLeft = new Date(expiresAt).getTime() - this.now();
    const minsLeft = msLeft / 60_000;
    if (minsLeft > this.warnWithinMinutes()) {
      return undefined;
    }
    if (msLeft <= 0) {
      return 'session expired';
    }
    const mins = Math.floor(minsLeft);
    const secs = Math.floor((msLeft % 60_000) / 1000);
    return `session expires in ${mins}:${secs.toString().padStart(2, '0')}`;
  });

  readonly tooltip = computed(() => {
    if (!this.gatewayRunning()) {
      return 'This broker needs a local gateway process and it is not running.';
    }
    switch (this.status()) {
      case 'live':
        return 'Connected and receiving live data.';
      case 'degraded':
        return 'Connected but behind, or reconnecting.';
      case 'stale':
        return 'Connected, but no fresh data has arrived recently.';
      case 'disconnected':
        return 'Not connected.';
    }
  });
}

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
      class="ak-conn"
      [class]="'ak-conn--' + status()"
      [matTooltip]="tooltip()"
      role="status"
      [attr.aria-label]="label() + ': ' + tooltip()"
    >
      <span class="ak-conn-dot" aria-hidden="true"></span>
      <span class="ak-conn-label">{{ label() }}</span>
      @if (sessionCountdownLabel(); as countdown) {
        <span class="ak-conn-countdown">· {{ countdown }}</span>
      }
    </span>
  `,
  // The four states map straight onto the semantic tokens they mean — there is no
  // separate `--ak-state-*` alias layer to keep in step with them.
  styles: `
    .ak-conn {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      color: var(--ak-text-secondary);
      white-space: nowrap;
    }
    .ak-conn-dot {
      flex: none;
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--dot, var(--ak-text-tertiary));
    }
    .ak-conn--live {
      --dot: var(--ak-success);
    }
    .ak-conn--live .ak-conn-dot {
      box-shadow: 0 0 0 2px color-mix(in srgb, var(--ak-success) 25%, transparent);
    }
    .ak-conn--degraded {
      --dot: var(--ak-warning);
    }
    .ak-conn--disconnected {
      --dot: var(--ak-danger);
    }
    .ak-conn-countdown {
      font-variant-numeric: tabular-nums;
      color: var(--ak-warning);
    }
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

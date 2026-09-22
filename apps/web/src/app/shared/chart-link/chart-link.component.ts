import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { BrokerLinksStore } from '../../core/broker-links.store';
import { ConnectorStore } from '../../core/connector.store';
import type { InstrumentKey } from '../../core/models';
import { formatInstrumentLabel } from '../../core/models';

/**
 * "Open the chart for this instrument", from any screen that lists one — the
 * positions and holdings blotters, orders, fills, the dashboard summary.
 *
 * UNLIKE EXIT OR SELL, THIS IS A ROW ACTION, NOT A LEG ACTION. A chart reads
 * market data; it does not need the one account that holds the stock, only
 * one that can serve its history. So it takes every account the row knows
 * about and picks the first whose manifest declares `marketData.historical`,
 * falling back to the first account so the chart screen can say plainly that
 * this broker serves no history.
 *
 * It never reaches for a linked account the row does NOT name, even one that
 * could chart the venue. The chart's Trade button routes through the same
 * link, and a ticket that opens against an account unrelated to the position
 * the user started from is a mis-route waiting to happen.
 *
 * `icon` is the 32px button for desktop grids, the same glyph the watchlist
 * uses; `button` is the labelled one for compact layouts, because a finger
 * cannot hover a tooltip.
 */
@Component({
  selector: 'ak-chart-link',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex' },
  template: `
    @if (brokerLinkId(); as linkId) {
      @if (variant() === 'button') {
        <a mat-button [routerLink]="['/chart', linkId, instrument()]" [attr.aria-label]="'Open chart for ' + label()">
          <mat-icon aria-hidden="true">candlestick_chart</mat-icon>
          Chart
        </a>
      } @else {
        <a
          class="ak-iconbtn ak-focus-halo size-8 rounded-sm pointer-coarse:size-11"
          [routerLink]="['/chart', linkId, instrument()]"
          [attr.aria-label]="'Open chart for ' + label()"
          matTooltip="Chart"
        >
          <mat-icon class="ak-i-sm" aria-hidden="true">candlestick_chart</mat-icon>
        </a>
      }
    }
  `,
})
export class ChartLinkComponent {
  private readonly brokerLinksStore = inject(BrokerLinksStore);
  private readonly connectorStore = inject(ConnectorStore);

  readonly instrument = input.required<InstrumentKey>();

  /**
   * The accounts this row knows the instrument through, in preference order —
   * a blended row's legs, or `[order]` / `[trade]` for a single record.
   */
  readonly accounts = input.required<readonly { readonly brokerLinkId: string }[]>();

  readonly variant = input<'icon' | 'button'>('icon');

  protected readonly label = computed(() => formatInstrumentLabel(this.instrument()));

  protected readonly brokerLinkId = computed(() => {
    const ids = this.accounts().map((account) => account.brokerLinkId);
    return ids.find((id) => this.servesHistory(id)) ?? ids[0];
  });

  /** Resolved through the link, never the leg's own `connectorId` — the link is what names the account. */
  private servesHistory(brokerLinkId: string): boolean {
    const connectorId = this.brokerLinksStore.linkFor(brokerLinkId)?.connectorId;
    return !!connectorId && this.connectorStore.manifestFor(connectorId)?.marketData.historical === true;
  }
}

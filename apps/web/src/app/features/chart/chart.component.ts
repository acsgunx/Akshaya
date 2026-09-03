import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RouterLink } from '@angular/router';

import { BrokerLinksStore } from '../../core/broker-links.store';
import { ConnectorStore } from '../../core/connector.store';
import { timeFrameLabel } from '../../core/labels';
import { MarketDataService } from '../../core/market-data.service';
import { MoneyPipe } from '../../core/money.pipe';
import type { InstrumentKey, TimeFrame } from '../../core/models';
import { formatInstrumentLabel } from '../../core/models';
import { ConnectionStatusComponent } from '../../shared/connection-status/connection-status.component';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { PriceChartComponent } from '../../shared/price-chart/price-chart.component';
import { StaleBannerComponent } from '../../shared/stale-banner/stale-banner.component';
import { ChartStore } from './chart.store';

/**
 * The chart screen. Like the order ticket, it is told a LINK and an
 * instrument and asks that link's manifest what it may offer:
 *
 * - `marketData.historical` decides whether this screen exists at all for
 *   this broker. A connector that serves no history gets an honest empty
 *   state, not a blank chart that looks broken.
 * - `marketData.historicalTimeFrames` IS the timeframe toggle. There is no
 *   hardcoded 1m/5m/1D list anywhere below — a broker offering only daily
 *   bars renders one button.
 * - `marketData.historyDays` caps the lookback, so we never ask a broker for
 *   a window it will reject and then show the user its error.
 *
 * If you are about to add `if (connectorId === '...')` here: see the same
 * note on `order-ticket.component.ts`. The fix is a manifest field.
 */
@Component({
  selector: 'ak-chart',
  standalone: true,
  imports: [
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    RouterLink,
    MoneyPipe,
    ConnectionStatusComponent,
    EmptyStateComponent,
    PriceChartComponent,
    StaleBannerComponent,
  ],
  providers: [ChartStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chart.component.html',
  styleUrl: './chart.component.scss',
})
export class ChartComponent {
  private readonly brokerLinksStore = inject(BrokerLinksStore);
  private readonly connectorStore = inject(ConnectorStore);
  private readonly marketData = inject(MarketDataService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly store = inject(ChartStore);

  // Route-bound (`/chart/:brokerLinkId/:instrument`), same contract as the
  // order ticket: a LINKED ACCOUNT id, not a connector id.
  readonly brokerLinkId = input.required<string>();
  readonly instrument = input.required<InstrumentKey>();

  protected readonly link = computed(() => this.brokerLinksStore.linkFor(this.brokerLinkId()));
  protected readonly manifest = computed(() => {
    const connectorId = this.link()?.connectorId;
    return connectorId ? this.connectorStore.manifestFor(connectorId) : undefined;
  });

  protected readonly instrumentLabel = computed(() => formatInstrumentLabel(this.instrument()));
  protected readonly supportsHistory = computed(() => this.manifest()?.marketData.historical === true);
  protected readonly timeFrames = computed<readonly TimeFrame[]>(
    () => this.manifest()?.marketData.historicalTimeFrames ?? [],
  );

  /** `undefined` until the manifest arrives and picks the broker's first offered frame. */
  private readonly chosenTimeFrame = signal<TimeFrame | undefined>(undefined);
  protected readonly timeFrame = computed(() => this.chosenTimeFrame() ?? this.timeFrames()[0]);

  protected readonly quote = computed(() => this.marketData.tickFor(this.instrument())());
  protected readonly connectionState = this.marketData.connectionState;
  protected readonly lastTickAgeMs = computed(() => this.marketData.ageMsFor(this.instrument())());
  protected readonly isFeedStale = computed(() => (this.lastTickAgeMs() ?? Number.POSITIVE_INFINITY) > 15_000);
  protected readonly lastUpdatedAt = computed(() => {
    const age = this.lastTickAgeMs();
    return age === undefined ? undefined : Date.now() - age;
  });

  protected readonly label = timeFrameLabel;

  constructor() {
    // Live prices for the forming bar. Refcounted in `MarketDataService`, so
    // opening a chart on an instrument already in the watchlist costs no
    // extra broker subscription — but the release is still ours to call.
    effect(() => {
      const brokerLinkId = this.brokerLinkId();
      const instrument = this.instrument();
      if (!brokerLinkId || !instrument) {
        return;
      }
      const unsubscribe = this.marketData.subscribe(brokerLinkId, instrument);
      this.destroyRef.onDestroy(unsubscribe);
    });

    // Backfill. Re-runs on instrument, link or timeframe change; `switchMap`
    // in the store makes the last selection the one that wins.
    effect(() => {
      const frame = this.timeFrame();
      const manifest = this.manifest();
      if (!frame || !manifest?.marketData.historical) {
        return;
      }
      this.store.load({
        brokerLinkId: this.brokerLinkId(),
        instrument: this.instrument(),
        timeFrame: frame,
        days: this.lookbackDays(frame, manifest.marketData.historyDays),
      });
    });
  }

  protected selectTimeFrame(frame: TimeFrame): void {
    this.chosenTimeFrame.set(frame);
  }

  /**
   * How far back to ask for, per frame. A minute chart wants a few sessions,
   * a monthly chart wants years — one fixed window would either starve the
   * long frames or ask for a million one-minute bars nobody will scroll to.
   * Whatever this picks is then clamped to the broker's own declared
   * retention, because asking beyond it is a guaranteed error response.
   */
  private lookbackDays(frame: TimeFrame, brokerMaxDays: number | undefined): number {
    const wanted =
      frame === 'oneMinute' || frame === 'threeMinutes'
        ? 5
        : frame === 'fiveMinutes' || frame === 'fifteenMinutes'
          ? 30
          : frame === 'thirtyMinutes' || frame === 'oneHour'
            ? 90
            : frame === 'oneDay'
              ? 730
              : 1825;
    return brokerMaxDays === undefined ? wanted : Math.min(wanted, brokerMaxDays);
  }
}

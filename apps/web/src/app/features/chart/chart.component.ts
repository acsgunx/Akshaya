import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { catchError, map, of, startWith, switchMap, timer } from 'rxjs';

import { ApiService } from '../../core/api.service';
import { BrokerLinksStore } from '../../core/broker-links.store';
import { ConnectorStore } from '../../core/connector.store';
import { timeFrameLabel } from '../../core/labels';
import { MarketDataService } from '../../core/market-data.service';
import { MoneyPipe } from '../../core/money.pipe';
import type { InstrumentDefinition, InstrumentKey, TimeFrame } from '../../core/models';
import { formatInstrumentLabel, parseInstrumentKey } from '../../core/models';
import { venueTimeZone } from '../../core/venue-state.service';
import { ConnectionStatusComponent } from '../../shared/connection-status/connection-status.component';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { LoadingStateComponent } from '../../shared/loading-state/loading-state.component';
import { PriceChartComponent } from '../../shared/price-chart/price-chart.component';
import { StaleBannerComponent } from '../../shared/stale-banner/stale-banner.component';
import { type ChartBar } from '../../shared/price-chart/candle-bucket';
import { DRAWING_TOOLS, type DrawingTool } from '../../shared/price-chart/chart-drawings';
import { CHART_TYPES, STUDIES, type ChartType, type StudyId } from '../../shared/price-chart/chart-studies';
import { WatchlistStore } from '../watchlist/watchlist.store';
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
    MatAutocompleteModule,
    MatFormFieldModule,
    MatInputModule,
    MatMenuModule,
    MatSliderModule,
    MatTooltipModule,
    ReactiveFormsModule,
    DecimalPipe,
    RouterLink,
    MoneyPipe,
    ConnectionStatusComponent,
    EmptyStateComponent,
    LoadingStateComponent,
    PriceChartComponent,
    StaleBannerComponent,
  ],
  providers: [ChartStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chart.component.html',
  styleUrl: './chart.component.scss',
  host: { '(keydown)': 'onKeydown($event)' },
})
export class ChartComponent {
  private readonly brokerLinksStore = inject(BrokerLinksStore);
  private readonly connectorStore = inject(ConnectorStore);
  private readonly marketData = inject(MarketDataService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly store = inject(ChartStore);
  protected readonly watchlist = inject(WatchlistStore);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  protected readonly chart = viewChild(PriceChartComponent);
  protected readonly chartTypes = CHART_TYPES;
  protected readonly studyOptions = STUDIES;
  protected readonly drawingTools = DRAWING_TOOLS;
  protected readonly chartType = signal<ChartType>('candles');
  protected readonly selectedStudies = signal<readonly StudyId[]>(['stochRsi']);
  protected readonly showVolume = signal(true);
  protected readonly showGrid = signal(true);
  protected readonly showSidebar = signal(true);
  protected readonly focusMode = signal(false);
  protected readonly scaleMode = signal<'normal' | 'log' | 'percentage'>('normal');
  protected readonly drawingTool = signal<DrawingTool>('cursor');
  protected readonly drawingsVisible = signal(true);
  protected readonly drawingState = signal({ undo: false, redo: false, count: 0 });
  protected readonly hoveredBar = signal<ChartBar | undefined>(undefined);
  protected readonly status = signal('Scroll to zoom · drag to pan · double-click an axis to reset');
  protected readonly replayIndex = signal<number | undefined>(undefined);
  protected readonly playing = signal(false);
  protected readonly searchControl = new FormControl('', { nonNullable: true });
  private readonly searchText = toSignal(this.searchControl.valueChanges.pipe(
    map((value) => typeof value === 'string' ? value : ''), startWith(''),
  ), { initialValue: '' });
  private readonly clock = signal(Date.now());
  private readonly reload = signal(0);
  protected readonly rangeOptions = [
    { label: '1D', days: 1 }, { label: '5D', days: 5 }, { label: '1M', days: 30 },
    { label: '3M', days: 90 }, { label: '6M', days: 180 }, { label: '1Y', days: 365 },
  ];

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
  protected readonly timeFrame = computed(() => {
    const chosen = this.chosenTimeFrame();
    return chosen && this.timeFrames().includes(chosen) ? chosen : this.timeFrames()[0];
  });

  protected readonly quote = computed(() => this.marketData.tickFor(this.instrument())());
  protected readonly connectionState = this.marketData.connectionState;
  protected readonly lastTickAgeMs = computed(() => {
    const timestamp = this.quote()?.timestamp;
    const at = timestamp ? Date.parse(timestamp) : NaN;
    return Number.isFinite(at) ? Math.max(0, this.clock() - at) : undefined;
  });
  protected readonly isFeedStale = computed(() => (this.lastTickAgeMs() ?? Number.POSITIVE_INFINITY) > 15_000);
  protected readonly lastUpdatedAt = computed(() => {
    const age = this.lastTickAgeMs();
    return age === undefined ? undefined : Date.now() - age;
  });

  /** The venue's own zone, so an NSE chart reads 09:15-15:30 wherever the trader is. */
  protected readonly timeZone = computed(() => {
    const venue = parseInstrumentKey(this.instrument())?.venue;
    return venue ? venueTimeZone(venue) : undefined;
  });

  protected readonly label = timeFrameLabel;
  protected readonly formatLabel = formatInstrumentLabel;
  protected readonly venue = computed(() => parseInstrumentKey(this.instrument())?.venue ?? '');
  protected readonly isReplay = computed(() => this.replayIndex() !== undefined);
  protected readonly history = computed(() => {
    const candles = new Map(this.store.candles().filter((bar) => Number.isFinite(Date.parse(bar.openTime))
      && [bar.open, bar.high, bar.low, bar.close].every(Number.isFinite)
      && bar.high >= Math.max(bar.open, bar.close, bar.low) && bar.low <= Math.min(bar.open, bar.close))
      .map((bar) => [Date.parse(bar.openTime), bar]));
    return [...candles].sort(([a], [b]) => a - b).map(([, bar]) => bar);
  });
  protected readonly displayedCandles = computed(() => this.history().slice(0, this.replayIndex()));
  protected readonly ready = computed(() => !this.store.loading() && !this.store.error() && this.history().length > 0);
  protected readonly currentType = computed(() => CHART_TYPES.find((type) => type.id === this.chartType()) ?? CHART_TYPES[0]!);
  protected readonly activeStudies = computed(() => STUDIES.filter((study) => this.selectedStudies().includes(study.id)));
  protected readonly changePercent = computed(() => {
    const tick = this.quote();
    const previous = Number(tick?.previousClose?.amount);
    return previous > 0 ? (Number(tick?.lastPrice.amount) - previous) / previous * 100 : undefined;
  });
  protected readonly searchState = toSignal(toObservable(computed(() => ({
    query: this.searchText().trim(), link: this.brokerLinkId(),
  }))).pipe(switchMap(({ query, link }) => query.length < 1 ? of({ results: [] as readonly InstrumentDefinition[], loading: false, error: '' })
    : timer(250).pipe(switchMap(() => this.api.searchInstruments(link, query)),
      map((results) => ({ results, loading: false, error: '' })),
      catchError(() => of({ results: [] as readonly InstrumentDefinition[], loading: false, error: 'Symbol search failed. Try again.' })),
      startWith({ results: [] as readonly InstrumentDefinition[], loading: true, error: '' })))),
  { initialValue: { results: [] as readonly InstrumentDefinition[], loading: false, error: '' } });
  protected readonly definition = toSignal(toObservable(computed(() => ({ link: this.brokerLinkId(), instrument: this.instrument() }))).pipe(
    switchMap(({ link, instrument }) => this.api.getInstrument(link, instrument).pipe(catchError(() => of(undefined)), startWith(undefined))),
  ));
  protected readonly precision = computed(() => {
    const size = this.definition()?.tickSize;
    if (!size || !Number.isFinite(size) || size <= 0) { return 2; }
    for (let decimals = 0; decimals < 8; decimals++) {
      const scaled = size * 10 ** decimals;
      if (Math.abs(scaled - Math.round(scaled)) < 1e-8) { return decimals; }
    }
    return 8;
  });
  protected readonly numberFormat = computed(() => `1.${this.precision()}-${this.precision()}`);
  protected readonly watchRows = computed(() => {
    const now = this.clock();
    return this.watchlist.watched().map((instrument) => {
      const tick = this.marketData.tickFor(instrument.key)();
      const previous = Number(tick?.previousClose?.amount);
      return { instrument, tick,
        change: previous > 0 && tick ? (Number(tick.lastPrice.amount) - previous) / previous * 100 : undefined,
        stale: !tick || !Number.isFinite(Date.parse(tick.timestamp)) || now - Date.parse(tick.timestamp) > 15000,
      };
    });
  });

  constructor() {
    this.restorePreferences();
    const clock = setInterval(() => this.clock.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => clearInterval(clock));
    effect(() => {
      const preferences = { type: this.chartType(), studies: this.selectedStudies(), volume: this.showVolume(),
        grid: this.showGrid(), sidebar: this.showSidebar(), scale: this.scaleMode() };
      try { localStorage.setItem('akshaya.chart.preferences.v1', JSON.stringify(preferences)); }
      catch { this.status.set('Chart preferences cannot be saved on this device.'); }
    });
    effect((cleanup) => {
      if (!this.playing()) { return; }
      const timer = setInterval(() => this.stepReplay(), 700);
      cleanup(() => clearInterval(timer));
    });
    effect((cleanup) => {
      const link = this.brokerLinkId();
      const watched = this.watchlist.watched();
      if (!this.showSidebar()) { return; }
      const releases = watched.filter((item) => item.key !== this.instrument())
        .map((item) => this.marketData.subscribe(link, item.key));
      cleanup(() => releases.forEach((release) => release()));
    });
    // Live prices for the forming bar. Refcounted in `MarketDataService`, so
    // opening a chart on an instrument already in the watchlist costs no
    // extra broker subscription — but the release is still ours to call.
    effect((cleanup) => {
      const brokerLinkId = this.brokerLinkId();
      const instrument = this.instrument();
      if (!brokerLinkId || !instrument) {
        return;
      }
      const unsubscribe = this.marketData.subscribe(brokerLinkId, instrument);
      cleanup(unsubscribe);
    });

    // Backfill. Re-runs on instrument, link or timeframe change; `switchMap`
    // in the store makes the last selection the one that wins.
    effect(() => {
      const frame = this.timeFrame();
      const manifest = this.manifest();
      this.reload();
      this.replayIndex.set(undefined);
      this.playing.set(false);
      this.hoveredBar.set(undefined);
      this.drawingTool.set('cursor');
      this.drawingState.set({ undo: false, redo: false, count: 0 });
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

  protected refresh(): void { this.reload.update((value) => value + 1); }

  protected formatTime(value: string | number, includeDate = false): string {
    const date = new Date(value);
    if (!Number.isFinite(date.getTime())) { return '—'; }
    return new Intl.DateTimeFormat(undefined, { timeZone: this.timeZone(), hour: '2-digit', minute: '2-digit',
      ...(includeDate ? { month: 'short', day: 'numeric' } as const : { second: '2-digit' } as const), hourCycle: 'h23' }).format(date);
  }

  protected toggleStudy(id: StudyId): void {
    this.selectedStudies.update((studies) => studies.includes(id) ? studies.filter((study) => study !== id) : [...studies, id]);
  }

  protected openSymbol(instrument: InstrumentDefinition): void {
    this.searchControl.setValue('');
    void this.router.navigate(['/chart', this.brokerLinkId(), instrument.key]);
  }

  protected pinInstrument(): void {
    const instrument = this.definition();
    if (instrument) { this.watchlist.add(instrument); this.status.set(`${formatInstrumentLabel(instrument.key)} added to your watchlist.`); }
  }

  protected selectTool(tool: DrawingTool): void {
    this.drawingTool.set(tool);
    this.chart()?.cancelDrawing();
    if (tool !== 'cursor') { this.drawingsVisible.set(true); }
    this.status.set(tool === 'cursor' ? 'Scroll to zoom · drag to pan · double-click an axis to reset'
      : tool === 'horizontal' ? 'Click the price chart to place a horizontal line. Escape cancels.'
        : 'Click two points on the price chart. Escape cancels.');
  }

  protected toggleReplay(): void {
    this.playing.set(false);
    this.replayIndex.set(this.isReplay() ? undefined : Math.max(1, Math.floor(this.history().length / 2)));
    this.selectTool('cursor');
    this.status.set(this.isReplay() ? 'Historical replay · live ticks are paused on the chart.' : 'Live chart restored.');
  }

  protected seekReplay(value: number): void {
    this.playing.set(false);
    this.replayIndex.set(Math.max(1, Math.min(this.history().length, value)));
  }

  protected stepReplay(): void {
    const index = this.replayIndex();
    if (index === undefined) { return; }
    this.replayIndex.set(Math.min(this.history().length, index + 1));
    if (index + 1 >= this.history().length) { this.playing.set(false); }
  }

  protected removeDrawings(): void {
    if (window.confirm('Remove all drawings for this instrument and timeframe on this device?')) { this.chart()?.clearDrawings(); }
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') { this.selectTool('cursor'); this.focusMode.set(false); return; }
    if (event.target instanceof HTMLElement && (event.target.closest('input, textarea, select, button, a, [contenteditable="true"]'))) { return; }
    if (event.key.startsWith('Arrow') && !event.altKey && !event.ctrlKey && !event.metaKey) {
      event.preventDefault();
      this.chart()?.moveCursor(event.key === 'ArrowLeft' ? -1 : event.key === 'ArrowRight' ? 1 : 0,
        event.key === 'ArrowUp' ? 1 : event.key === 'ArrowDown' ? -1 : 0);
    }
    if (event.key === 'Enter' && this.drawingTool() !== 'cursor') {
      event.preventDefault();
      this.chart()?.placeDrawingAtCursor();
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
      event.preventDefault();
      if (event.shiftKey) { this.chart()?.redoDrawing(); } else { this.chart()?.undoDrawing(); }
    }
    if (event.key === '+' || event.key === '=') { event.preventDefault(); this.chart()?.zoom(0.8); }
    if (event.key === '-') { event.preventDefault(); this.chart()?.zoom(1.25); }
  }

  private restorePreferences(): void {
    try {
      const value = JSON.parse(localStorage.getItem('akshaya.chart.preferences.v1') ?? '{}') as Record<string, unknown>;
      const type = CHART_TYPES.find((item) => item.id === value['type']);
      if (type) { this.chartType.set(type.id); }
      const studies = value['studies'];
      if (Array.isArray(studies)) { this.selectedStudies.set(STUDIES.filter((item) => studies.includes(item.id)).map((item) => item.id)); }
      if (typeof value['volume'] === 'boolean') { this.showVolume.set(value['volume']); }
      if (typeof value['grid'] === 'boolean') { this.showGrid.set(value['grid']); }
      if (typeof value['sidebar'] === 'boolean') { this.showSidebar.set(value['sidebar']); }
      const scale = value['scale'];
      if (scale === 'normal' || scale === 'log' || scale === 'percentage') { this.scaleMode.set(scale); }
    } catch { untracked(() => this.status.set('Using default chart preferences.')); }
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

import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { catchError, map, of, startWith, switchMap, timer } from 'rxjs';

import {
  ApiService,
  BrokerLinksStore,
  ConnectorStore,
  MarketDataService,
  venueTimeZone,
} from '@akshaya/shared/data-access';
import { ClockService, timeFrameLabel } from '@akshaya/shared/util';
import type { Candle, InstrumentDefinition, InstrumentKey, TimeFrame } from '@akshaya/shared/models';
import { formatInstrumentLabel, parseInstrumentKey } from '@akshaya/shared/models';
import {
  AK_DIALOG_DEFAULTS,
  ConnectionStatusComponent,
  EmptyStateComponent,
  LoadingStateComponent,
  StaleBannerComponent,
} from '@akshaya/shared/ui';
import type {
  ChartBar,
  ChartType,
  DrawingTool,
  IndicatorKind,
  StudyInstance,
  StudyLegendEntry,
} from '@akshaya/market/ui-price-chart';
import { CHART_TYPES, PriceChartComponent, newStudy, studyLabel } from '@akshaya/market/ui-price-chart';
import { DashboardStore } from '@akshaya/portfolio/data-access';
import { OrdersStore } from '@akshaya/orders/data-access';
import { WatchlistStore } from '@akshaya/market/data-access';
import { exposureLevels, exposureRows, type ExposureRow } from './chart-exposure';
import { loadChartPreferences, saveChartPreferences } from './chart-preferences';
import { ChartIndicatorPickerComponent, type ChartIndicatorPickerData } from './chart-indicator-picker.component';
import { ChartLegendComponent } from './chart-legend.component';
import { ChartMenuComponent } from './chart-menu.component';
import { ChartReplayBarComponent } from './chart-replay-bar.component';
import { ChartStudySettingsComponent, type ChartStudySettingsResult } from './chart-study-settings.component';
import { ChartSidebarComponent } from './chart-sidebar.component';
import { ChartToolsComponent } from './chart-tools.component';
import { ChartStore } from './chart.store';

/** The frame a chart opens on when the broker serves it — see `timeFrame`. */
const DEFAULT_TIME_FRAME: TimeFrame = 'oneDay';

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
    MatTooltipModule,
    ReactiveFormsModule,
    DecimalPipe,
    RouterLink,
    ConnectionStatusComponent,
    EmptyStateComponent,
    LoadingStateComponent,
    PriceChartComponent,
    StaleBannerComponent,
    ChartLegendComponent,
    ChartMenuComponent,
    ChartReplayBarComponent,
    ChartSidebarComponent,
    ChartToolsComponent,
  ],
  providers: [ChartStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chart.component.html',
  styleUrl: './chart.component.scss',
  host: {
    '(keydown)': 'onKeydown($event)',
    '(document:click)': 'onDocumentClick()',
    '(document:contextmenu)': 'onDocumentContextMenu($event)',
  },
})
export class ChartComponent {
  private readonly brokerLinksStore = inject(BrokerLinksStore);
  private readonly connectorStore = inject(ConnectorStore);
  private readonly marketData = inject(MarketDataService);
  /** The app's one ticking clock — this screen used to run its own. See `ClockService`. */
  private readonly clock = inject(ClockService).now;
  protected readonly store = inject(ChartStore);
  protected readonly watchlist = inject(WatchlistStore);
  // Both root stores the blotters already fill, so arriving from Positions or
  // Orders costs no request; a deep link fetches each once.
  private readonly portfolio = inject(DashboardStore);
  private readonly orders = inject(OrdersStore);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly dialogs = inject(MatDialog);
  protected readonly chart = viewChild(PriceChartComponent);
  private readonly menuHost = viewChild<ChartMenuComponent, ElementRef<HTMLElement>>(ChartMenuComponent, { read: ElementRef });
  private readonly chartCard = viewChild<ElementRef<HTMLElement>>('chartCard');
  protected readonly chartTypes = CHART_TYPES;
  protected readonly chartType = signal<ChartType>('candles');
  /** The studies on the chart, in order — see `StudyInstance`. */
  protected readonly studies = signal<readonly StudyInstance[]>([]);
  /** What each study reads at the cursor, straight from the chart. */
  protected readonly studyValues = signal<readonly StudyLegendEntry[]>([]);
  protected readonly showVolume = signal(true);
  protected readonly showGrid = signal(true);
  protected readonly showLegend = signal(true);
  protected readonly magnet = signal(false);
  protected readonly showSidebar = signal(true);
  /** Average prices and working orders drawn on the chart. The sidebar lists them either way. */
  protected readonly showExposure = signal(true);
  protected readonly focusMode = signal(false);
  protected readonly scaleMode = signal<'normal' | 'log' | 'percentage'>('normal');
  protected readonly drawingTool = signal<DrawingTool>('cursor');
  protected readonly drawingsVisible = signal(true);
  protected readonly drawingState = signal({ undo: false, redo: false, count: 0, selected: false });
  /** Pointer position + chart value where the chart was right-clicked, or menu closed. */
  protected readonly contextMenu = signal<{ x: number; y: number; price: number | undefined; time: number | undefined } | undefined>(undefined);
  protected readonly hoveredBar = signal<ChartBar | undefined>(undefined);
  protected readonly status = signal('Scroll to zoom · drag to pan · click a drawing to select it');
  protected readonly replayIndex = signal<number | undefined>(undefined);
  protected readonly playing = signal(false);
  protected readonly searchControl = new FormControl('', { nonNullable: true });
  private readonly searchText = toSignal(this.searchControl.valueChanges.pipe(
    map((value) => typeof value === 'string' ? value : ''), startWith(''),
  ), { initialValue: '' });
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

  /** `undefined` until the user picks a frame; the default below stands in until then. */
  private readonly chosenTimeFrame = signal<TimeFrame | undefined>(undefined);
  /**
   * Opening frame: the daily bar. A chart opened from a position, an order or
   * a watchlist row is being opened to answer "where is this instrument",
   * which a day chart answers and a one-minute chart — the first entry in
   * every broker's declared list, and so the old default — does not.
   *
   * Still chosen from the broker's OWN list, not asserted: a connector that
   * declares no daily history falls back to its first offered frame, exactly
   * as before.
   */
  protected readonly timeFrame = computed(() => {
    const frames = this.timeFrames();
    const chosen = this.chosenTimeFrame();
    if (chosen && frames.includes(chosen)) { return chosen; }
    return frames.includes(DEFAULT_TIME_FRAME) ? DEFAULT_TIME_FRAME : frames[0];
  });

  protected readonly quote = computed(() => this.marketData.tickFor(this.instrument())());
  protected readonly connectionState = this.marketData.connectionState;
  /**
   * When the last tick ARRIVED — `MarketDataService.lastTickAtFor`, never
   * `quote().timestamp`. The tick carries the exchange's time on the print,
   * so a symbol that has not traded in the last minute (or any symbol at all
   * in a quiet patch) reports a timestamp minutes old while its ticks keep
   * landing every second. Measuring freshness from it is what put "prices
   * last updated 47s ago — this view may be out of date" over a live chart.
   */
  protected readonly lastUpdatedAt = computed(() => this.marketData.lastTickAtFor(this.instrument())());
  protected readonly lastTickAgeMs = computed(() => {
    const at = this.lastUpdatedAt();
    return at === undefined ? undefined : Math.max(0, this.clock() - at);
  });
  protected readonly isFeedStale = computed(() => (this.lastTickAgeMs() ?? Number.POSITIVE_INFINITY) > 15_000);

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
  /** Only the visible studies are drawn, so this is what the chart is handed. */
  protected readonly drawnStudies = computed(() => this.studies().filter((study) => study.visible));
  /** What the user holds or has resting in this instrument — see `exposureRows`. */
  protected readonly exposure = computed<readonly ExposureRow[]>(
    () => exposureRows(this.instrument(), this.portfolio.snapshot(), this.orders.orders()),
  );
  protected readonly priceLevels = computed(() => this.showExposure() ? exposureLevels(this.exposure()) : []);
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
      // Arrival time, not the print's exchange time — same reason as `lastUpdatedAt`.
      const arrivedAt = this.marketData.lastTickAtFor(instrument.key)();
      return { instrument, tick,
        change: previous > 0 && tick ? (Number(tick.lastPrice.amount) - previous) / previous * 100 : undefined,
        stale: arrivedAt === undefined || now - arrivedAt > 15000,
      };
    });
  });

  constructor() {
    this.restorePreferences();
    // No-ops when the blotters already hold a current answer — see each store's own note.
    this.portfolio.ensureFresh();
    this.orders.ensureFresh();
    effect(() => {
      const saved = saveChartPreferences({
        type: this.chartType(), studies: this.studies(), volume: this.showVolume(),
        grid: this.showGrid(), sidebar: this.showSidebar(), scale: this.scaleMode(), exposure: this.showExposure(),
        legend: this.showLegend(), magnet: this.magnet(),
      });
      if (!saved) { this.status.set('Chart preferences cannot be saved on this device.'); }
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
      this.drawingState.set({ undo: false, redo: false, count: 0, selected: false });
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

  /** Opens the indicator library. It edits `studies` live — see `ChartIndicatorPickerData`. */
  protected openIndicators(): void {
    const data: ChartIndicatorPickerData = {
      studies: this.studies.asReadonly(),
      add: (kind) => this.addStudy(kind),
      remove: (id) => this.removeStudy(id),
    };
    this.dialogs.open(ChartIndicatorPickerComponent, { ...AK_DIALOG_DEFAULTS, width: '460px', data,
      autoFocus: 'first-tabbable', restoreFocus: true });
  }

  protected addStudy(kind: IndicatorKind): void {
    const study = newStudy(kind, this.studies());
    if (!study) { return; }
    this.studies.update((studies) => [...studies, study]);
    this.status.set(`${studyLabel(study.kind, study.params)} added. Click its name on the chart to change it.`);
  }

  protected removeStudy(id: string): void {
    const going = this.studies().find((study) => study.id === id);
    this.studies.update((studies) => studies.filter((study) => study.id !== id));
    if (going) { this.status.set(`${studyLabel(going.kind, going.params)} removed.`); }
  }

  protected toggleStudyVisible(id: string): void {
    this.studies.update((studies) => studies.map((study) => study.id === id ? { ...study, visible: !study.visible } : study));
  }

  /** Per-study settings: parameters and colour, or removal. */
  protected configureStudy(id: string): void {
    const study = this.studies().find((item) => item.id === id);
    if (!study) { return; }
    this.dialogs.open(ChartStudySettingsComponent, { ...AK_DIALOG_DEFAULTS, width: '380px', data: study,
      restoreFocus: true })
      .afterClosed()
      .subscribe((result: ChartStudySettingsResult) => {
        if (result === 'remove') { this.removeStudy(id); return; }
        if (!result) { return; }
        this.studies.update((studies) => studies.map((item) => item.id === id ? result : item));
        this.status.set(`${studyLabel(result.kind, result.params)} updated.`);
      });
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
    this.status.set(tool === 'cursor' ? 'Scroll to zoom · drag to pan · click a drawing to select it'
      : tool === 'horizontal' ? 'Click the price chart to place a horizontal line. Escape cancels.'
        : tool === 'measure' ? 'Click two points on the price chart to measure the range. Escape cancels.'
          : 'Click two points on the price chart. Escape cancels.');
  }

  protected openContextMenu(event: { x: number; y: number; price: number | undefined; time: number | undefined }): void {
    this.contextMenu.set(event);
    this.status.set('Chart menu open. Arrow keys move between items, Escape closes.');
  }

  protected closeContextMenu(restoreFocus = false): void {
    if (this.contextMenu() === undefined) { return; }
    // Focus inside the menu dies with it; hand it back to the chart card so
    // chart shortcuts keep working after the menu closes.
    const focusInside = this.menuHost()?.nativeElement.contains(document.activeElement) === true;
    this.contextMenu.set(undefined);
    if (restoreFocus || focusInside) { setTimeout(() => this.chartCard()?.nativeElement.focus()); }
  }

  /** Any click while the menu is open dismisses it — the overlay div swallows
   *  chart clicks, and menu items close it themselves after their action. */
  protected onDocumentClick(): void {
    this.closeContextMenu();
  }

  protected onDocumentContextMenu(event: MouseEvent): void {
    if (this.contextMenu() === undefined) { return; }
    event.preventDefault();
    const target = event.target instanceof Element ? event.target : null;
    if (target?.closest('ak-price-chart')) { return; }
    // Right-click lands on the overlay while the menu is open — re-anchor at
    // the new chart point rather than just dismissing.
    if (target?.closest('.ak-chart-card') && this.chart()?.emitMenuAt(event.clientX, event.clientY)) { return; }
    this.closeContextMenu();
  }

  protected barAt(time: number | undefined): Candle | undefined {
    return time === undefined ? undefined : this.history().find((bar) => Date.parse(bar.openTime) === time * 1000);
  }

  protected addLineAt(price: number): void {
    this.drawingsVisible.set(true);
    this.chart()?.addHorizontalLine(price);
    this.status.set(`Horizontal line added at ${price.toFixed(this.precision())}.`);
    this.closeContextMenu();
  }

  protected measureFrom(menu: { price: number | undefined; time: number | undefined }): void {
    this.closeContextMenu();
    const time = menu.time ?? Date.parse(this.history().at(-1)?.openTime ?? '') / 1000;
    if (menu.price === undefined || !Number.isFinite(time)) { return; }
    this.selectTool('measure');
    this.chart()?.beginMeasure({ time, price: menu.price });
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
    if (event.key === 'Escape') { this.closeContextMenu(true); this.selectTool('cursor'); this.focusMode.set(false); return; }
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
    if ((event.key === 'Delete' || event.key === 'Backspace') && this.drawingState().selected) {
      event.preventDefault();
      if (this.chart()?.deleteSelectedDrawing()) { this.status.set('Drawing removed. Control or Command Z brings it back.'); }
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
      event.preventDefault();
      if (event.shiftKey) { this.chart()?.redoDrawing(); } else { this.chart()?.undoDrawing(); }
    }
    if (event.key === '+' || event.key === '=') { event.preventDefault(); this.chart()?.zoom(0.8); }
    if (event.key === '-') { event.preventDefault(); this.chart()?.zoom(1.25); }
  }

  private restorePreferences(): void {
    const { preferences, ok } = loadChartPreferences();
    this.chartType.set(preferences.type);
    this.studies.set(preferences.studies);
    this.showVolume.set(preferences.volume);
    this.showGrid.set(preferences.grid);
    this.showSidebar.set(preferences.sidebar);
    this.showExposure.set(preferences.exposure);
    this.showLegend.set(preferences.legend);
    this.magnet.set(preferences.magnet);
    this.scaleMode.set(preferences.scale);
    if (!ok) { untracked(() => this.status.set('Using default chart preferences.')); }
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

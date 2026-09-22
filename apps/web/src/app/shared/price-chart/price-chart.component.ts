import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import {
  AreaSeries,
  BarSeries,
  CandlestickSeries,
  CrosshairMode,
  HistogramSeries,
  LineSeries,
  LineStyle,
  PriceScaleMode,
  TickMarkType,
  createChart,
  type Coordinate,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type MouseEventParams,
  type Time,
  type UTCTimestamp,
} from 'lightweight-charts';

import { AppearanceStore } from '../../core/appearance.store';
import type { Candle, Tick, TimeFrame } from '../../core/models';
import { type ChartBar, foldTickIntoBar } from './candle-bucket';
import { ChartDrawings, validDrawing, type ChartDrawing, type DrawingAnchor, type DrawingTool, type MeasureOverlay } from './chart-drawings';
import { calculateStudies, type ChartType, type StudyId } from './chart-studies';

/**
 * A horizontal price the caller wants marked — an average cost, a resting
 * order's limit or trigger. The chart only draws it; what it means, and
 * whether it should be shown at all, is the caller's business.
 */
export interface ChartPriceLevel {
  readonly price: number;
  /** Short, because it is drawn on the line beside the axis: "Long 50", "Buy 10 limit". */
  readonly title: string;
  readonly tone: 'buy' | 'sell' | 'brand';
  /**
   * Solid for what is already held, dashed for a resting limit, dotted for a
   * trigger that has not fired — the line says how real the price is before
   * anyone reads its label.
   */
  readonly kind: 'held' | 'limit' | 'trigger';
}

/**
 * TradingView Lightweight Charts, wrapped as a dumb presentational component:
 * OHLC in via `candles`, live prices in via `tick`, nothing out. It fetches
 * nothing and knows about no broker — the feature that owns the data decides
 * where bars come from and hands them here.
 *
 * ============================================================================
 * WHY THE CANDLES ARE NOT RED AND GREEN
 * ============================================================================
 * Lightweight Charts defaults to `#26a69a` up / `#ef5350` down, which is the
 * red/green pair DESIGN.md rejects on purpose: roughly 8% of men have
 * red-green colour vision deficiency, and red-on-dark vs green-on-dark is
 * close to the worst case for them. Every colour below is pulled from the
 * app's OWN `--ak-*` tokens instead — the same blue/amber buy/sell pair the
 * order ticket and the watchlist use, including the colour-blind-safe
 * alternate. A candle body and a Buy button are therefore never two
 * different blues, and turning on Settings → Accessibility recolours this
 * chart along with everything else. Do not hardcode a hex value in this file.
 *
 * Theme changes are applied by asking the browser what the tokens currently
 * resolve to (see the `probe` field) rather than by keeping a second palette
 * in here, so the stylesheet stays the single source of truth.
 *
 * The library is ~50kB gzipped, so every consumer must reach it through a
 * lazy route or a `@defer` block; it must never enter the initial bundle
 * (see the `budgets` in `angular.json`).
 */
@Component({
  selector: 'ak-price-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="ak-chart-host" #host role="img" [attr.aria-label]="ariaLabel()"></div>`,
  // Genuinely not expressible as a utility: the canvas needs a definite box to
  // measure itself against, and the library writes its own inline sizing inside.
  styles: `
    :host {
      display: block;
    }
    .ak-chart-host {
      width: 100%;
      height: var(--ak-chart-height, 420px);
    }
  `,
})
export class PriceChartComponent {
  private readonly appearance = inject(AppearanceStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');

  /** Historical bars, oldest first. Replacing this input redraws the series. */
  readonly candles = input.required<readonly Candle[]>();

  /** The timeframe `candles` was requested at — decides how a live tick folds in. */
  readonly timeFrame = input.required<TimeFrame>();

  /** Latest live price. Each new tick updates (or opens) the most recent bar. */
  readonly tick = input<Tick | undefined>(undefined);

  /** Volume histogram under the price series; off for instruments that report none. */
  readonly showVolume = input(true);

  /**
   * IANA zone the time axis and crosshair are labelled in — pass the VENUE's,
   * so an NSE session reads 09:15-15:30 wherever the trader is sitting (what
   * TradingView calls exchange time). Undefined means the browser's own zone.
   *
   * Only the labels move. The bars stay in true UTC seconds, so live ticks
   * bucket into them exactly as before.
   */
  readonly timeZone = input<string | undefined>(undefined);

  /**
   * A canvas is invisible to a screen reader, so the host carries a text
   * label describing what is plotted. It is not a substitute for the data
   * being available elsewhere on the page — the order ticket's live price
   * and the watchlist's LTP cell remain the accessible readings of "what is
   * this instrument doing right now".
   */
  readonly ariaLabel = input('Price chart');

  readonly chartType = input<ChartType>('candles');
  readonly studies = input<readonly StudyId[]>([]);
  readonly scaleMode = input<'normal' | 'log' | 'percentage'>('normal');
  readonly showGrid = input(true);
  readonly drawingTool = input<DrawingTool>('cursor');
  readonly drawingsVisible = input(true);
  readonly drawingKey = input('');
  readonly replay = input(false);
  readonly precision = input(2);
  /** Reference lines on the price series. Replacing the list redraws them; pan and zoom survive. */
  readonly priceLevels = input<readonly ChartPriceLevel[]>([]);
  readonly barChange = output<ChartBar | undefined>();
  readonly drawingComplete = output<void>();
  readonly drawingState = output<{ undo: boolean; redo: boolean; count: number }>();
  readonly notice = output<string>();
  /** Right-click on the chart — pane-0 pixel offsets plus the chart value at that point. */
  readonly chartMenu = output<{ x: number; y: number; price: number | undefined; time: number | undefined }>();

  private chart: IChartApi | undefined;
  private priceSeries: ISeriesApi<'Candlestick' | 'Bar' | 'Line' | 'Area'> | undefined;
  private volumeSeries: ISeriesApi<'Histogram'> | undefined;
  private lastBar: ChartBar | undefined;
  private bars: ChartBar[] = [];
  private readonly studySeries: ISeriesApi<'Line' | 'Histogram'>[] = [];
  private levelLines: IPriceLine[] = [];
  private readonly drawings = new ChartDrawings();
  private drawingItems: ChartDrawing[] = [];
  private redoItems: ChartDrawing[] = [];
  private startAnchor: DrawingAnchor | undefined;
  private previewAnchor: DrawingAnchor | undefined;
  private measureStart: DrawingAnchor | undefined;
  private measureEnd: DrawingAnchor | undefined;
  private measurePreview: DrawingAnchor | undefined;
  private hoveredTime: number | undefined;
  private keyboardAnchor: DrawingAnchor | undefined;
  private updateFrame = 0;

  /**
   * Hidden element used to RESOLVE a custom property to a real colour.
   * `getComputedStyle(el).getPropertyValue('--ak-buy')` hands back the
   * literal token stream — `light-dark(#1d4ed8, #3b82f6)` — because a custom
   * property's computed value is its substitution value, not a used colour.
   * Lightweight Charts cannot parse that and silently paints the candles
   * near-black. Assigning the var to a REAL colour property and reading that
   * back forces the resolution, which is what this probe is for; it also
   * means `color-mix()` and any future token syntax resolve for free.
   */
  private readonly probe = document.createElement('span');
  private readonly colorContext = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  private readonly colorCache = new Map<string, string>();

  constructor() {
    // The library measures its container, so it cannot be constructed until
    // that container is actually in the document with a box.
    afterNextRender(() => {
      this.create();
      this.applyTheme();
      this.applyTimeZone();
      this.drawHistory();
      this.loadDrawings();
      this.applySettings();
    });

    effect(() => {
      this.chartType();
      if (this.chart) { untracked(() => this.replacePriceSeries()); }
    });
    effect(() => {
      this.studies();
      if (this.chart) { untracked(() => this.drawStudies()); }
    });
    effect(() => {
      this.scaleMode();
      this.showGrid();
      this.showVolume();
      this.precision();
      this.drawingTool();
      this.drawingsVisible();
      this.startAnchor = undefined;
      this.previewAnchor = undefined;
      if (this.chart) { untracked(() => this.applySettings()); }
    });
    effect(() => {
      this.drawingKey();
      if (this.chart) { untracked(() => this.loadDrawings()); }
    });
    effect(() => {
      this.priceLevels();
      if (this.chart) { untracked(() => this.drawLevels()); }
    });

    effect(() => {
      this.timeZone();
      this.timeFrame();
      if (this.chart) {
        this.applyTimeZone();
      }
    });

    // Full redraw when the caller swaps the series (new instrument, new timeframe).
    effect(() => {
      this.candles();
      this.timeFrame();
      if (this.chart) {
        untracked(() => this.drawHistory());
      }
    });

    // Incremental update on every tick — never a redraw, which would throw
    // away the user's pan/zoom on each price change.
    effect(() => {
      const tick = this.tick();
      if (!tick || !this.priceSeries || this.replay()) {
        return;
      }
      untracked(() => this.applyTick(tick));
    });

    // Re-read the tokens rather than keeping a second palette in here.
    effect(() => {
      this.appearance.theme();
      this.appearance.cvdSafe();
      if (this.chart) {
        untracked(() => { this.applyTheme(); this.drawStudies(); this.applySettings(); this.drawLevels(); });
      }
    });
  }

  private create(): void {
    const element = this.host().nativeElement;

    // Inside the host so it inherits exactly the cascade the chart sits in.
    this.probe.style.display = 'none';
    element.appendChild(this.probe);

    this.chart = createChart(element, {
      autoSize: true,
      // Labels are formatted in `timeZone` by `applyTimeZone`. `locale` alone
      // (what this used to set, commented "local time, not UTC") changes only
      // how dates are WRITTEN — the library still draws every label in UTC, so
      // an NSE session showed as 03:45-10:00.
      localization: { locale: navigator.language },
      timeScale: { timeVisible: true, secondsVisible: false },
      handleScale: { axisPressedMouseMove: { time: true, price: true } },
      crosshair: { mode: CrosshairMode.Normal },
    });

    this.replacePriceSeries();
    this.chart.subscribeCrosshairMove((event) => this.onCrosshair(event));
    this.chart.subscribeClick((event) => this.onChartClick(event));
    // The library swallows nothing here — the browser's own menu would open
    // over the canvas, so suppress it and hand the point to the page's menu.
    element.addEventListener('contextmenu', (event) => {
      event.preventDefault();
      this.emitMenuAt(event.clientX, event.clientY);
    });
    this.destroyRef.onDestroy(() => {
      cancelAnimationFrame(this.updateFrame);
      // Disposes the canvases and the library's own resize listener; without
      // it, navigating between instruments leaks one chart per visit.
      this.chart?.remove();
      this.chart = undefined;
      this.priceSeries = undefined;
      this.volumeSeries = undefined;
    });
  }

  /**
   * Labels the crosshair and the time axis in `timeZone`. Lightweight Charts
   * has no time-zone option — it formats UTC unless handed formatters — so
   * these are `Intl` formatters bound to the zone.
   */
  private applyTimeZone(): void {
    const chart = this.chart;
    if (!chart) {
      return;
    }

    const locale = navigator.language;
    const timeZone = this.timeZone();
    const format = (options: Intl.DateTimeFormatOptions): ((time: Time) => string) => {
      const formatter = new Intl.DateTimeFormat(locale, { ...options, timeZone });
      return (time) => (typeof time === 'number' ? formatter.format(time * 1000) : String(time));
    };

    const clock: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' };
    // Intraday: "Mon, 21 Sep, 14:23" — the weekday says more than a year would. A daily or
    // longer bar has no time of day worth showing (every NSE daily bar "opens" at 09:15), and
    // a year of bars spans a year boundary, so there it is the date with its year.
    const daily = ['oneDay', 'oneWeek', 'oneMonth'].includes(this.timeFrame());
    const crosshair = daily
      ? format({ weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
      : format({ weekday: 'short', day: 'numeric', month: 'short', ...clock });
    const year = format({ year: 'numeric' });
    const month = format({ month: 'short' });
    const day = format({ day: 'numeric', month: 'short' });
    const minute = format(clock);
    const second = format({ ...clock, second: '2-digit' });

    chart.applyOptions({
      localization: { locale, timeFormatter: crosshair },
      timeScale: {
        tickMarkFormatter: (time: Time, type: TickMarkType) => {
          switch (type) {
            case TickMarkType.Year:
              return year(time);
            case TickMarkType.Month:
              return month(time);
            case TickMarkType.DayOfMonth:
              return day(time);
            case TickMarkType.TimeWithSeconds:
              return second(time);
            default:
              return minute(time);
          }
        },
      },
    });
  }

  /**
   * Pushes the current values of the `--ak-*` tokens into the chart. Called on
   * creation and whenever an appearance preference changes.
   */
  private applyTheme(): void {
    const chart = this.chart;
    const price = this.priceSeries;
    if (!chart || !price) {
      return;
    }

    const up = this.token('--ak-buy', '#3b82f6');
    const down = this.token('--ak-sell', '#f59e0b');
    const text = this.token('--ak-text-secondary', '#98a1b3');
    const border = this.token('--ak-border', '#2a2f38');

    chart.applyOptions({
      layout: {
        // `transparent` lets the surrounding card supply the background,
        // so the chart cannot end up a slightly different dark than its card.
        background: { color: this.token('--ak-surface-1', 'transparent') },
        textColor: text,
        attributionLogo: true,
      },
      grid: { vertLines: { color: border }, horzLines: { color: border } },
      rightPriceScale: { borderColor: border },
      timeScale: { borderColor: border },
      crosshair: { vertLine: { color: text, labelBackgroundColor: border }, horzLine: { color: text, labelBackgroundColor: border } },
    });

    price.applyOptions({
      upColor: this.chartType() === 'hollow' ? 'transparent' : up,
      downColor: down,
      borderUpColor: up,
      borderDownColor: down,
      wickUpColor: up,
      wickDownColor: down,
      color: up,
      lineColor: up,
      topColor: this.token('--ak-buy-surface', 'transparent'),
      bottomColor: 'transparent',
    });

    this.volumeSeries?.applyOptions({ color: border });
  }

  /**
   * Current resolved value of one `--ak-*` colour token, or `fallback` when
   * the property is not set at all. The sentinel round trip is how "not set"
   * is told apart from "set to something": an unresolvable `var()` leaves the
   * probe's colour at whatever was assigned before it, so the sentinel going
   * unchanged is the signal that nothing took.
   */
  private token(name: string, fallback: string): string {
    const sentinel = 'rgb(1, 2, 3)';
    this.probe.style.color = sentinel;
    this.probe.style.color = `var(${name})`;
    const resolved = getComputedStyle(this.probe).color;
    if (!resolved || resolved === sentinel) { return fallback; }
    if (/^rgba?\(/.test(resolved)) { return resolved; }
    const cached = this.colorCache.get(resolved);
    if (cached) { return cached; }
    const context = this.colorContext;
    if (!context) { return fallback; }
    context.clearRect(0, 0, 1, 1);
    context.fillStyle = resolved;
    context.fillRect(0, 0, 1, 1);
    const [red = 0, green = 0, blue = 0, alpha = 255] = context.getImageData(0, 0, 1, 1).data;
    const color = `rgba(${red}, ${green}, ${blue}, ${alpha / 255})`;
    this.colorCache.set(resolved, color);
    return color;
  }

  private drawHistory(): void {
    const chart = this.chart;
    const price = this.priceSeries;
    if (!chart || !price) {
      return;
    }

    // Keyed by time, last one wins. The library requires STRICTLY ascending
    // time and throws on the first repeat — leaving the chart blank, not
    // missing one bar. Brokers do repeat bars: mStock sends some daily candles
    // twice, verbatim. The connector collapses those too; this is the net
    // under the next broker that finds a new way to do it.
    const byTime = new Map<number, Candle>();
    for (const candle of this.candles()) {
      const time = Math.floor(Date.parse(candle.openTime) / 1000);
      if (!Number.isFinite(time)) {
        // A bar we cannot place on the time axis is dropped rather than
        // rendered at epoch zero, which would compress the whole chart.
        continue;
      }
      if ([candle.open, candle.high, candle.low, candle.close].every(Number.isFinite)
        && candle.high >= Math.max(candle.open, candle.close, candle.low)
        && candle.low <= Math.min(candle.open, candle.close)) {
        byTime.set(time, candle);
      }
    }

    // Ascending, whatever order the connector returned them in: newest-first
    // (or unsorted) must not silently render an empty chart either.
    const bars: ChartBar[] = [];
    const volumes: { time: number; value: number }[] = [];
    for (const [time, candle] of [...byTime].sort(([a], [b]) => a - b)) {
      bars.push({ time, open: candle.open, high: candle.high, low: candle.low, close: candle.close });
      volumes.push({ time, value: Number.isFinite(candle.volume) ? Math.max(0, candle.volume) : 0 });
    }

    const previousRange = this.replay() ? chart.timeScale().getVisibleLogicalRange() : null;
    this.bars = bars;
    price.setData(bars.map((bar) => this.pricePoint(bar)));
    this.lastBar = bars.at(-1);
    this.hoveredTime = undefined;
    this.keyboardAnchor = undefined;

    if (volumes.some((v) => v.value > 0)) {
      this.volumeSeries ??= chart.addSeries(HistogramSeries, {
        priceScaleId: 'volume',
        priceFormat: { type: 'volume' },
        // The volume scale is an overlay with no axis of its own, so its last-value badge lands
        // on the PRICE axis — a "2.26M" (or a live bar's "0") sitting among the prices,
        // covering one. Volume is read off the bars; the badge only gets in the way.
        lastValueVisible: false,
        priceLineVisible: false,
      });
      // Pinned to the bottom fifth so volume reads as a footer to the price
      // action rather than competing with it for vertical space.
      chart.priceScale('volume').applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });
      const up = this.token('--ak-buy-surface', 'transparent');
      const down = this.token('--ak-sell-surface', 'transparent');
      this.volumeSeries.setData(volumes.map((v, i) => ({ time: v.time as UTCTimestamp, value: v.value,
        color: (bars[i]?.close ?? 0) >= (bars[i]?.open ?? 0) ? up : down })));
      this.applyTheme();
    } else if (this.volumeSeries) {
      chart.removeSeries(this.volumeSeries);
      this.volumeSeries = undefined;
    }

    this.drawStudies();
    this.applySettings();
    if (previousRange) {
      const width = previousRange.to - previousRange.from;
      chart.timeScale().setVisibleLogicalRange({ from: bars.length - width, to: bars.length });
    } else {
      chart.timeScale().fitContent();
    }
    const tick = this.tick();
    if (tick && !this.replay()) { this.applyTick(tick); }
    this.barChange.emit(this.lastBar);
  }

  private applyTick(tick: Tick): void {
    const price = Number(tick.lastPrice.amount);
    const at = Date.parse(tick.timestamp);
    // A tick whose timestamp we cannot read is still a real price; place it
    // now rather than discarding it.
    const seconds = Math.floor((Number.isFinite(at) ? at : Date.now()) / 1000);

    const bar = foldTickIntoBar(this.lastBar, price, seconds, this.timeFrame());
    if (!bar) {
      return;
    }

    if (this.lastBar?.time === bar.time) { this.bars[this.bars.length - 1] = bar; }
    else {
      this.bars.push(bar);
      this.volumeSeries?.update({ time: bar.time as UTCTimestamp, value: 0 });
    }
    this.lastBar = bar;
    this.priceSeries?.update(this.pricePoint(bar));
    if (this.hoveredTime === undefined || this.hoveredTime === bar.time) { this.barChange.emit(bar); }
    cancelAnimationFrame(this.updateFrame);
    this.updateFrame = requestAnimationFrame(() => this.drawStudies(false));
  }

  moveCursor(horizontal: number, vertical: number): void {
    const chart = this.chart;
    const series = this.priceSeries;
    if (!chart || !series || !this.lastBar) { return; }
    const current = this.keyboardAnchor ?? { time: this.lastBar.time, price: this.lastBar.close };
    const index = this.bars.findIndex((bar) => bar.time === current.time);
    const bar = this.bars[Math.max(0, Math.min(this.bars.length - 1, index + horizontal))];
    if (!bar) { return; }
    const step = Math.max(10 ** -this.precision(), Math.abs(current.price) * 0.001);
    this.keyboardAnchor = { time: bar.time, price: current.price + vertical * step };
    this.previewAnchor = this.keyboardAnchor;
    if (this.measureStart && !this.measureEnd) { this.measurePreview = this.keyboardAnchor; }
    this.hoveredTime = bar.time;
    chart.setCrosshairPosition(this.keyboardAnchor.price, bar.time as UTCTimestamp, series);
    this.barChange.emit(bar);
    this.renderDrawings();
    this.notice.emit(`Cursor price ${this.keyboardAnchor.price.toFixed(this.precision())}. Arrow keys move; Enter places a drawing point.`);
  }

  placeDrawingAtCursor(): void {
    if (!this.keyboardAnchor) { this.moveCursor(0, 0); }
    const anchor = this.keyboardAnchor;
    if (!anchor) { return; }
    const x = this.chart?.timeScale().timeToCoordinate(anchor.time as UTCTimestamp);
    const y = this.priceSeries?.priceToCoordinate(anchor.price);
    if (x !== null && x !== undefined && y !== null && y !== undefined) {
      this.onChartClick({ point: { x, y }, time: anchor.time as UTCTimestamp, paneIndex: 0, seriesData: new Map() });
    }
  }

  fitContent(): void { this.chart?.timeScale().fitContent(); }
  goToLatest(): void { this.chart?.timeScale().scrollToRealTime(); }
  zoom(factor: number): void {
    const scale = this.chart?.timeScale();
    const range = scale?.getVisibleLogicalRange();
    if (!scale || !range) { return; }
    const middle = (range.from + range.to) / 2;
    const width = Math.max(5, (range.to - range.from) * factor) / 2;
    scale.setVisibleLogicalRange({ from: middle - width, to: middle + width });
  }
  selectRange(days: number): void {
    const last = this.bars.at(-1);
    if (!last || !this.chart) { return; }
    const start = this.bars.findIndex((bar) => bar.time >= last.time - days * 86400);
    this.chart.timeScale().setVisibleLogicalRange({ from: Math.max(0, start), to: this.bars.length });
  }
  screenshot(name: string): void {
    const canvas = this.chart?.takeScreenshot();
    canvas?.toBlob((blob) => { if (blob) { this.download(blob, `${name}.png`); } });
  }
  exportCsv(name: string): void {
    const rows = this.bars.map((bar) => [new Date(bar.time * 1000).toISOString(), bar.open, bar.high, bar.low, bar.close].join(','));
    this.download(new Blob([['Time (UTC),Open,High,Low,Close', ...rows].join('\r\n')], { type: 'text/csv;charset=utf-8' }), `${name}.csv`);
  }
  undoDrawing(): void {
    const drawing = this.drawingItems.pop();
    if (drawing) { this.redoItems.push(drawing); this.saveDrawings(); }
  }
  redoDrawing(): void {
    const drawing = this.redoItems.pop();
    if (drawing) { this.drawingItems.push(drawing); this.saveDrawings(); }
  }
  clearDrawings(): void {
    this.drawingItems = [];
    this.redoItems = [];
    this.saveDrawings();
  }
  /** Emits the chart-menu position for a client point — shared by the canvas
   *  contextmenu listener and the card backdrop's right-click re-anchor.
   *  Returns false when the point falls outside the chart surface. */
  emitMenuAt(clientX: number, clientY: number): boolean {
    const rect = this.host().nativeElement.getBoundingClientRect();
    const x = clientX - rect.left;
    const y = clientY - rect.top;
    if (x < 0 || y < 0 || x > rect.width || y > rect.height) { return false; }
    const time = this.chart?.timeScale().coordinateToTime(x as Coordinate);
    const price = this.priceSeries?.coordinateToPrice(y as Coordinate);
    this.chartMenu.emit({ x, y,
      price: price === null || price === undefined ? undefined : price,
      time: typeof time === 'number' ? time : undefined });
    return true;
  }

  /** Drops a horizontal line straight onto the chart (context menu "add line at price"). */
  addHorizontalLine(price: number): void {
    const time = this.lastBar?.time ?? Math.floor(Date.now() / 1000);
    this.drawingItems.push({ tool: 'horizontal', start: { time, price }, end: { time, price } });
    this.redoItems = [];
    this.saveDrawings();
  }
  /** Arms the measure tool with its first point; the next click finishes it. */
  beginMeasure(anchor: DrawingAnchor): void {
    this.measureStart = anchor;
    this.measureEnd = undefined;
    this.measurePreview = undefined;
    this.renderDrawings();
    this.notice.emit('Click the second point on the price chart to finish measuring. Escape cancels.');
  }
  cancelDrawing(): void {
    this.startAnchor = undefined;
    this.previewAnchor = undefined;
    this.measureStart = undefined;
    this.measureEnd = undefined;
    this.measurePreview = undefined;
    this.renderDrawings();
  }

  private download(blob: Blob, name: string): void {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = name.replace(/[^a-zA-Z0-9._-]/g, '_');
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  private pricePoint(bar: ChartBar) {
    return { ...bar, time: bar.time as UTCTimestamp, value: bar.close };
  }

  private replacePriceSeries(): void {
    const chart = this.chart;
    if (!chart) { return; }
    const old = this.priceSeries;
    const options = { priceLineVisible: true, lastValueVisible: true };
    switch (this.chartType()) {
      case 'line': this.priceSeries = chart.addSeries(LineSeries, options); break;
      case 'area': this.priceSeries = chart.addSeries(AreaSeries, options); break;
      case 'bars': this.priceSeries = chart.addSeries(BarSeries, options); break;
      default: this.priceSeries = chart.addSeries(CandlestickSeries, options);
    }
    this.priceSeries.setData(this.bars.map((bar) => this.pricePoint(bar)));
    // A price line belongs to its series and is removed with it, so the old
    // handles are dropped rather than removed a second time.
    this.levelLines = [];
    if (old) { old.detachPrimitive(this.drawings); chart.removeSeries(old); }
    this.priceSeries.setSeriesOrder(0);
    this.priceSeries.attachPrimitive(this.drawings);
    this.applyTheme();
    this.applySettings();
    this.drawLevels();
  }

  /** Rebuilds the caller's reference lines; cheap enough that there is no diffing. */
  private drawLevels(): void {
    const series = this.priceSeries;
    for (const line of this.levelLines.splice(0)) { series?.removePriceLine(line); }
    if (!series) { return; }
    const style = { held: LineStyle.Solid, limit: LineStyle.Dashed, trigger: LineStyle.Dotted } as const;
    for (const level of this.priceLevels()) {
      if (!Number.isFinite(level.price) || level.price <= 0) { continue; }
      this.levelLines.push(series.createPriceLine({
        price: level.price,
        title: level.title,
        color: this.token(`--ak-${level.tone}`, 'currentColor'),
        lineWidth: level.kind === 'held' ? 2 : 1,
        lineStyle: style[level.kind],
        axisLabelVisible: true,
      }));
    }
  }

  private applySettings(): void {
    const drawing = this.drawingTool() !== 'cursor';
    this.chart?.applyOptions({
      grid: { vertLines: { visible: this.showGrid() }, horzLines: { visible: this.showGrid() } },
      handleScroll: !drawing,
      handleScale: !drawing,
    });
    this.priceSeries?.priceScale().applyOptions({ mode: this.scaleMode() === 'log' ? PriceScaleMode.Logarithmic
      : this.scaleMode() === 'percentage' ? PriceScaleMode.Percentage : PriceScaleMode.Normal });
    this.volumeSeries?.applyOptions({ visible: this.showVolume() });
    this.priceSeries?.applyOptions({ priceFormat: { type: 'price', precision: this.precision(), minMove: 10 ** -this.precision() } });
    this.host().nativeElement.style.cursor = drawing ? 'crosshair' : '';
    this.renderDrawings();
  }

  private drawStudies(rebuild = true): void {
    const chart = this.chart;
    if (!chart) { return; }
    if (rebuild) {
      for (const series of this.studySeries.splice(0).reverse()) { chart.removeSeries(series); }
    }
    let pane = 0;
    let index = 0;
    const colors = { brand: this.token('--ak-brand', 'currentColor'), buy: this.token('--ak-buy', 'currentColor'),
      sell: this.token('--ak-sell', 'currentColor') };
    for (const study of calculateStudies(this.bars, this.studies())) {
      const paneIndex = study.pane ? ++pane : 0;
      for (const [plotIndex, plot] of study.plots.entries()) {
        const color = colors[plot.color];
        let series = this.studySeries[index++];
        if (!series) {
          const options = { title: plot.title, color, lineWidth: 1 as const, priceLineVisible: false,
            lastValueVisible: study.pane, crosshairMarkerVisible: false };
          series = plot.histogram ? chart.addSeries(HistogramSeries, options, paneIndex) : chart.addSeries(LineSeries, options, paneIndex);
          this.studySeries.push(series);
          if (study.pane && study.id !== 'macd') {
            series.applyOptions({ autoscaleInfoProvider: () => ({ priceRange: { minValue: 0, maxValue: 100 } }) });
          }
          if (plotIndex === 0) {
            for (const level of study.levels ?? []) {
              series.createPriceLine({ price: level, color: this.token('--ak-text-tertiary', color), lineWidth: 1,
                lineStyle: 2, axisLabelVisible: false, title: String(level) });
            }
          }
        }
        const data = plot.points.map((point) => ({ ...point, time: point.time as UTCTimestamp,
          ...(plot.histogram ? { color: point.value >= 0 ? colors.buy : colors.sell } : {}) }));
        if (rebuild) { series.setData(data); }
        else {
          const lastTime = series.data().at(-1)?.time;
          for (const point of data) {
            if (typeof lastTime !== 'number' || point.time >= lastTime) { series.update(point); }
          }
        }
      }
    }
    if (rebuild) {
      chart.panes().forEach((item, i) => {
        item.setStretchFactor(i === 0 ? 4 : 1.2);
        if (i > 0) { item.priceScale('right').applyOptions({ mode: PriceScaleMode.Normal }); }
      });
    }
  }

  private onCrosshair(event: MouseEventParams): void {
    this.hoveredTime = typeof event.time === 'number' ? event.time : undefined;
    this.barChange.emit(this.bars.find((bar) => bar.time === this.hoveredTime) ?? this.lastBar);
    if (this.measureStart && !this.measureEnd) {
      this.measurePreview = this.anchorFrom(event);
      this.renderDrawings();
    }
    if (this.startAnchor) {
      this.previewAnchor = this.anchorFrom(event);
      this.renderDrawings();
    }
  }

  private anchorFrom(event: MouseEventParams): DrawingAnchor | undefined {
    if (!event.point || (event.paneIndex ?? 0) !== 0 || typeof event.time !== 'number') { return undefined; }
    const price = this.priceSeries?.coordinateToPrice(event.point.y);
    return price === null || price === undefined ? undefined : { time: event.time, price };
  }

  private onChartClick(event: MouseEventParams): void {
    const tool = this.drawingTool();
    const anchor = this.anchorFrom(event);
    if (tool === 'cursor' || !anchor) { return; }
    if (tool === 'measure') {
      if (this.measureStart && !this.measureEnd) {
        this.measureEnd = anchor;
        this.measurePreview = undefined;
        this.notice.emit(`Measured ${this.measureLines(this.measureStart, anchor).join(' · ')}. Click again to measure a new range.`);
      } else {
        this.beginMeasure(anchor);
      }
      this.renderDrawings();
      return;
    }
    if (this.drawingItems.length >= 200) {
      this.notice.emit('This chart has reached its 200-drawing limit. Remove a drawing before adding another.');
      return;
    }
    if (tool !== 'horizontal' && !this.startAnchor) {
      this.startAnchor = anchor;
      this.notice.emit('Choose the second point on the price chart. Escape cancels.');
      return;
    }
    this.drawingItems.push({ tool, start: this.startAnchor ?? anchor, end: anchor });
    this.redoItems = [];
    this.cancelDrawing();
    this.saveDrawings();
    this.drawingComplete.emit();
  }

  private renderDrawings(): void {
    const tool = this.drawingTool();
    const preview = this.startAnchor && this.previewAnchor && tool !== 'cursor' && tool !== 'measure'
      ? [{ tool, start: this.startAnchor, end: this.previewAnchor }] : [];
    const end = this.measureEnd ?? this.measurePreview;
    const measure: MeasureOverlay | undefined = tool === 'measure' && this.measureStart && end
      ? { start: this.measureStart, end,
        color: this.token(end.price >= this.measureStart.price ? '--ak-buy' : '--ak-sell', 'currentColor'),
        lines: this.measureLines(this.measureStart, end) }
      : undefined;
    this.drawings.update(this.drawingsVisible() ? [...this.drawingItems, ...preview] : [],
      this.token('--ak-brand', 'currentColor'), measure);
  }

  /** Price change, percentage change, bar count and elapsed time between the two anchors. */
  private measureLines(start: DrawingAnchor, end: DrawingAnchor): string[] {
    const delta = end.price - start.price;
    const sign = delta >= 0 ? '+' : '';
    const percent = start.price ? delta / start.price * 100 : 0;
    const low = Math.min(start.time, end.time);
    const high = Math.max(start.time, end.time);
    const count = this.bars.filter((bar) => bar.time >= low && bar.time <= high).length;
    const seconds = Math.abs(end.time - start.time);
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const elapsed = [days && `${days}d`, hours && `${hours}h`, minutes && `${minutes}m`]
      .filter(Boolean).join(' ') || `${seconds}s`;
    return [
      `${sign}${delta.toFixed(this.precision())} (${sign}${percent.toFixed(2)}%)`,
      `${count} ${count === 1 ? 'bar' : 'bars'} · ${elapsed}`,
    ];
  }

  private loadDrawings(): void {
    this.drawingItems = [];
    this.redoItems = [];
    this.startAnchor = undefined;
    this.measureStart = undefined;
    this.measureEnd = undefined;
    this.measurePreview = undefined;
    try {
      const parsed: unknown = JSON.parse(localStorage.getItem(`akshaya.chart.drawings.${this.drawingKey()}`) ?? '[]');
      if (Array.isArray(parsed)) { this.drawingItems = parsed.filter(validDrawing).slice(-200); }
    } catch { this.notice.emit('Saved drawings could not be restored on this device.'); }
    this.publishDrawings();
  }

  private saveDrawings(): void {
    this.drawingItems = this.drawingItems.slice(-200);
    try { localStorage.setItem(`akshaya.chart.drawings.${this.drawingKey()}`, JSON.stringify(this.drawingItems)); }
    catch { this.notice.emit('Drawings are available for this session only; device storage is unavailable.'); }
    this.publishDrawings();
  }

  private publishDrawings(): void {
    this.renderDrawings();
    this.drawingState.emit({ undo: this.drawingItems.length > 0, redo: this.redoItems.length > 0, count: this.drawingItems.length });
  }
}

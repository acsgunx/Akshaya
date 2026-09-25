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
  CrosshairMode,
  createChart,
  type Coordinate,
  type IChartApi,
  type MouseEventParams,
  type UTCTimestamp,
} from 'lightweight-charts';

import { AppearanceStore } from '@akshaya/shared/data-access';
import type { Candle, Tick, TimeFrame } from '@akshaya/shared/models';
import { type ChartBar, foldTickIntoBar } from './candle-bucket';
import { barsToCsv, downloadFile, normalizeCandles } from './chart-data';
import { ChartSeries, type ChartPriceLevel } from './chart-series';
import { DrawingController, type DrawingState } from './chart-drawing-controller';
import type { DrawingAnchor, DrawingTool } from './chart-drawings';
import type { StudyInstance } from './chart-indicators';
import { StudyPanes, type StudyLegendEntry } from './chart-study-panes';
import { timeAxisOptions } from './chart-time-format';
import { ChartTokens } from './chart-tokens';
import type { ChartType } from './chart-types';

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
  /** The studies to draw, in order. Instances, not flags — see `StudyInstance`. */
  readonly studies = input<readonly StudyInstance[]>([]);
  readonly scaleMode = input<'normal' | 'log' | 'percentage'>('normal');
  readonly showGrid = input(true);
  /** Magnet snaps the crosshair to the nearest series value, as TradingView's does. */
  readonly crosshair = input<'normal' | 'magnet'>('normal');
  readonly drawingTool = input<DrawingTool>('cursor');
  readonly drawingsVisible = input(true);
  readonly drawingKey = input('');
  readonly replay = input(false);
  readonly precision = input(2);
  /** Reference lines on the price series. Replacing the list redraws them; pan and zoom survive. */
  readonly priceLevels = input<readonly ChartPriceLevel[]>([]);
  readonly barChange = output<ChartBar | undefined>();
  /** What each study reads at the cursor, for the chart legend. */
  readonly studyLegend = output<readonly StudyLegendEntry[]>();
  readonly drawingComplete = output<void>();
  readonly drawingState = output<DrawingState>();
  readonly notice = output<string>();
  /** Right-click on the chart — pane-0 pixel offsets plus the chart value at that point. */
  readonly chartMenu = output<{ x: number; y: number; price: number | undefined; time: number | undefined }>();

  private chart: IChartApi | undefined;
  /** The price, volume and level series. Created with the chart; see `ChartSeries`. */
  private series: ChartSeries | undefined;
  private lastBar: ChartBar | undefined;
  private bars: ChartBar[] = [];
  private studyPanes: StudyPanes | undefined;
  private hoveredTime: number | undefined;
  private keyboardAnchor: DrawingAnchor | undefined;
  private updateFrame = 0;
  /**
   * The first bar's time of the series currently drawn. A refresh that returns
   * the same window should not throw away the user's pan and zoom, and this is
   * how "same series, more bars" is told from "different instrument".
   */
  private seriesAnchor: string | undefined;

  /** Created with the chart, because the probe it reads tokens through has to live in the host. */
  private tokens: ChartTokens | undefined;

  /**
   * The drawing tools' state machine. It owns what a click means, the undo
   * stacks and the saved drawings; this component owns the chart it draws on,
   * and hands it prices, pixels and tokens through this surface.
   */
  private readonly drawing = new DrawingController({
    tool: () => this.drawingTool(),
    visible: () => this.drawingsVisible(),
    precision: () => this.precision(),
    bars: () => this.bars,
    color: (name) => this.token(name, 'currentColor'),
    notice: (text) => this.notice.emit(text),
    state: (state) => this.drawingState.emit(state),
    completed: () => this.drawingComplete.emit(),
  });

  constructor() {
    // The library measures its container, so it cannot be constructed until
    // that container is actually in the document with a box.
    afterNextRender(() => {
      this.create();
      this.applyTheme();
      this.applyTimeZone();
      this.drawHistory();
      this.drawing.load(this.drawingKey());
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
      this.crosshair();
      this.drawingTool();
      this.drawingsVisible();
      this.drawing.resetPending();
      if (this.chart) { untracked(() => this.applySettings()); }
    });
    effect(() => {
      const key = this.drawingKey();
      if (this.chart) { untracked(() => this.drawing.load(key)); }
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
      if (!tick || !this.series || this.replay()) {
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
    this.tokens = new ChartTokens(element);

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

    this.studyPanes = new StudyPanes(this.chart, (name, fallback) => this.token(name, fallback));
    this.series = new ChartSeries(this.chart, (name, fallback) => this.token(name, fallback),
      (previous, current) => {
        previous?.detachPrimitive(this.drawing.primitive);
        current.attachPrimitive(this.drawing.primitive);
      });
    this.replacePriceSeries();
    this.chart.subscribeCrosshairMove((event) => this.onCrosshair(event));
    this.chart.subscribeClick((event) => this.onClick(event));
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
      this.series = undefined;
      this.studyPanes = undefined;
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

    chart.applyOptions(timeAxisOptions(this.timeFrame(), this.timeZone(), navigator.language));
  }

  /**
   * Pushes the current values of the `--ak-*` tokens into the chart. Called on
   * creation and whenever an appearance preference changes.
   */
  private applyTheme(): void {
    const chart = this.chart;
    if (!chart) {
      return;
    }

    const text = this.token('--ak-text-secondary', '#98a1b3');
    const border = this.token('--ak-border', '#2a2f38');

    chart.applyOptions({
      layout: {
        // `transparent` lets the surrounding card supply the background,
        // so the chart cannot end up a slightly different dark than its card.
        background: { color: this.token('--ak-surface-1', 'transparent') },
        textColor: text,
        attributionLogo: true,
        // The separator a user drags to resize a study pane. Without an explicit
        // colour it is the library's own grey, which is invisible on the dark
        // theme and the reason pane heights looked fixed.
        panes: { separatorColor: border, separatorHoverColor: this.token('--ak-brand', border), enableResize: true },
      },
      grid: { vertLines: { color: border }, horzLines: { color: border } },
      rightPriceScale: { borderColor: border },
      timeScale: { borderColor: border },
      crosshair: { vertLine: { color: text, labelBackgroundColor: border }, horzLine: { color: text, labelBackgroundColor: border } },
    });

    this.series?.applyTheme();
  }

  /**
   * Current resolved value of one `--ak-*` colour token, or `fallback` before
   * the chart exists (and therefore the probe that resolves them) or when the
   * property is not set at all. See `ChartTokens`.
   */
  private token(name: string, fallback: string): string {
    return this.tokens?.color(name, fallback) ?? fallback;
  }

  private drawHistory(): void {
    const chart = this.chart;
    const series = this.series;
    if (!chart || !series) {
      return;
    }

    const bars = normalizeCandles(this.candles());
    // Replay steps through the same series one bar at a time, and a refresh
    // returns the same window with a few more bars on the end. Neither is a new
    // chart, so neither should reset the view the user set up.
    const anchor = `${this.drawingKey()}:${bars[0]?.time ?? ''}`;
    const sameSeries = anchor === this.seriesAnchor;
    const previousRange = sameSeries ? chart.timeScale().getVisibleLogicalRange() : null;
    const previousCount = this.bars.length;
    this.seriesAnchor = anchor;
    this.bars = bars;
    series.setBars(bars);
    this.lastBar = bars.at(-1);
    this.hoveredTime = undefined;
    this.keyboardAnchor = undefined;
    // The volume series may have just been created, and it takes its colour from the theme.
    if (series.hasVolume) { this.applyTheme(); }

    this.drawStudies();
    this.applySettings();
    if (previousRange) {
      // Held at the same width, shifted by however many bars arrived — so a
      // replay step and a refresh both keep the window the user was reading.
      const shift = bars.length - previousCount;
      chart.timeScale().setVisibleLogicalRange({ from: previousRange.from + shift, to: previousRange.to + shift });
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

    const opened = this.lastBar?.time !== bar.time;
    if (opened) { this.bars.push(bar); } else { this.bars[this.bars.length - 1] = bar; }
    this.lastBar = bar;
    this.series?.updateBar(bar, opened, this.bars);
    if (this.hoveredTime === undefined || this.hoveredTime === bar.time) { this.barChange.emit(bar); }
    cancelAnimationFrame(this.updateFrame);
    this.updateFrame = requestAnimationFrame(() => this.drawStudies(true));
  }

  moveCursor(horizontal: number, vertical: number): void {
    const chart = this.chart;
    const series = this.series?.priceSeries;
    if (!chart || !series || !this.lastBar) { return; }
    const current = this.keyboardAnchor ?? { time: this.lastBar.time, price: this.lastBar.close };
    const index = this.bars.findIndex((bar) => bar.time === current.time);
    const bar = this.bars[Math.max(0, Math.min(this.bars.length - 1, index + horizontal))];
    if (!bar) { return; }
    const step = Math.max(10 ** -this.precision(), Math.abs(current.price) * 0.001);
    this.keyboardAnchor = { time: bar.time, price: current.price + vertical * step };
    this.hoveredTime = bar.time;
    chart.setCrosshairPosition(this.keyboardAnchor.price, bar.time as UTCTimestamp, series);
    this.barChange.emit(bar);
    this.emitLegend(bar.time);
    this.drawing.cursorMoved(this.keyboardAnchor);
    this.notice.emit(`Cursor price ${this.keyboardAnchor.price.toFixed(this.precision())}. Arrow keys move; Enter places a drawing point.`);
  }

  placeDrawingAtCursor(): void {
    if (!this.keyboardAnchor) { this.moveCursor(0, 0); }
    const anchor = this.keyboardAnchor;
    if (!anchor) { return; }
    const x = this.chart?.timeScale().timeToCoordinate(anchor.time as UTCTimestamp);
    const y = this.series?.priceSeries?.priceToCoordinate(anchor.price);
    // Through the same pixel round trip a mouse click takes, so a cursor
    // parked off-screen places nothing, exactly as a click there would.
    if (x !== null && x !== undefined && y !== null && y !== undefined) {
      this.onClick({ point: { x, y }, time: anchor.time as UTCTimestamp, paneIndex: 0, seriesData: new Map() });
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
    canvas?.toBlob((blob) => { if (blob) { downloadFile(blob, `${name}.png`); } });
  }
  exportCsv(name: string): void {
    downloadFile(new Blob([barsToCsv(this.bars)], { type: 'text/csv;charset=utf-8' }), `${name}.csv`);
  }
  undoDrawing(): void { this.drawing.undo(); }
  redoDrawing(): void { this.drawing.redo(); }
  clearDrawings(): void { this.drawing.clear(); }
  /** Removes the drawing the user selected, if any. True when one went. */
  deleteSelectedDrawing(): boolean { return this.drawing.removeSelected(); }
  /** Emits the chart-menu position for a client point — shared by the canvas
   *  contextmenu listener and the card backdrop's right-click re-anchor.
   *  Returns false when the point falls outside the chart surface. */
  emitMenuAt(clientX: number, clientY: number): boolean {
    const rect = this.host().nativeElement.getBoundingClientRect();
    const x = clientX - rect.left;
    const y = clientY - rect.top;
    if (x < 0 || y < 0 || x > rect.width || y > rect.height) { return false; }
    const time = this.chart?.timeScale().coordinateToTime(x as Coordinate);
    const price = this.series?.priceSeries?.coordinateToPrice(y as Coordinate);
    this.chartMenu.emit({ x, y,
      price: price === null || price === undefined ? undefined : price,
      time: typeof time === 'number' ? time : undefined });
    return true;
  }

  /** Drops a horizontal line straight onto the chart (context menu "add line at price"). */
  addHorizontalLine(price: number): void {
    this.drawing.addHorizontalLine(price, this.lastBar?.time ?? Math.floor(Date.now() / 1000));
  }
  /** Arms the measure tool with its first point; the next click finishes it. */
  beginMeasure(anchor: DrawingAnchor): void { this.drawing.beginMeasure(anchor); }
  cancelDrawing(): void { this.drawing.cancel(); }

  /** Swaps the price series for another form, keeping bars, drawings and levels. */
  private replacePriceSeries(): void {
    this.series?.setType(this.chartType(), this.bars);
    this.applyTheme();
    this.applySettings();
    this.drawLevels();
  }

  private drawLevels(): void {
    this.series?.setLevels(this.priceLevels());
  }

  private applySettings(): void {
    const drawing = this.drawingTool() !== 'cursor';
    this.chart?.applyOptions({
      grid: { vertLines: { visible: this.showGrid() }, horzLines: { visible: this.showGrid() } },
      crosshair: { mode: this.crosshair() === 'magnet' ? CrosshairMode.MagnetOHLC : CrosshairMode.Normal },
      // While a drawing tool is armed, a DRAG must not pan — the two points of a
      // trend line are two clicks, and panning between them moves the chart out
      // from under the second one. The wheel and the axes keep working, which is
      // what TradingView does and what the old blanket `handleScroll: false`
      // took away: zooming to place a point accurately was impossible.
      handleScroll: drawing
        ? { mouseWheel: true, pressedMouseMove: false, horzTouchDrag: false, vertTouchDrag: false }
        : true,
      handleScale: drawing
        ? { mouseWheel: true, pinch: true, axisPressedMouseMove: { time: true, price: true }, axisDoubleClickReset: true }
        : { axisPressedMouseMove: { time: true, price: true } },
    });
    this.series?.applySettings(this.scaleMode(), this.precision(), this.showVolume());
    this.host().nativeElement.style.cursor = drawing ? 'crosshair' : '';
    this.drawing.render();
  }

  /** Redraws the indicator panes; `live` is the per-tick path. See `StudyPanes`. */
  private drawStudies(live = false): void {
    this.studyPanes?.sync(this.bars, this.studies(), { live });
    this.emitLegend(this.hoveredTime);
  }

  /** Hands the legend the studies' values at a bar time (the last bar when absent). */
  private emitLegend(time: number | undefined): void {
    const panes = this.studyPanes;
    if (!panes) { return; }
    const index = time === undefined ? undefined : this.bars.findIndex((bar) => bar.time === time);
    this.studyLegend.emit(panes.legend(index === undefined || index < 0 ? undefined : index));
  }

  private onCrosshair(event: MouseEventParams): void {
    this.hoveredTime = typeof event.time === 'number' ? event.time : undefined;
    this.barChange.emit(this.bars.find((bar) => bar.time === this.hoveredTime) ?? this.lastBar);
    this.emitLegend(this.hoveredTime);
    this.drawing.pointerMoved(() => this.anchorFrom(event));
  }

  /**
   * A click on the chart. With a tool armed it places a drawing point; with the
   * cursor it SELECTS the drawing under the pointer, which is what makes
   * removing one of forty drawings possible without clearing them all.
   */
  private onClick(event: MouseEventParams): void {
    if (this.drawingTool() === 'cursor') {
      if ((event.paneIndex ?? 0) !== 0 || !event.point) { return; }
      const selected = this.drawing.selectAt(event.point);
      if (selected) { this.notice.emit('Drawing selected. Delete or Backspace removes it; Escape deselects.'); }
      return;
    }
    this.drawing.click(this.anchorFrom(event));
  }

  private anchorFrom(event: MouseEventParams): DrawingAnchor | undefined {
    if (!event.point || (event.paneIndex ?? 0) !== 0 || typeof event.time !== 'number') { return undefined; }
    const price = this.series?.priceSeries?.coordinateToPrice(event.point.y);
    return price === null || price === undefined ? undefined : { time: event.time, price };
  }
}

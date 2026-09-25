import {
  HistogramSeries,
  LineSeries,
  LineStyle,
  LineType,
  PriceScaleMode,
  type HistogramSeriesPartialOptions,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type LineSeriesPartialOptions,
  type UTCTimestamp,
} from 'lightweight-charts';

import type { ChartBar } from './candle-bucket';
import { ChartBandFill, type FillBand } from './chart-band-fill';
import {
  calculateStudies,
  type IndicatorKind,
  type PlotShape,
  type StudyInstance,
  type StudyPlot,
  type StudyResult,
} from './chart-indicators';

/** Resolves an `--ak-*` token to a colour the library can paint — see `ChartTokens`. */
type TokenColor = (name: string, fallback: string) => string;

/** One number the legend shows for a study. */
export interface StudyLegendValue {
  readonly title: string;
  /**
   * The plot's `--ak-*` TOKEN, not a resolved colour: the legend is HTML, so it
   * can put the token in a `var()` and re-colour itself on a theme change
   * without the chart having to emit the legend again.
   */
  readonly color: string;
  readonly value: number;
  readonly precision: number | undefined;
}

/** A study's row in the chart legend: what it is, and what it reads at the cursor. */
export interface StudyLegendEntry {
  readonly id: string;
  readonly kind: IndicatorKind;
  readonly label: string;
  readonly overlay: boolean;
  readonly values: readonly StudyLegendValue[];
}

interface Entry {
  readonly series: ISeriesApi<'Line' | 'Histogram'>;
  readonly paneIndex: number;
  readonly shape: PlotShape;
  readonly levels: IPriceLine[];
}

/** A study's band shading, and the series it is attached to so it can be detached again. */
interface Shading {
  readonly fill: ChartBandFill;
  readonly series: ISeriesApi<'Line' | 'Histogram'>;
}

/**
 * Draws the studies: overlays on the price pane, oscillators in panes of their
 * own underneath, the shading between a band's edges, and the reference levels
 * in each oscillator's pane.
 *
 * ============================================================================
 * WHY THIS DIFFS INSTEAD OF REBUILDING
 * ============================================================================
 * Series are held in a map keyed by study instance and plot, and every sync
 * reconciles that map against what the studies now want. Adding an indicator
 * creates only its own series; changing an RSI's period only re-feeds the one
 * that changed; a live tick appends one point to each.
 *
 * The version before this rewrite removed and re-added every series on each
 * sync, which cost the user's pan and zoom several times a second on a live
 * chart — it had to special-case ticks into a second code path to stay usable
 * at all. There is one path here, and `live` only chooses between "replace the
 * data" and "append the tail".
 *
 * The one thing that does force a teardown is a change to the PANE LAYOUT, and
 * only because pane indices shift when a pane in the middle is removed: with
 * RSI, MACD and ATR open, dropping MACD renumbers ATR's pane under it. That
 * happens on a click, not on a tick.
 */
export class StudyPanes {
  private readonly entries = new Map<string, Entry>();
  private readonly fills = new Map<string, Shading>();
  /** Study ids with a pane of their own, in pane order. Pane 0 is the price. */
  private paneOrder: readonly string[] = [];
  /**
   * The last computed studies, kept so `legend` can answer a crosshair move
   * without recomputing anything. A pointer sweep across a 2,000-bar chart
   * fires this dozens of times a second, and recalculating twenty studies on
   * each one is the difference between a legend that tracks the cursor and one
   * that lags behind it.
   */
  private results: readonly StudyResult[] = [];
  private bars: readonly ChartBar[] = [];

  constructor(private readonly chart: IChartApi, private readonly token: TokenColor) {}

  /**
   * Reconciles the drawn series with `studies`, and caches the computed values
   * for `legend` to read.
   *
   * `live` is the tick path: it appends to each series instead of replacing its
   * data, which is what keeps a forming bar cheap.
   */
  sync(
    bars: readonly ChartBar[],
    studies: readonly StudyInstance[],
    options: { readonly live?: boolean } = {},
  ): void {
    const results = calculateStudies(bars, studies);
    const paneStudies = results.filter((result) => !result.overlay).map((result) => result.id);
    if (paneStudies.join('|') !== this.paneOrder.join('|')) {
      this.teardown();
      this.paneOrder = paneStudies;
    }
    // After the teardown above, which clears the cache it would otherwise drop.
    this.results = results;
    this.bars = bars;

    const wanted = new Set<string>();
    for (const result of results) {
      const paneIndex = result.overlay ? 0 : this.paneOrder.indexOf(result.id) + 1;
      let anchor: ISeriesApi<'Line' | 'Histogram'> | undefined;
      for (const [plotIndex, plot] of result.plots.entries()) {
        const key = `${result.id}:${plot.key}`;
        wanted.add(key);
        const entry = this.ensure(key, plot, paneIndex, result, plotIndex === 0);
        this.feed(entry, plot, bars, options.live === true);
        anchor ??= entry.series;
      }
      this.shade(result, bars, anchor);
    }

    for (const [key, entry] of [...this.entries]) {
      if (!wanted.has(key)) {
        for (const line of entry.levels) { entry.series.removePriceLine(line); }
        this.chart.removeSeries(entry.series);
        this.entries.delete(key);
      }
    }
    for (const [id, shading] of [...this.fills]) {
      if (results.some((result) => result.id === id && result.fills.length > 0)) { continue; }
      // Only detach from a series the chart still holds. One that went with its
      // study is already disposed, and detaching from that is what the
      // `ChartSeries.attach` note warns about.
      if ([...this.entries.values()].some((entry) => entry.series === shading.series)) {
        shading.series.detachPrimitive(shading.fill);
      }
      this.fills.delete(id);
    }

    this.layoutPanes();
  }

  /**
   * What the legend should read at a bar index — the last bar when `at` is
   * undefined. Reads the cached results, so it is safe to call on every
   * crosshair move.
   */
  legend(at?: number): StudyLegendEntry[] {
    return this.results.map((result) => this.legendFor(result, this.bars, at));
  }

  /** Drops every study series and every pane below the price. */
  teardown(): void {
    for (const entry of [...this.entries.values()].reverse()) {
      this.chart.removeSeries(entry.series);
    }
    this.entries.clear();
    this.fills.clear();
    this.results = [];
    // Highest first, re-reading the count: removing a pane renumbers the rest.
    while (this.chart.panes().length > 1) {
      this.chart.removePane(this.chart.panes().length - 1);
    }
    this.paneOrder = [];
  }

  private ensure(key: string, plot: StudyPlot, paneIndex: number, result: StudyResult, first: boolean): Entry {
    const existing = this.entries.get(key);
    if (existing && (existing.paneIndex !== paneIndex || existing.shape !== (plot.shape ?? 'line'))) {
      for (const line of existing.levels) { existing.series.removePriceLine(line); }
      this.chart.removeSeries(existing.series);
      this.entries.delete(key);
    }
    const reused = this.entries.get(key);
    if (reused) {
      // Colour, width, dash and precision are all live: a theme change or a
      // settings edit re-styles the series it already has rather than making
      // new ones, which is what keeps pan and zoom through both.
      if (reused.shape === 'histogram') { reused.series.applyOptions(this.histogramStyle(plot, result)); }
      else { reused.series.applyOptions(this.lineStyle(plot, result)); }
      for (const line of reused.levels) {
        line.applyOptions({ color: this.token('--ak-text-tertiary', 'currentColor') });
      }
      return reused;
    }

    const shape = plot.shape ?? 'line';
    const series = shape === 'histogram'
      ? this.chart.addSeries(HistogramSeries, this.histogramStyle(plot, result), paneIndex)
      : this.chart.addSeries(LineSeries, this.lineStyle(plot, result), paneIndex);
    // A bounded oscillator is read against its own fixed scale — an RSI that
    // autoscales to 44-58 makes a quiet patch look like an extreme.
    if (result.bounds) {
      const { min, max } = result.bounds;
      series.applyOptions({ autoscaleInfoProvider: () => ({ priceRange: { minValue: min, maxValue: max } }) });
    }
    const levels: IPriceLine[] = first
      ? result.levels.map((level) => series.createPriceLine({
        price: level,
        color: this.token('--ak-text-tertiary', 'currentColor'),
        lineWidth: 1,
        lineStyle: LineStyle.Dashed,
        axisLabelVisible: false,
        title: String(level),
      }))
      : [];
    const entry: Entry = { series, paneIndex, shape, levels };
    this.entries.set(key, entry);
    return entry;
  }

  /**
   * Options both series forms share. The axis badge is worth the space in an
   * oscillator's own pane, where it is the only thing on that scale; over the
   * price it would sit among the prices and cover one. The series `title` is
   * empty because the legend above the chart is what names a study now.
   */
  private commonStyle(result: StudyResult): LineSeriesPartialOptions & HistogramSeriesPartialOptions {
    const precision = result.precision ?? 2;
    return {
      title: '',
      priceLineVisible: false,
      lastValueVisible: !result.overlay,
      priceFormat: { type: 'price', precision, minMove: 10 ** -precision },
    };
  }

  private lineStyle(plot: StudyPlot, result: StudyResult): LineSeriesPartialOptions {
    return {
      ...this.commonStyle(result),
      color: this.token(plot.color, 'currentColor'),
      lineWidth: plot.width ?? 1,
      lineStyle: plot.dash === 'dashed' ? LineStyle.Dashed : plot.dash === 'dotted' ? LineStyle.Dotted : LineStyle.Solid,
      lineType: plot.shape === 'stepline' ? LineType.WithSteps : LineType.Simple,
      // Parabolic SAR is a run of dots, not a line through them.
      lineVisible: plot.shape !== 'dots',
      pointMarkersVisible: plot.shape === 'dots',
      pointMarkersRadius: 1.6,
      crosshairMarkerVisible: false,
    };
  }

  private histogramStyle(plot: StudyPlot, result: StudyResult): HistogramSeriesPartialOptions {
    return { ...this.commonStyle(result), color: this.token(plot.color, 'currentColor'), base: 0 };
  }

  /**
   * Pushes a plot's values in as series data. A plot's `offset` is applied
   * here, by reading the bar `offset` slots along — and dropping anything that
   * would fall off either end, because the chart does not invent session times
   * (see `StudyPlot.offset`).
   */
  private feed(entry: Entry, plot: StudyPlot, bars: readonly ChartBar[], live: boolean): void {
    const offset = plot.offset ?? 0;
    const up = this.token('--ak-buy', 'currentColor');
    const down = this.token('--ak-sell', 'currentColor');
    const points: { time: UTCTimestamp; value: number; color?: string }[] = [];
    for (let index = 0; index < plot.values.length; index++) {
      const value = plot.values[index];
      if (value === undefined || !Number.isFinite(value)) { continue; }
      const bar = bars[index + offset];
      if (!bar) { continue; }
      points.push({
        time: bar.time as UTCTimestamp,
        value,
        ...(plot.signColors ? { color: value >= 0 ? up : down } : {}),
      });
    }
    if (!live) {
      entry.series.setData(points);
      return;
    }
    // The tail only: a tick can move the forming bar's value and, for a study
    // whose window has just filled, add one earlier point behind it.
    const lastDrawn = entry.series.data().at(-1)?.time;
    for (const point of points) {
      if (typeof lastDrawn !== 'number' || point.time >= lastDrawn) { entry.series.update(point); }
    }
  }

  /** Attaches (or updates) the shading between a study's band edges. */
  private shade(result: StudyResult, bars: readonly ChartBar[], anchor: ISeriesApi<'Line' | 'Histogram'> | undefined): void {
    if (result.fills.length === 0 || !anchor) { return; }
    let shading = this.fills.get(result.id);
    if (!shading || shading.series !== anchor) {
      shading = { fill: new ChartBandFill(), series: anchor };
      this.fills.set(result.id, shading);
      anchor.attachPrimitive(shading.fill);
    }
    const valuesOf = (key: string) => result.plots.find((plot) => plot.key === key);
    const bands: FillBand[] = [];
    for (const band of result.fills) {
      const upper = valuesOf(band.from);
      const lower = valuesOf(band.to);
      if (!upper || !lower) { continue; }
      const offset = upper.offset ?? 0;
      // Both edges share the band's offset, so the times are shifted once.
      const times = bars.map((_, index) => bars[index + offset]?.time ?? NaN);
      bands.push({ times, upper: upper.values, lower: lower.values,
        color: this.token(band.color, 'currentColor'), opacity: band.opacity });
    }
    shading.fill.update(bands);
  }

  /**
   * The price keeps the height; the oscillators share what is left. One pane
   * takes a sixth, and they never take more than half between them however many
   * are open — past that the price series stops being a chart, which is what the
   * whole screen is for. Beyond three or four the panes get thin, and the answer
   * there is the separators, which are draggable and themed to be visible.
   */
  private layoutPanes(): void {
    const count = this.paneOrder.length;
    const share = Math.min(0.5, 0.16 * count);
    this.chart.panes().forEach((pane, index) => {
      pane.setStretchFactor(index === 0 ? (1 - share) * 100 : (share / Math.max(1, count)) * 100);
      if (index > 0) {
        // Log and percentage belong to the price, never to an oscillator's own scale.
        pane.priceScale('right').applyOptions({ mode: PriceScaleMode.Normal });
      }
    });
  }

  private legendFor(result: StudyResult, bars: readonly ChartBar[], at: number | undefined): StudyLegendEntry {
    const index = at === undefined || at < 0 || at >= bars.length ? bars.length - 1 : at;
    return {
      id: result.id,
      kind: result.kind,
      label: result.label,
      overlay: result.overlay,
      values: result.plots.flatMap((plot) => {
        if (plot.inLegend === false) { return []; }
        // The value DRAWN at this bar comes from `offset` slots back in the plot.
        const value = plot.values[index - (plot.offset ?? 0)];
        return value !== undefined && Number.isFinite(value)
          ? [{ title: plot.title, color: plot.color, value, precision: result.precision }]
          : [];
      }),
    };
  }
}

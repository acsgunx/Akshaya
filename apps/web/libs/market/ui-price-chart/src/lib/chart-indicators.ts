import type { ChartBar } from './candle-bucket';
import {
  PRICE_SOURCES,
  atr,
  change,
  dema,
  ema,
  highest,
  hma,
  lowest,
  rma,
  rollingSum,
  rsi,
  sessionStarts,
  sma,
  sourceValues,
  stdev,
  tema,
  trueRange,
  volumes,
  vwma,
  wma,
  type PriceSource,
} from './indicator-math';

/**
 * ============================================================================
 * THE INDICATOR REGISTRY
 * ============================================================================
 * Every study the chart can draw, described as DATA rather than as a branch in
 * a switch. A definition says what it is called, what it is filed under, what
 * knobs it has, and how to turn bars into plots; nothing else in the app
 * knows the difference between an SMA and an Ichimoku cloud.
 *
 * That is the whole point. The screen before this rewrite had six studies with
 * their periods baked into their names — "SMA 20", "EMA 50" — computed from
 * closes only, one instance each, no settings. Adding a seventh meant editing
 * a `StudyId` union, a `STUDIES` list, a `switch`, a preferences validator and
 * a menu template. Here it means appending one object to `INDICATORS`, and the
 * picker, the settings sheet, the legend, the panes and the saved preferences
 * all pick it up with no further edits.
 *
 * Three consequences worth knowing about:
 *
 * - **Studies are INSTANCES, not flags.** Two SMAs at 20 and 50 are two
 *   `StudyInstance` records with the same `kind`. See `StudyInstance`.
 * - **Every study sees whole bars**, not just closes: OHLC *and* volume. That
 *   is what makes ATR, VWAP, OBV, MFI and the rest possible at all.
 * - **Plot values are dense arrays aligned with the bars** (see the warm-up
 *   contract in `indicator-math.ts`), so the legend can read a value at the
 *   cursor by index and a tick can update the last slot in place.
 *
 * Nothing here touches Lightweight Charts, the DOM or Angular: it is arrays in,
 * arrays out, and it is what `StudyPanes` draws and what the legend reads.
 */

/** How a plot is painted. `dots` is a line with markers and no stroke (Parabolic SAR). */
export type PlotShape = 'line' | 'histogram' | 'dots' | 'stepline';
export type PlotDash = 'solid' | 'dashed' | 'dotted';

export interface StudyPlot {
  /** Stable within the study, so a series survives a parameter change without being recreated. */
  readonly key: string;
  readonly title: string;
  /** One slot per bar, `NaN` where the study has no value. See the warm-up contract. */
  readonly values: readonly number[];
  /** An `--ak-*` token name, resolved against the live theme when drawn. */
  readonly color: string;
  readonly shape?: PlotShape;
  readonly dash?: PlotDash;
  readonly width?: 1 | 2 | 3;
  /** Histogram bars coloured by sign rather than one colour (MACD, Awesome Oscillator). */
  readonly signColors?: boolean;
  /**
   * Bars to shift the plot by; positive is forward in time. CLAMPED AT THE
   * LAST BAR: projecting Ichimoku's cloud 26 bars into the future would mean
   * inventing 26 session timestamps, and the venue calendar is not this
   * library's to guess (see `BUCKET_SECONDS`). The cloud therefore ends at the
   * last bar instead of leading it, and the study says so.
   */
  readonly offset?: number;
  /** False keeps the plot off the legend row — a band edge nobody reads a number off. */
  readonly inLegend?: boolean;
}

/** A translucent fill between two of a study's plots: Bollinger, Keltner, Donchian, Ichimoku. */
export interface StudyFill {
  readonly from: string;
  readonly to: string;
  readonly color: string;
  readonly opacity: number;
}

export type StudyParams = Readonly<Record<string, number | string>>;

/**
 * One study on the chart. `kind` says which indicator, `params` how it is
 * tuned, and `id` distinguishes this instance from another of the same kind —
 * which is what makes "SMA 20 and SMA 50 and SMA 200" expressible.
 */
export interface StudyInstance {
  readonly id: string;
  readonly kind: IndicatorKind;
  readonly params: StudyParams;
  /** Palette token for the study's main plot. Always set; `newStudy` rotates it. */
  readonly color: string;
  /** The legend's eye toggle. A hidden study is not computed or drawn. */
  readonly visible: boolean;
}

export interface StudyResult {
  readonly id: string;
  readonly kind: IndicatorKind;
  readonly label: string;
  readonly overlay: boolean;
  readonly plots: readonly StudyPlot[];
  readonly fills: readonly StudyFill[];
  /** Dashed reference lines in the study's own pane. */
  readonly levels: readonly number[];
  /** A fixed pane scale for a bounded oscillator; absent means autoscale. */
  readonly bounds: { readonly min: number; readonly max: number } | undefined;
  /** Decimals the legend rounds to. Absent means "use the instrument's price precision". */
  readonly precision: number | undefined;
}

export type IndicatorCategory =
  | 'Moving averages'
  | 'Bands & channels'
  | 'Trend'
  | 'Momentum'
  | 'Volatility'
  | 'Volume';

export const INDICATOR_CATEGORIES: readonly IndicatorCategory[] = [
  'Moving averages', 'Bands & channels', 'Trend', 'Momentum', 'Volatility', 'Volume',
];

export type IndicatorParam =
  | { readonly key: string; readonly label: string; readonly kind: 'number'; readonly min: number; readonly max: number; readonly step: number }
  | { readonly key: string; readonly label: string; readonly kind: 'source' };

export interface IndicatorDefinition {
  readonly kind: IndicatorKind;
  readonly label: string;
  readonly category: IndicatorCategory;
  /** True draws on the price pane; false gets a pane of its own underneath. */
  readonly overlay: boolean;
  /** The one line under the name in the picker. Say what it measures, not how. */
  readonly summary: string;
  readonly params: readonly IndicatorParam[];
  readonly defaults: StudyParams;
  readonly levels?: readonly number[];
  readonly bounds?: { readonly min: number; readonly max: number };
  readonly precision?: number;
  /** The parameters, as they read after the name: "SMA **20**", "MACD **12 26 9**". */
  readonly caption?: (params: StudyParams) => string;
  readonly build: (bars: readonly ChartBar[], params: StudyParams, tint: Tint) => {
    readonly plots: readonly StudyPlot[];
    readonly fills?: readonly StudyFill[];
  };
}

/**
 * The palette a study's lines are drawn from.
 *
 * These are the app's own `--ak-*` tokens, so a study recolours with the theme
 * and with Settings → Accessibility exactly as the candles do (see the note at
 * the top of `price-chart.component.ts`). They are LINE IDENTITY, not
 * direction: nothing here means "up" or "down", the buy/sell pair keeps that
 * job on the candles and on the order ticket, and the user picks per study.
 */
export const STUDY_COLORS: readonly { readonly token: string; readonly label: string }[] = [
  { token: '--ak-brand', label: 'Violet' },
  { token: '--ak-info', label: 'Sky' },
  { token: '--ak-sell', label: 'Amber' },
  { token: '--ak-buy', label: 'Blue' },
  { token: '--ak-success', label: 'Green' },
  { token: '--ak-danger', label: 'Red' },
  { token: '--ak-warning', label: 'Yellow' },
  { token: '--ak-text-secondary', label: 'Grey' },
];

/**
 * A study's colours: its own, plus neighbours for its secondary plots.
 *
 * A multi-line study picks its extra colours by stepping through the palette
 * from the one the user chose, so MACD's signal line is never the same colour
 * as its MACD line whatever the user set, and no definition has to hardcode a
 * token that might collide.
 */
export interface Tint {
  readonly own: string;
  readonly next: (step: number) => string;
}

function tintFrom(color: string): Tint {
  const index = Math.max(0, STUDY_COLORS.findIndex((entry) => entry.token === color));
  return {
    own: color,
    next: (step) => STUDY_COLORS[(index + step) % STUDY_COLORS.length]?.token ?? color,
  };
}

// ---- parameter access -------------------------------------------------------

/**
 * Merges a study's saved parameters over its defaults and CLAMPS every one to
 * the range its definition declares.
 *
 * Done here, once, rather than in each `build`: parameters arrive from
 * `localStorage` and from a number field the user can type anything into, and a
 * period of `0`, `-3` or `1e9` is a blank plot, a thrown exception or a frozen
 * tab rather than a preference. Every `build` below can therefore read
 * `n(params, 'length')` and trust it.
 */
export function normalizeParams(definition: IndicatorDefinition, params: StudyParams): StudyParams {
  const merged: Record<string, number | string> = {};
  for (const meta of definition.params) {
    const saved = params[meta.key];
    const fallback = definition.defaults[meta.key];
    if (meta.kind === 'source') {
      merged[meta.key] = PRICE_SOURCES.some((entry) => entry.value === saved)
        ? (saved as string)
        : (PRICE_SOURCES.some((entry) => entry.value === fallback) ? (fallback as string) : 'close');
      continue;
    }
    const raw = Number(saved);
    const value = Number.isFinite(raw) ? raw : Number(fallback);
    const clamped = Math.min(meta.max, Math.max(meta.min, Number.isFinite(value) ? value : meta.min));
    // A whole-number step means a whole-number parameter: a 20.5-bar average
    // is a window of 20 bars with a misleading label on it.
    merged[meta.key] = meta.step >= 1 ? Math.round(clamped) : clamped;
  }
  return merged;
}

/** One normalized numeric parameter. Safe because `normalizeParams` already ran. */
function n(params: StudyParams, key: string): number {
  return Number(params[key]);
}

function source(params: StudyParams, key = 'source'): PriceSource {
  const value = params[key];
  return PRICE_SOURCES.some((entry) => entry.value === value) ? (value as PriceSource) : 'close';
}

// ---- shared parameter shapes ------------------------------------------------

const LENGTH = (label = 'Length', min = 1, max = 2000): IndicatorParam =>
  ({ key: 'length', label, kind: 'number', min, max, step: 1 });
const SOURCE: IndicatorParam = { key: 'source', label: 'Source', kind: 'source' };
const MULT = (key: string, label: string, max = 20): IndicatorParam =>
  ({ key, label, kind: 'number', min: 0.1, max, step: 0.1 });

/** A single-line moving-average definition — seven of these differ only in the average used. */
function movingAverage(
  kind: IndicatorKind,
  label: string,
  summary: string,
  length: number,
  average: (values: readonly number[], period: number, bars: readonly ChartBar[]) => number[],
): IndicatorDefinition {
  return {
    kind, label, summary,
    category: 'Moving averages',
    overlay: true,
    params: [LENGTH(), SOURCE],
    defaults: { length, source: 'close' },
    caption: (params) => String(params['length'] ?? length),
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      return { plots: [{ key: 'ma', title: `${label} ${period}`, color: tint.own, width: 2,
        values: average(sourceValues(bars, source(params)), period, bars) }] };
    },
  };
}

export const INDICATORS: readonly IndicatorDefinition[] = [
  // ---- Moving averages ----------------------------------------------------
  movingAverage('sma', 'SMA', 'Simple moving average — the plain mean of the last N bars.', 20,
    (values, period) => sma(values, period)),
  movingAverage('ema', 'EMA', 'Exponential moving average — recent bars weighted more heavily.', 50,
    (values, period) => ema(values, period)),
  movingAverage('wma', 'WMA', 'Weighted moving average — weights fall off linearly into the past.', 20,
    (values, period) => wma(values, period)),
  movingAverage('hma', 'HMA', 'Hull moving average — turns far sooner than an EMA of the same length.', 21,
    (values, period) => hma(values, period)),
  movingAverage('dema', 'DEMA', 'Double exponential moving average — one lag-reduction step.', 21,
    (values, period) => dema(values, period)),
  movingAverage('tema', 'TEMA', 'Triple exponential moving average — two lag-reduction steps.', 21,
    (values, period) => tema(values, period)),
  movingAverage('vwma', 'VWMA', 'Volume-weighted moving average — heavier bars pull it further.', 20,
    (values, period, bars) => vwma(values, volumes(bars), period)),
  {
    kind: 'vwap', label: 'VWAP', category: 'Moving averages', overlay: true,
    summary: 'Volume-weighted average price, re-anchored each session.',
    params: [SOURCE],
    defaults: { source: 'hlc3' },
    build: (bars, params, tint) => {
      const prices = sourceValues(bars, source(params));
      const volume = volumes(bars);
      const { starts, perSession } = sessionStarts(bars);
      const values: number[] = [];
      let cumulativePrice = 0;
      let cumulativeVolume = 0;
      bars.forEach((_, i) => {
        if (starts[i]) { cumulativePrice = 0; cumulativeVolume = 0; }
        cumulativePrice += (prices[i] ?? 0) * (volume[i] ?? 0);
        cumulativeVolume += volume[i] ?? 0;
        values.push(cumulativeVolume > 0 ? cumulativePrice / cumulativeVolume : NaN);
      });
      return { plots: [{ key: 'vwap', title: perSession ? 'VWAP' : 'VWAP (range)', color: tint.own, width: 2, values }] };
    },
  },

  // ---- Bands & channels ---------------------------------------------------
  {
    kind: 'bollinger', label: 'Bollinger Bands', category: 'Bands & channels', overlay: true,
    summary: 'A moving average with standard-deviation bands — volatility as width.',
    params: [LENGTH(), MULT('mult', 'Standard deviations', 10), SOURCE],
    defaults: { length: 20, mult: 2, source: 'close' },
    caption: (params) => `${params['length']} ${params['mult']}`,
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      const multiple = n(params, 'mult');
      const basis = sma(sourceValues(bars, source(params)), period);
      const deviation = stdev(sourceValues(bars, source(params)), period);
      const band = (sign: number) => basis.map((value, i) => value + sign * multiple * (deviation[i] ?? NaN));
      return {
        plots: [
          { key: 'upper', title: 'Upper', color: tint.own, values: band(1), dash: 'solid' },
          { key: 'basis', title: 'Basis', color: tint.own, values: basis, dash: 'dashed', width: 2 },
          { key: 'lower', title: 'Lower', color: tint.own, values: band(-1), dash: 'solid' },
        ],
        fills: [{ from: 'upper', to: 'lower', color: tint.own, opacity: 0.07 }],
      };
    },
  },
  {
    kind: 'keltner', label: 'Keltner Channels', category: 'Bands & channels', overlay: true,
    summary: 'An EMA with average-true-range bands — volatility from the whole bar, not just closes.',
    params: [LENGTH(), MULT('mult', 'ATR multiple'), { key: 'atrLength', label: 'ATR length', kind: 'number', min: 1, max: 500, step: 1 }, SOURCE],
    defaults: { length: 20, mult: 2, atrLength: 10, source: 'close' },
    caption: (params) => `${params['length']} ${params['mult']}`,
    build: (bars, params, tint) => {
      const basis = ema(sourceValues(bars, source(params)), n(params, 'length'));
      const range = atr(bars, n(params, 'atrLength'));
      const multiple = n(params, 'mult');
      const band = (sign: number) => basis.map((value, i) => value + sign * multiple * (range[i] ?? NaN));
      return {
        plots: [
          { key: 'upper', title: 'Upper', color: tint.own, values: band(1) },
          { key: 'basis', title: 'Basis', color: tint.own, values: basis, dash: 'dashed', width: 2 },
          { key: 'lower', title: 'Lower', color: tint.own, values: band(-1) },
        ],
        fills: [{ from: 'upper', to: 'lower', color: tint.own, opacity: 0.07 }],
      };
    },
  },
  {
    kind: 'donchian', label: 'Donchian Channels', category: 'Bands & channels', overlay: true,
    summary: 'The highest high and lowest low of the last N bars — the breakout box.',
    params: [LENGTH()],
    defaults: { length: 20 },
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      const upper = highest(bars.map((bar) => bar.high), period);
      const lower = lowest(bars.map((bar) => bar.low), period);
      return {
        plots: [
          { key: 'upper', title: 'Upper', color: tint.own, values: upper, shape: 'stepline' },
          { key: 'basis', title: 'Basis', color: tint.own, dash: 'dashed', values: upper.map((value, i) => (value + (lower[i] ?? NaN)) / 2) },
          { key: 'lower', title: 'Lower', color: tint.own, values: lower, shape: 'stepline' },
        ],
        fills: [{ from: 'upper', to: 'lower', color: tint.own, opacity: 0.07 }],
      };
    },
  },
  {
    kind: 'envelope', label: 'Envelope', category: 'Bands & channels', overlay: true,
    summary: 'A moving average offset by a fixed percentage either side.',
    params: [LENGTH(), { key: 'percent', label: 'Percent', kind: 'number', min: 0.1, max: 50, step: 0.1 }, SOURCE],
    defaults: { length: 20, percent: 2.5, source: 'close' },
    caption: (params) => `${params['length']} ${params['percent']}%`,
    build: (bars, params, tint) => {
      const basis = sma(sourceValues(bars, source(params)), n(params, 'length'));
      const factor = n(params, 'percent') / 100;
      return {
        plots: [
          { key: 'upper', title: 'Upper', color: tint.own, values: basis.map((value) => value * (1 + factor)) },
          { key: 'basis', title: 'Basis', color: tint.own, values: basis, dash: 'dashed', width: 2 },
          { key: 'lower', title: 'Lower', color: tint.own, values: basis.map((value) => value * (1 - factor)) },
        ],
        fills: [{ from: 'upper', to: 'lower', color: tint.own, opacity: 0.06 }],
      };
    },
  },
  {
    kind: 'ichimoku', label: 'Ichimoku Cloud', category: 'Bands & channels', overlay: true,
    summary: 'Tenkan, Kijun, the Senkou cloud and Chikou. The cloud is not projected past the last bar.',
    params: [
      { key: 'conversion', label: 'Conversion (Tenkan)', kind: 'number', min: 1, max: 200, step: 1 },
      { key: 'base', label: 'Base (Kijun)', kind: 'number', min: 1, max: 400, step: 1 },
      { key: 'span', label: 'Leading span B', kind: 'number', min: 1, max: 600, step: 1 },
      { key: 'displacement', label: 'Displacement', kind: 'number', min: 0, max: 200, step: 1 },
    ],
    defaults: { conversion: 9, base: 26, span: 52, displacement: 26 },
    caption: (params) => `${params['conversion']} ${params['base']} ${params['span']}`,
    build: (bars, params, tint) => {
      const highs = bars.map((bar) => bar.high);
      const lows = bars.map((bar) => bar.low);
      const midpoint = (period: number) => {
        const top = highest(highs, period);
        const bottom = lowest(lows, period);
        return top.map((value, i) => (value + (bottom[i] ?? NaN)) / 2);
      };
      const tenkan = midpoint(n(params, 'conversion'));
      const kijun = midpoint(n(params, 'base'));
      const offset = n(params, 'displacement');
      return {
        plots: [
          { key: 'tenkan', title: 'Tenkan', color: tint.own, values: tenkan },
          { key: 'kijun', title: 'Kijun', color: tint.next(1), values: kijun, width: 2 },
          { key: 'senkouA', title: 'Senkou A', color: tint.next(2), offset,
            values: tenkan.map((value, i) => (value + (kijun[i] ?? NaN)) / 2) },
          { key: 'senkouB', title: 'Senkou B', color: tint.next(3), offset, values: midpoint(n(params, 'span')) },
          { key: 'chikou', title: 'Chikou', color: tint.next(4), offset: -offset, dash: 'dotted',
            values: bars.map((bar) => bar.close) },
        ],
        fills: [{ from: 'senkouA', to: 'senkouB', color: tint.next(2), opacity: 0.1 }],
      };
    },
  },

  // ---- Trend --------------------------------------------------------------
  {
    kind: 'supertrend', label: 'Supertrend', category: 'Trend', overlay: true,
    summary: 'An ATR-banded trailing stop that flips side when price closes through it.',
    params: [MULT('mult', 'ATR multiple'), { key: 'atrLength', label: 'ATR length', kind: 'number', min: 1, max: 500, step: 1 }],
    defaults: { mult: 3, atrLength: 10 },
    caption: (params) => `${params['atrLength']} ${params['mult']}`,
    build: (bars, params, tint) => {
      const range = atr(bars, n(params, 'atrLength'));
      const multiple = n(params, 'mult');
      const rising: number[] = new Array<number>(bars.length).fill(NaN);
      const falling: number[] = new Array<number>(bars.length).fill(NaN);
      let upper = NaN;
      let lower = NaN;
      let trend = 1;
      bars.forEach((bar, i) => {
        const band = range[i];
        const previous = bars[i - 1];
        if (!Number.isFinite(band) || !previous) { return; }
        const middle = (bar.high + bar.low) / 2;
        const rawUpper = middle + multiple * (band as number);
        const rawLower = middle - multiple * (band as number);
        // The bands only ever tighten while the trend holds; a close through
        // the far band is what releases them.
        upper = Number.isFinite(upper) && !(rawUpper < upper || previous.close > upper) ? upper : rawUpper;
        lower = Number.isFinite(lower) && !(rawLower > lower || previous.close < lower) ? lower : rawLower;
        trend = trend === -1 && bar.close > upper ? 1 : trend === 1 && bar.close < lower ? -1 : trend;
        if (trend === 1) { rising[i] = lower; } else { falling[i] = upper; }
      });
      return { plots: [
        { key: 'up', title: 'Support', color: tint.own, values: rising, width: 2, shape: 'stepline' },
        { key: 'down', title: 'Resistance', color: tint.next(2), values: falling, width: 2, shape: 'stepline' },
      ] };
    },
  },
  {
    kind: 'psar', label: 'Parabolic SAR', category: 'Trend', overlay: true,
    summary: 'Wilder\'s stop-and-reverse dots, accelerating toward price while a leg runs.',
    params: [
      { key: 'start', label: 'Start', kind: 'number', min: 0.001, max: 1, step: 0.001 },
      { key: 'step', label: 'Increment', kind: 'number', min: 0.001, max: 1, step: 0.001 },
      { key: 'max', label: 'Maximum', kind: 'number', min: 0.01, max: 1, step: 0.01 },
    ],
    defaults: { start: 0.02, step: 0.02, max: 0.2 },
    caption: (params) => `${params['step']} ${params['max']}`,
    build: (bars, params, tint) => {
      const start = n(params, 'start');
      const step = n(params, 'step');
      const ceiling = n(params, 'max');
      const values: number[] = new Array<number>(bars.length).fill(NaN);
      const first = bars[0];
      const second = bars[1];
      if (!first || !second) { return { plots: [{ key: 'sar', title: 'SAR', color: tint.own, values, shape: 'dots' }] }; }
      let rising = second.close >= first.close;
      let stop = rising ? first.low : first.high;
      let extreme = rising ? second.high : second.low;
      let acceleration = start;
      for (let i = 1; i < bars.length; i++) {
        const bar = bars[i] as ChartBar;
        stop += acceleration * (extreme - stop);
        // The stop may never move inside the last two bars' range.
        const previous = bars[i - 1] as ChartBar;
        if (rising) {
          stop = Math.min(stop, previous.low, bars[i - 2]?.low ?? previous.low);
          if (bar.low < stop) { rising = false; stop = extreme; extreme = bar.low; acceleration = start; }
          else if (bar.high > extreme) { extreme = bar.high; acceleration = Math.min(ceiling, acceleration + step); }
        } else {
          stop = Math.max(stop, previous.high, bars[i - 2]?.high ?? previous.high);
          if (bar.high > stop) { rising = true; stop = extreme; extreme = bar.high; acceleration = start; }
          else if (bar.low < extreme) { extreme = bar.low; acceleration = Math.min(ceiling, acceleration + step); }
        }
        values[i] = stop;
      }
      return { plots: [{ key: 'sar', title: 'SAR', color: tint.own, values, shape: 'dots' }] };
    },
  },
  {
    kind: 'adx', label: 'ADX / DMI', category: 'Trend', overlay: false,
    summary: 'Trend strength, and which side is supplying it. Above 25 is usually read as trending.',
    params: [
      { key: 'length', label: 'DI length', kind: 'number', min: 1, max: 500, step: 1 },
      { key: 'smoothing', label: 'ADX smoothing', kind: 'number', min: 1, max: 500, step: 1 },
    ],
    defaults: { length: 14, smoothing: 14 },
    levels: [20, 25],
    bounds: { min: 0, max: 100 },
    precision: 2,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      const upMoves = bars.map((bar, i) => {
        const previous = bars[i - 1];
        return previous ? bar.high - previous.high : NaN;
      });
      const downMoves = bars.map((bar, i) => {
        const previous = bars[i - 1];
        return previous ? previous.low - bar.low : NaN;
      });
      const directional = (mine: readonly number[], theirs: readonly number[]) =>
        mine.map((value, i) => {
          const other = theirs[i];
          if (!Number.isFinite(value) || other === undefined) { return NaN; }
          return value > other && value > 0 ? value : 0;
        });
      const smoothedRange = rma(trueRange(bars), period);
      const ratio = (moves: readonly number[]) => rma(moves, period).map((value, i) => {
        const total = smoothedRange[i];
        return total !== undefined && Number.isFinite(total) && total !== 0 ? (100 * value) / total : NaN;
      });
      const plus = ratio(directional(upMoves, downMoves));
      const minus = ratio(directional(downMoves, upMoves));
      const index = rma(plus.map((value, i) => {
        const other = minus[i];
        if (other === undefined || !Number.isFinite(value) || !Number.isFinite(other)) { return NaN; }
        const total = value + other;
        return total === 0 ? 0 : (100 * Math.abs(value - other)) / total;
      }), n(params, 'smoothing'));
      return { plots: [
        { key: 'adx', title: 'ADX', color: tint.own, values: index, width: 2 },
        { key: 'plus', title: '+DI', color: tint.next(3), values: plus },
        { key: 'minus', title: '−DI', color: tint.next(2), values: minus },
      ] };
    },
  },
  {
    kind: 'aroon', label: 'Aroon', category: 'Trend', overlay: false,
    summary: 'How recently the window\'s high and low were set — a trend\'s age rather than its size.',
    params: [LENGTH('Length', 1, 500)],
    defaults: { length: 14 },
    levels: [30, 70],
    bounds: { min: 0, max: 100 },
    precision: 2,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      const sinceExtreme = (pick: (a: number, b: number) => boolean, read: (bar: ChartBar) => number) =>
        bars.map((_, i) => {
          if (i < period - 1) { return NaN; }
          let best = read(bars[i] as ChartBar);
          let age = 0;
          for (let back = 1; back < period; back++) {
            const value = read(bars[i - back] as ChartBar);
            if (pick(value, best)) { best = value; age = back; }
          }
          return (100 * (period - 1 - age)) / (period - 1 || 1);
        });
      return { plots: [
        { key: 'up', title: 'Aroon up', color: tint.own, values: sinceExtreme((a, b) => a > b, (bar) => bar.high) },
        { key: 'down', title: 'Aroon down', color: tint.next(2), values: sinceExtreme((a, b) => a < b, (bar) => bar.low) },
      ] };
    },
  },

  // ---- Momentum -----------------------------------------------------------
  {
    kind: 'rsi', label: 'RSI', category: 'Momentum', overlay: false,
    summary: 'Relative strength index on Wilder smoothing. 30 and 70 are the conventional bands.',
    params: [LENGTH('Length', 1, 500), SOURCE],
    defaults: { length: 14, source: 'close' },
    levels: [30, 50, 70],
    bounds: { min: 0, max: 100 },
    precision: 2,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      return { plots: [{ key: 'rsi', title: 'RSI', color: tint.own, width: 2,
        values: rsi(sourceValues(bars, source(params)), n(params, 'length')) }] };
    },
  },
  {
    kind: 'stochastic', label: 'Stochastic', category: 'Momentum', overlay: false,
    summary: 'Where the close sits inside the recent range, smoothed. 20 and 80 are the bands.',
    params: [
      { key: 'length', label: '%K length', kind: 'number', min: 1, max: 500, step: 1 },
      { key: 'smooth', label: '%K smoothing', kind: 'number', min: 1, max: 100, step: 1 },
      { key: 'signal', label: '%D smoothing', kind: 'number', min: 1, max: 100, step: 1 },
    ],
    defaults: { length: 14, smooth: 3, signal: 3 },
    levels: [20, 80],
    bounds: { min: 0, max: 100 },
    precision: 2,
    caption: (params) => `${params['length']} ${params['smooth']} ${params['signal']}`,
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      const top = highest(bars.map((bar) => bar.high), period);
      const bottom = lowest(bars.map((bar) => bar.low), period);
      const raw = bars.map((bar, i) => {
        const spread = (top[i] ?? NaN) - (bottom[i] ?? NaN);
        if (!Number.isFinite(spread)) { return NaN; }
        // A dead-flat window has no range to place the close in; 50 is the midpoint.
        return spread === 0 ? 50 : (100 * (bar.close - (bottom[i] as number))) / spread;
      });
      const k = sma(raw, n(params, 'smooth'));
      return { plots: [
        { key: 'k', title: '%K', color: tint.own, values: k, width: 2 },
        { key: 'd', title: '%D', color: tint.next(2), values: sma(k, n(params, 'signal')) },
      ] };
    },
  },
  {
    kind: 'stochRsi', label: 'Stochastic RSI', category: 'Momentum', overlay: false,
    summary: 'A stochastic of the RSI — faster and noisier than either on its own.',
    params: [
      { key: 'rsiLength', label: 'RSI length', kind: 'number', min: 1, max: 500, step: 1 },
      { key: 'length', label: 'Stochastic length', kind: 'number', min: 1, max: 500, step: 1 },
      { key: 'smooth', label: '%K smoothing', kind: 'number', min: 1, max: 100, step: 1 },
      { key: 'signal', label: '%D smoothing', kind: 'number', min: 1, max: 100, step: 1 },
      SOURCE,
    ],
    defaults: { rsiLength: 14, length: 14, smooth: 3, signal: 3, source: 'close' },
    levels: [20, 80],
    bounds: { min: 0, max: 100 },
    precision: 2,
    caption: (params) => `${params['rsiLength']} ${params['length']} ${params['smooth']} ${params['signal']}`,
    build: (bars, params, tint) => {
      const relative = rsi(sourceValues(bars, source(params)), n(params, 'rsiLength'));
      const period = n(params, 'length');
      const top = highest(relative, period);
      const bottom = lowest(relative, period);
      const raw = relative.map((value, i) => {
        const spread = (top[i] ?? NaN) - (bottom[i] ?? NaN);
        if (!Number.isFinite(spread)) { return NaN; }
        return spread === 0 ? 50 : (100 * (value - (bottom[i] as number))) / spread;
      });
      const k = sma(raw, n(params, 'smooth'));
      return { plots: [
        { key: 'k', title: '%K', color: tint.own, values: k, width: 2 },
        { key: 'd', title: '%D', color: tint.next(2), values: sma(k, n(params, 'signal')) },
      ] };
    },
  },
  {
    kind: 'macd', label: 'MACD', category: 'Momentum', overlay: false,
    summary: 'The gap between two EMAs, its own average, and the difference as a histogram.',
    params: [
      { key: 'fast', label: 'Fast length', kind: 'number', min: 1, max: 500, step: 1 },
      { key: 'slow', label: 'Slow length', kind: 'number', min: 1, max: 1000, step: 1 },
      { key: 'signal', label: 'Signal length', kind: 'number', min: 1, max: 500, step: 1 },
      SOURCE,
    ],
    defaults: { fast: 12, slow: 26, signal: 9, source: 'close' },
    levels: [0],
    caption: (params) => `${params['fast']} ${params['slow']} ${params['signal']}`,
    build: (bars, params, tint) => {
      const prices = sourceValues(bars, source(params));
      const fast = ema(prices, n(params, 'fast'));
      const slow = ema(prices, n(params, 'slow'));
      const line = fast.map((value, i) => value - (slow[i] ?? NaN));
      const signal = ema(line, n(params, 'signal'));
      return { plots: [
        { key: 'histogram', title: 'Histogram', color: tint.own, shape: 'histogram', signColors: true,
          values: line.map((value, i) => value - (signal[i] ?? NaN)) },
        { key: 'macd', title: 'MACD', color: tint.own, values: line, width: 2 },
        { key: 'signal', title: 'Signal', color: tint.next(2), values: signal },
      ] };
    },
  },
  {
    kind: 'cci', label: 'CCI', category: 'Momentum', overlay: false,
    summary: 'Commodity channel index — how far the typical price has strayed from its mean.',
    params: [LENGTH('Length', 1, 500)],
    defaults: { length: 20 },
    levels: [-100, 0, 100],
    precision: 2,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      const typical = sourceValues(bars, 'hlc3');
      const mean = sma(typical, period);
      const values = typical.map((_, i) => {
        const average = mean[i];
        if (average === undefined || !Number.isFinite(average)) { return NaN; }
        let deviation = 0;
        for (let back = 0; back < period; back++) { deviation += Math.abs((typical[i - back] as number) - average); }
        deviation /= period;
        return deviation === 0 ? 0 : ((typical[i] as number) - average) / (0.015 * deviation);
      });
      return { plots: [{ key: 'cci', title: 'CCI', color: tint.own, values, width: 2 }] };
    },
  },
  {
    kind: 'williamsR', label: 'Williams %R', category: 'Momentum', overlay: false,
    summary: 'The inverse stochastic, on a −100 to 0 scale. −20 and −80 are the bands.',
    params: [LENGTH('Length', 1, 500)],
    defaults: { length: 14 },
    levels: [-80, -20],
    bounds: { min: -100, max: 0 },
    precision: 2,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const period = n(params, 'length');
      const top = highest(bars.map((bar) => bar.high), period);
      const bottom = lowest(bars.map((bar) => bar.low), period);
      return { plots: [{ key: 'wpr', title: '%R', color: tint.own, width: 2,
        values: bars.map((bar, i) => {
          const spread = (top[i] ?? NaN) - (bottom[i] ?? NaN);
          if (!Number.isFinite(spread)) { return NaN; }
          return spread === 0 ? -50 : (-100 * ((top[i] as number) - bar.close)) / spread;
        }) }] };
    },
  },
  {
    kind: 'roc', label: 'Rate of change', category: 'Momentum', overlay: false,
    summary: 'Percentage change over N bars — momentum in units that compare across instruments.',
    params: [LENGTH('Length', 1, 1000), SOURCE],
    defaults: { length: 9, source: 'close' },
    levels: [0],
    precision: 2,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const prices = sourceValues(bars, source(params));
      const back = n(params, 'length');
      return { plots: [{ key: 'roc', title: 'ROC %', color: tint.own, width: 2,
        values: prices.map((value, i) => {
          const earlier = prices[i - back];
          return earlier !== undefined && Number.isFinite(earlier) && earlier !== 0 ? (100 * (value - earlier)) / earlier : NaN;
        }) }] };
    },
  },
  {
    kind: 'momentum', label: 'Momentum', category: 'Momentum', overlay: false,
    summary: 'The raw price difference over N bars, in the instrument\'s own units.',
    params: [LENGTH('Length', 1, 1000), SOURCE],
    defaults: { length: 10, source: 'close' },
    levels: [0],
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      return { plots: [{ key: 'mom', title: 'Momentum', color: tint.own, width: 2,
        values: change(sourceValues(bars, source(params)), n(params, 'length')) }] };
    },
  },
  {
    kind: 'ao', label: 'Awesome Oscillator', category: 'Momentum', overlay: false,
    summary: 'A 5-bar median against a 34-bar one, as a histogram. Sign changes are the signal.',
    params: [
      { key: 'fast', label: 'Fast length', kind: 'number', min: 1, max: 500, step: 1 },
      { key: 'slow', label: 'Slow length', kind: 'number', min: 1, max: 1000, step: 1 },
    ],
    defaults: { fast: 5, slow: 34 },
    levels: [0],
    caption: (params) => `${params['fast']} ${params['slow']}`,
    build: (bars, params, tint) => {
      const median = sourceValues(bars, 'hl2');
      const fast = sma(median, n(params, 'fast'));
      const slow = sma(median, n(params, 'slow'));
      return { plots: [{ key: 'ao', title: 'AO', color: tint.own, shape: 'histogram', signColors: true,
        values: fast.map((value, i) => value - (slow[i] ?? NaN)) }] };
    },
  },

  // ---- Volatility ---------------------------------------------------------
  {
    kind: 'atr', label: 'ATR', category: 'Volatility', overlay: false,
    summary: 'Average true range — typical bar size, gaps included. The usual stop-distance unit.',
    params: [LENGTH('Length', 1, 500)],
    defaults: { length: 14 },
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      return { plots: [{ key: 'atr', title: 'ATR', color: tint.own, width: 2, values: atr(bars, n(params, 'length')) }] };
    },
  },
  {
    kind: 'stdev', label: 'Standard deviation', category: 'Volatility', overlay: false,
    summary: 'Dispersion of the source around its own mean over N bars.',
    params: [LENGTH('Length', 1, 1000), SOURCE],
    defaults: { length: 20, source: 'close' },
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      return { plots: [{ key: 'stdev', title: 'StdDev', color: tint.own, width: 2,
        values: stdev(sourceValues(bars, source(params)), n(params, 'length')) }] };
    },
  },

  // ---- Volume -------------------------------------------------------------
  {
    kind: 'obv', label: 'On-balance volume', category: 'Volume', overlay: false,
    summary: 'Volume added on up closes and subtracted on down closes — accumulation as a running total.',
    params: [],
    defaults: {},
    precision: 0,
    build: (bars, _params, tint) => {
      const volume = volumes(bars);
      let total = 0;
      return { plots: [{ key: 'obv', title: 'OBV', color: tint.own, width: 2,
        values: bars.map((bar, i) => {
          const previous = bars[i - 1];
          if (previous) { total += bar.close > previous.close ? (volume[i] ?? 0) : bar.close < previous.close ? -(volume[i] ?? 0) : 0; }
          return total;
        }) }] };
    },
  },
  {
    kind: 'mfi', label: 'Money flow index', category: 'Volume', overlay: false,
    summary: 'A volume-weighted RSI: which side of the range the money went to. 20 and 80 are the bands.',
    params: [LENGTH('Length', 1, 500)],
    defaults: { length: 14 },
    levels: [20, 80],
    bounds: { min: 0, max: 100 },
    precision: 2,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const typical = sourceValues(bars, 'hlc3');
      const volume = volumes(bars);
      const flow = (sign: number) => rollingSum(typical.map((value, i) => {
        const previous = typical[i - 1];
        if (previous === undefined) { return NaN; }
        return Math.sign(value - previous) === sign ? value * (volume[i] ?? 0) : 0;
      }), n(params, 'length'));
      const positive = flow(1);
      const negative = flow(-1);
      return { plots: [{ key: 'mfi', title: 'MFI', color: tint.own, width: 2,
        values: positive.map((value, i) => {
          const down = negative[i];
          if (down === undefined || !Number.isFinite(value) || !Number.isFinite(down)) { return NaN; }
          // No down-flow in the window means the formula's ratio is unbounded; 100 is its limit.
          return down === 0 ? (value === 0 ? 50 : 100) : 100 - 100 / (1 + value / down);
        }) }] };
    },
  },
  {
    kind: 'cmf', label: 'Chaikin Money Flow', category: 'Volume', overlay: false,
    summary: 'Where in each bar\'s range the close landed, volume-weighted over N bars.',
    params: [LENGTH('Length', 1, 500)],
    defaults: { length: 20 },
    levels: [0],
    bounds: { min: -1, max: 1 },
    precision: 3,
    caption: (params) => String(params['length']),
    build: (bars, params, tint) => {
      const volume = volumes(bars);
      const period = n(params, 'length');
      const flow = rollingSum(bars.map((bar, i) => {
        const range = bar.high - bar.low;
        return range === 0 ? 0 : (((bar.close - bar.low) - (bar.high - bar.close)) / range) * (volume[i] ?? 0);
      }), period);
      const traded = rollingSum(volume, period);
      return { plots: [{ key: 'cmf', title: 'CMF', color: tint.own, width: 2,
        values: flow.map((value, i) => {
          const total = traded[i];
          return total !== undefined && Number.isFinite(total) && total > 0 ? value / total : NaN;
        }) }] };
    },
  },
];

export type IndicatorKind =
  | 'sma' | 'ema' | 'wma' | 'hma' | 'dema' | 'tema' | 'vwma' | 'vwap'
  | 'bollinger' | 'keltner' | 'donchian' | 'envelope' | 'ichimoku'
  | 'supertrend' | 'psar' | 'adx' | 'aroon'
  | 'rsi' | 'stochastic' | 'stochRsi' | 'macd' | 'cci' | 'williamsR' | 'roc' | 'momentum' | 'ao'
  | 'atr' | 'stdev'
  | 'obv' | 'mfi' | 'cmf';

const BY_KIND = new Map(INDICATORS.map((definition) => [definition.kind, definition]));

/** The definition for a kind, or `undefined` for a kind that no longer exists. */
export function indicatorFor(kind: string): IndicatorDefinition | undefined {
  return BY_KIND.get(kind as IndicatorKind);
}

/** "SMA 20", "MACD 12 26 9" — the name the legend, the picker and the chip all show. */
export function studyLabel(kind: IndicatorKind, params: StudyParams): string {
  const definition = indicatorFor(kind);
  if (!definition) { return kind; }
  const caption = definition.caption?.(normalizeParams(definition, params));
  return caption ? `${definition.label} ${caption}` : definition.label;
}

/**
 * A new instance of an indicator, coloured so it does not land on top of one
 * already on the chart: the palette is stepped through by how many studies are
 * already there, which is why two SMAs come out two different colours without
 * the user touching anything.
 */
export function newStudy(kind: IndicatorKind, existing: readonly StudyInstance[]): StudyInstance | undefined {
  const definition = indicatorFor(kind);
  if (!definition) { return undefined; }
  const token = STUDY_COLORS[existing.length % STUDY_COLORS.length]?.token ?? STUDY_COLORS[0]!.token;
  return {
    // `crypto.randomUUID` needs a secure context and this only has to be
    // unique within one chart's study list, which a counter plus the clock is.
    id: `${kind}-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6).toString(36)}`,
    kind,
    params: { ...definition.defaults },
    color: token,
    visible: true,
  };
}

/**
 * Narrows one entry out of saved preferences. Anything unrecognised — a kind
 * that has since been removed, a hand-edited file, a colour that is not in the
 * palette — is rejected rather than half-restored, and the parameters are
 * re-clamped by each `build` anyway.
 */
export function validStudy(value: unknown): value is StudyInstance {
  if (!value || typeof value !== 'object') { return false; }
  const study = value as Partial<StudyInstance>;
  return typeof study.id === 'string' && study.id.length > 0
    && typeof study.kind === 'string' && indicatorFor(study.kind) !== undefined
    && typeof study.color === 'string' && STUDY_COLORS.some((entry) => entry.token === study.color)
    && typeof study.visible === 'boolean'
    && !!study.params && typeof study.params === 'object';
}

/**
 * Every visible study's plots, over these bars.
 *
 * A study whose `build` throws is DROPPED, not allowed to take the chart with
 * it: a saved parameter combination that divides by zero somewhere should cost
 * one indicator, not the whole screen.
 */
export function calculateStudies(bars: readonly ChartBar[], studies: readonly StudyInstance[]): StudyResult[] {
  const results: StudyResult[] = [];
  for (const study of studies) {
    const definition = indicatorFor(study.kind);
    if (!definition || !study.visible) { continue; }
    const params = normalizeParams(definition, study.params);
    try {
      const { plots, fills } = definition.build(bars, params, tintFrom(study.color));
      results.push({
        id: study.id,
        kind: study.kind,
        label: studyLabel(study.kind, params),
        overlay: definition.overlay,
        plots,
        fills: fills ?? [],
        levels: definition.levels ?? [],
        bounds: definition.bounds,
        precision: definition.precision,
      });
    } catch {
      continue;
    }
  }
  return results;
}

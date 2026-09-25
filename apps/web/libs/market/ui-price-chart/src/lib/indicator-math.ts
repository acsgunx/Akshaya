import type { ChartBar } from './candle-bucket';

/**
 * The numeric primitives every indicator is built from.
 *
 * ============================================================================
 * THE WARM-UP CONTRACT
 * ============================================================================
 * Every function here returns an array the SAME LENGTH as its input, aligned
 * index-for-index with the bars, and writes `NaN` wherever a value does not
 * exist yet. Nothing is dropped and nothing is shifted.
 *
 * That alignment is the whole reason these are arrays rather than the
 * `{time, value}` point lists the chart eventually draws. A chart legend has
 * to answer "what was RSI on the bar under the cursor" in constant time, a
 * live tick has to update the last slot of twenty studies without rebuilding
 * them, and an indicator built on another indicator (MACD's signal, Hull's
 * inner averages, Stochastic RSI) has to line up with its input exactly. Every
 * one of those is an index lookup here and a bug in a sparse representation.
 *
 * `NaN` propagates: a window containing one is incomplete, so it produces
 * `NaN` rather than an average of the rest. That keeps a study's first plotted
 * point honest — the point where it has genuinely seen enough history — instead
 * of showing a 20-period average computed from three bars.
 */

/** Which price a study reads. `hl2`/`hlc3`/`ohlc4` are the usual averaged sources. */
export type PriceSource = 'close' | 'open' | 'high' | 'low' | 'hl2' | 'hlc3' | 'ohlc4';

export const PRICE_SOURCES: readonly { value: PriceSource; label: string }[] = [
  { value: 'close', label: 'Close' },
  { value: 'open', label: 'Open' },
  { value: 'high', label: 'High' },
  { value: 'low', label: 'Low' },
  { value: 'hl2', label: 'Median (H+L)/2' },
  { value: 'hlc3', label: 'Typical (H+L+C)/3' },
  { value: 'ohlc4', label: 'Average (O+H+L+C)/4' },
];

/** One bar's chosen price, per `PriceSource`. */
export function sourceValues(bars: readonly ChartBar[], source: PriceSource): number[] {
  return bars.map((bar) => {
    switch (source) {
      case 'open': return bar.open;
      case 'high': return bar.high;
      case 'low': return bar.low;
      case 'hl2': return (bar.high + bar.low) / 2;
      case 'hlc3': return (bar.high + bar.low + bar.close) / 3;
      case 'ohlc4': return (bar.open + bar.high + bar.low + bar.close) / 4;
      default: return bar.close;
    }
  });
}

/** An all-`NaN` result of the right length — the answer for a period longer than the history. */
function blank(length: number): number[] {
  return new Array<number>(length).fill(NaN);
}

function finite(value: number | undefined): value is number {
  return value !== undefined && Number.isFinite(value);
}

/**
 * Simple moving average.
 *
 * The window sum is carried rather than recomputed, and `filled` counts the
 * finite values inside it — so a window straddling another study's warm-up
 * `NaN`s reports `NaN` instead of dividing a short sum by the full period.
 */
export function sma(values: readonly number[], period: number): number[] {
  const out = blank(values.length);
  if (period < 1) { return out; }
  let sum = 0;
  let filled = 0;
  for (let i = 0; i < values.length; i++) {
    const entering = values[i];
    if (finite(entering)) { sum += entering; filled++; }
    const leaving = values[i - period];
    if (finite(leaving)) { sum -= leaving; filled--; }
    if (i >= period - 1 && filled === period) { out[i] = sum / period; }
  }
  return out;
}

/** Exponential moving average, seeded with the simple average of its first full window. */
export function ema(values: readonly number[], period: number): number[] {
  const out = blank(values.length);
  if (period < 1) { return out; }
  const weight = 2 / (period + 1);
  let previous = NaN;
  let seed = 0;
  let seeded = 0;
  for (let i = 0; i < values.length; i++) {
    const value = values[i];
    if (!finite(value)) { continue; }
    if (Number.isFinite(previous)) {
      previous += weight * (value - previous);
    } else {
      seed += value;
      if (++seeded < period) { continue; }
      previous = seed / period;
    }
    out[i] = previous;
  }
  return out;
}

/**
 * Wilder's smoothing — an EMA with `1/period` as its weight rather than
 * `2/(period+1)`. RSI, ATR and ADX are all defined on this one, and using a
 * plain EMA instead is the single most common way an indicator ends up
 * disagreeing with every other platform by a few points.
 */
export function rma(values: readonly number[], period: number): number[] {
  const out = blank(values.length);
  if (period < 1) { return out; }
  let previous = NaN;
  let seed = 0;
  let seeded = 0;
  for (let i = 0; i < values.length; i++) {
    const value = values[i];
    if (!finite(value)) { continue; }
    if (Number.isFinite(previous)) {
      previous += (value - previous) / period;
    } else {
      seed += value;
      if (++seeded < period) { continue; }
      previous = seed / period;
    }
    out[i] = previous;
  }
  return out;
}

/** Linearly weighted moving average — the newest bar carries `period` times the oldest one's weight. */
export function wma(values: readonly number[], period: number): number[] {
  const out = blank(values.length);
  if (period < 1) { return out; }
  const divisor = (period * (period + 1)) / 2;
  for (let i = period - 1; i < values.length; i++) {
    let weighted = 0;
    let ok = true;
    for (let back = 0; back < period && ok; back++) {
      const value = values[i - back];
      if (!finite(value)) { ok = false; break; }
      weighted += value * (period - back);
    }
    if (ok) { out[i] = weighted / divisor; }
  }
  return out;
}

/**
 * Hull moving average: `WMA(2·WMA(n/2) − WMA(n), √n)`. Much faster to turn
 * than an EMA of the same length, which is the point of it.
 */
export function hma(values: readonly number[], period: number): number[] {
  const half = Math.max(1, Math.round(period / 2));
  const root = Math.max(1, Math.round(Math.sqrt(period)));
  const fast = wma(values, half);
  const slow = wma(values, period);
  return wma(fast.map((value, i) => 2 * value - (slow[i] ?? NaN)), root);
}

/** Double EMA: `2·EMA − EMA(EMA)`, one lag-reduction step. */
export function dema(values: readonly number[], period: number): number[] {
  const first = ema(values, period);
  const second = ema(first, period);
  return first.map((value, i) => 2 * value - (second[i] ?? NaN));
}

/** Triple EMA: `3·EMA − 3·EMA(EMA) + EMA(EMA(EMA))`. */
export function tema(values: readonly number[], period: number): number[] {
  const first = ema(values, period);
  const second = ema(first, period);
  const third = ema(second, period);
  return first.map((value, i) => 3 * value - 3 * (second[i] ?? NaN) + (third[i] ?? NaN));
}

/** Volume-weighted moving average. Falls to `NaN` on a window whose volume is all zero. */
export function vwma(values: readonly number[], volumes: readonly number[], period: number): number[] {
  const weighted = sma(values.map((value, i) => value * (volumes[i] ?? NaN)), period);
  const weight = sma(volumes, period);
  return weighted.map((value, i) => {
    const total = weight[i];
    return finite(total) && total > 0 ? value / total : NaN;
  });
}

/**
 * Population standard deviation over a rolling window.
 *
 * Deliberately a direct two-pass window rather than the textbook
 * `E[x²] − E[x]²` running form: on index-level prices, the sum of squares is
 * around `1e9` and the variance a few units, so the subtraction loses most of
 * its significant digits and Bollinger Bands come out visibly wrong (or with a
 * negative variance). `period` extra multiplications per bar is nothing next
 * to being right.
 */
export function stdev(values: readonly number[], period: number): number[] {
  const out = blank(values.length);
  const mean = sma(values, period);
  for (let i = period - 1; i < values.length; i++) {
    const average = mean[i];
    if (!finite(average)) { continue; }
    let total = 0;
    for (let back = 0; back < period; back++) {
      total += ((values[i - back] as number) - average) ** 2;
    }
    out[i] = Math.sqrt(total / period);
  }
  return out;
}

/** Highest value in each rolling window. */
export function highest(values: readonly number[], period: number): number[] {
  return rollingExtreme(values, period, Math.max);
}

/** Lowest value in each rolling window. */
export function lowest(values: readonly number[], period: number): number[] {
  return rollingExtreme(values, period, Math.min);
}

function rollingExtreme(values: readonly number[], period: number, pick: (a: number, b: number) => number): number[] {
  const out = blank(values.length);
  if (period < 1) { return out; }
  for (let i = period - 1; i < values.length; i++) {
    let result = NaN;
    let ok = true;
    for (let back = 0; back < period; back++) {
      const value = values[i - back];
      if (!finite(value)) { ok = false; break; }
      result = Number.isFinite(result) ? pick(result, value) : value;
    }
    if (ok) { out[i] = result; }
  }
  return out;
}

/** Rolling sum, `NaN` on any incomplete or `NaN`-containing window. */
export function rollingSum(values: readonly number[], period: number): number[] {
  return sma(values, period).map((value) => value * period);
}

/** Difference from `back` bars ago, aligned to the later bar. */
export function change(values: readonly number[], back = 1): number[] {
  return values.map((value, i) => {
    const previous = values[i - back];
    return finite(previous) && finite(value) ? value - previous : NaN;
  });
}

/** True range per bar: the widest of today's range and either gap against yesterday's close. */
export function trueRange(bars: readonly ChartBar[]): number[] {
  return bars.map((bar, i) => {
    const previous = bars[i - 1];
    if (!previous) { return bar.high - bar.low; }
    return Math.max(bar.high - bar.low, Math.abs(bar.high - previous.close), Math.abs(bar.low - previous.close));
  });
}

/** Average true range — Wilder-smoothed true range. */
export function atr(bars: readonly ChartBar[], period: number): number[] {
  return rma(trueRange(bars), period);
}

/** Relative strength index on Wilder smoothing, in 0-100. */
export function rsi(values: readonly number[], period: number): number[] {
  const differences = change(values);
  const gains = rma(differences.map((value) => (finite(value) ? Math.max(value, 0) : NaN)), period);
  const losses = rma(differences.map((value) => (finite(value) ? Math.max(-value, 0) : NaN)), period);
  return gains.map((gain, i) => {
    const loss = losses[i];
    if (!finite(gain) || !finite(loss)) { return NaN; }
    // An unbroken run of up bars has no loss to divide by. 100 is the limit of
    // the formula, and 50 is the only defensible answer for a flat series.
    if (loss === 0) { return gain === 0 ? 50 : 100; }
    return 100 - 100 / (1 + gain / loss);
  });
}

/** The bars' volume, zero where a broker reported none. */
export function volumes(bars: readonly ChartBar[]): number[] {
  return bars.map((bar) => (finite(bar.volume) ? Math.max(0, bar.volume) : 0));
}

/**
 * Index of the first bar of each session, for the session-anchored studies
 * (VWAP is the only one today).
 *
 * The venue calendar is NOT guessed at — see the note on `BUCKET_SECONDS`.
 * What this reads instead is the spacing the broker's own bars arrived at: a
 * session's bars are evenly spaced, and an overnight or weekend gap is several
 * times that spacing. Anything past 1.5x the median spacing starts a new
 * session, which needs no calendar and is right for every venue.
 *
 * On daily and longer frames every bar would be its own session, which would
 * make a session VWAP identical to the typical price. There, the whole loaded
 * range is treated as one anchor instead, and the study says so in its label.
 */
export function sessionStarts(bars: readonly ChartBar[]): { readonly starts: readonly boolean[]; readonly perSession: boolean } {
  const starts = bars.map((_, i) => i === 0);
  const gaps: number[] = [];
  for (let i = 1; i < bars.length; i++) {
    gaps.push((bars[i] as ChartBar).time - (bars[i - 1] as ChartBar).time);
  }
  if (gaps.length === 0) { return { starts, perSession: false }; }
  const sorted = [...gaps].sort((a, b) => a - b);
  const median = sorted[Math.floor(sorted.length / 2)] ?? 0;
  if (median >= 86400) {
    // Daily bars or longer: one anchor for the whole loaded window.
    return { starts, perSession: false };
  }
  for (let i = 1; i < bars.length; i++) {
    if ((bars[i] as ChartBar).time - (bars[i - 1] as ChartBar).time > median * 1.5) { starts[i] = true; }
  }
  return { starts, perSession: true };
}

import type { ChartBar } from './candle-bucket';

export type ChartType = 'candles' | 'hollow' | 'bars' | 'line' | 'area';
export type StudyId = 'sma' | 'ema' | 'bollinger' | 'rsi' | 'macd' | 'stochRsi';
export interface StudyPoint { readonly time: number; readonly value: number }
export interface StudyPlot {
  readonly title: string;
  readonly points: readonly StudyPoint[];
  readonly color: 'brand' | 'buy' | 'sell';
  readonly histogram?: boolean;
}
export interface StudyResult {
  readonly id: StudyId;
  readonly pane: boolean;
  readonly levels?: readonly number[];
  readonly plots: readonly StudyPlot[];
}

export const CHART_TYPES: readonly { id: ChartType; label: string; icon: string }[] = [
  { id: 'candles', label: 'Candles', icon: 'candlestick_chart' },
  { id: 'hollow', label: 'Hollow candles', icon: 'bar_chart' },
  { id: 'bars', label: 'OHLC bars', icon: 'align_vertical_center' },
  { id: 'line', label: 'Line', icon: 'show_chart' },
  { id: 'area', label: 'Area', icon: 'area_chart' },
];

export const STUDIES: readonly { id: StudyId; label: string; detail: string }[] = [
  { id: 'sma', label: 'SMA 20', detail: 'Simple moving average · close' },
  { id: 'ema', label: 'EMA 50', detail: 'Exponential moving average · close' },
  { id: 'bollinger', label: 'Bollinger Bands', detail: '20 periods · 2 standard deviations' },
  { id: 'rsi', label: 'RSI 14', detail: 'Wilder smoothing · levels 30 / 70' },
  { id: 'macd', label: 'MACD', detail: 'EMA 12 / 26 · signal 9' },
  { id: 'stochRsi', label: 'Stochastic RSI', detail: 'RSI 14 · stochastic 14 · K 3 / D 3' },
];

function sma(values: readonly number[], period: number): number[] {
  let sum = 0;
  let count = 0;
  return values.map((value, index) => {
    if (Number.isFinite(value)) { sum += value; count++; }
    const expired = values[index - period];
    if (expired !== undefined && Number.isFinite(expired)) { sum -= expired; count--; }
    return count === period ? sum / period : NaN;
  });
}

function ema(values: readonly number[], period: number): number[] {
  let previous = NaN;
  let sum = 0;
  let count = 0;
  const weight = 2 / (period + 1);
  return values.map((value) => {
    if (!Number.isFinite(value)) { return NaN; }
    if (!Number.isFinite(previous)) {
      sum += value;
      count++;
      if (count < period) { return NaN; }
      previous = sum / period;
    } else {
      previous += weight * (value - previous);
    }
    return previous;
  });
}

function rsi(values: readonly number[], period: number): number[] {
  let gain = 0;
  let loss = 0;
  return values.map((value, index) => {
    const previous = values[index - 1];
    if (previous === undefined) { return NaN; }
    const change = value - previous;
    if (index <= period) {
      gain += Math.max(change, 0) / period;
      loss += Math.max(-change, 0) / period;
    } else {
      gain = (gain * (period - 1) + Math.max(change, 0)) / period;
      loss = (loss * (period - 1) + Math.max(-change, 0)) / period;
    }
    return index < period ? NaN : loss === 0 ? (gain === 0 ? 50 : 100) : 100 - 100 / (1 + gain / loss);
  });
}

export function calculateStudies(bars: readonly ChartBar[], enabled: readonly StudyId[]): StudyResult[] {
  const closes = bars.map((bar) => bar.close);
  const plot = (title: string, values: readonly number[], color: StudyPlot['color'], histogram = false): StudyPlot => ({
    title, color, histogram,
    points: bars.flatMap((bar, i) => {
      const value = values[i];
      return value !== undefined && Number.isFinite(value) ? [{ time: bar.time, value }] : [];
    }),
  });
  return enabled.map((id): StudyResult => {
    switch (id) {
      case 'sma': return { id, pane: false, plots: [plot('SMA 20', sma(closes, 20), 'brand')] };
      case 'ema': return { id, pane: false, plots: [plot('EMA 50', ema(closes, 50), 'sell')] };
      case 'bollinger': {
        const middle = sma(closes, 20);
        const deviation = closes.map((_, index) => {
          const mean = middle[index];
          return mean === undefined || !Number.isFinite(mean) ? NaN
            : Math.sqrt(closes.slice(index - 19, index + 1).reduce((sum, close) => sum + (close - mean) ** 2, 0) / 20) * 2;
        });
        return { id, pane: false, plots: [
          plot('BB upper', middle.map((value, i) => value + (deviation[i] ?? NaN)), 'buy'),
          plot('BB basis', middle, 'brand'),
          plot('BB lower', middle.map((value, i) => value - (deviation[i] ?? NaN)), 'sell'),
        ] };
      }
      case 'rsi': return { id, pane: true, levels: [30, 70], plots: [plot('RSI 14', rsi(closes, 14), 'brand')] };
      case 'macd': {
        const fast = ema(closes, 12);
        const slow = ema(closes, 26);
        const macd = fast.map((value, i) => value - (slow[i] ?? NaN));
        const signal = ema(macd, 9);
        return { id, pane: true, levels: [0], plots: [
          plot('Histogram', macd.map((value, i) => value - (signal[i] ?? NaN)), 'buy', true),
          plot('MACD', macd, 'buy'), plot('Signal', signal, 'sell'),
        ] };
      }
      case 'stochRsi': {
        const relative = rsi(closes, 14);
        const stochastic = relative.map((value, index) => {
          if (index < 27) { return NaN; }
          const window = relative.slice(index - 13, index + 1);
          const low = Math.min(...window);
          const spread = Math.max(...window) - low;
          return spread === 0 ? 0 : 100 * (value - low) / spread;
        });
        const k = sma(stochastic, 3);
        return { id, pane: true, levels: [20, 80], plots: [plot('Stoch RSI K', k, 'buy'), plot('Stoch RSI D', sma(k, 3), 'sell')] };
      }
    }
  });
}

import type { ChartBar } from './candle-bucket';

/** The forms the price itself can be drawn in. */
export type ChartType = 'candles' | 'hollow' | 'heikinAshi' | 'bars' | 'line' | 'area' | 'baseline';

export const CHART_TYPES: readonly { id: ChartType; label: string; icon: string; detail: string }[] = [
  { id: 'candles', label: 'Candles', icon: 'candlestick_chart', detail: 'Open, high, low and close as a filled body' },
  { id: 'hollow', label: 'Hollow candles', icon: 'bar_chart', detail: 'Up bars drawn as outlines only' },
  { id: 'heikinAshi', label: 'Heikin Ashi', icon: 'view_week', detail: 'Averaged bars — trend over the individual print' },
  { id: 'bars', label: 'OHLC bars', icon: 'align_vertical_center', detail: 'Western bars with open and close ticks' },
  { id: 'line', label: 'Line', icon: 'show_chart', detail: 'Closing price only' },
  { id: 'area', label: 'Area', icon: 'area_chart', detail: 'Closing price with a filled body' },
  { id: 'baseline', label: 'Baseline', icon: 'stacked_line_chart', detail: 'Shaded above and below the first loaded close' },
];

/**
 * The Heikin Ashi transform: each bar averaged into the one before it.
 *
 *     close = (O + H + L + C) / 4
 *     open  = (previous HA open + previous HA close) / 2
 *     high  = max(H, HA open, HA close)
 *     low   = min(L, HA open, HA close)
 *
 * The result is a smoothed series that makes a trend obvious and individual
 * prints unreadable — which is the trade it exists to make. Two things follow
 * from that, and both are deliberate:
 *
 * - **A Heikin Ashi bar's open and close are not prices anything traded at.**
 *   The crosshair readout, the legend's O/H/L/C and the order ticket all keep
 *   showing the REAL bar, so nobody places an order against an averaged number.
 * - **Indicators are computed on the real bars, not on these.** An RSI of a
 *   twice-smoothed series reads calmer than the instrument actually is, and the
 *   number would not match the same study on any other view of the same data.
 *
 * `seed` carries the previous bar's HA open and close, so a live tick can
 * re-derive the forming bar without walking the whole series again.
 */
export function heikinAshi(bars: readonly ChartBar[]): ChartBar[] {
  let previousOpen = NaN;
  let previousClose = NaN;
  return bars.map((bar) => {
    const close = (bar.open + bar.high + bar.low + bar.close) / 4;
    const open = Number.isFinite(previousOpen) ? (previousOpen + previousClose) / 2 : (bar.open + bar.close) / 2;
    previousOpen = open;
    previousClose = close;
    return {
      time: bar.time,
      open,
      close,
      high: Math.max(bar.high, open, close),
      low: Math.min(bar.low, open, close),
      volume: bar.volume,
    };
  });
}

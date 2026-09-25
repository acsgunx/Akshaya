import type { Candle } from '@akshaya/shared/models';

import type { ChartBar } from './candle-bucket';

/**
 * Broker candles turned into bars the library will actually draw: one per
 * time, strictly ascending, each internally consistent.
 *
 * Keyed by time, last one wins. The library requires STRICTLY ascending time
 * and throws on the first repeat — leaving the chart blank, not missing one
 * bar. Brokers do repeat bars: mStock sends some daily candles twice,
 * verbatim. The connector collapses those too; this is the net under the next
 * broker that finds a new way to do it.
 *
 * Sorted whatever order the connector returned them in: newest-first (or
 * unsorted) must not silently render an empty chart either.
 */
export function normalizeCandles(candles: readonly Candle[]): ChartBar[] {
  const byTime = new Map<number, Candle>();
  for (const candle of candles) {
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

  return [...byTime].sort(([a], [b]) => a - b).map(([time, candle]) => ({
    time,
    open: candle.open,
    high: candle.high,
    low: candle.low,
    close: candle.close,
    volume: Number.isFinite(candle.volume) ? Math.max(0, candle.volume) : 0,
  }));
}

/** The loaded bars as CSV, times in UTC ISO-8601 — what "Download OHLC CSV" saves. */
export function barsToCsv(bars: readonly ChartBar[]): string {
  const rows = bars.map((bar) => [new Date(bar.time * 1000).toISOString(),
    bar.open, bar.high, bar.low, bar.close, bar.volume].join(','));
  return ['Time (UTC),Open,High,Low,Close,Volume', ...rows].join('\r\n');
}

/** Hands a file to the browser's download, with a name safe on every OS. */
export function downloadFile(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = name.replace(/[^a-zA-Z0-9._-]/g, '_');
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

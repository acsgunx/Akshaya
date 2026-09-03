import type { TimeFrame } from '../../core/models';

/**
 * Bucket width, in seconds, for the timeframes that HAVE a fixed one.
 *
 * `oneDay`, `oneWeek` and `oneMonth` are deliberately absent. A daily bar's
 * boundary is a *venue session* boundary, not a UTC midnight — the NSE's
 * trading day and the NYSE's start at different instants, and a week or a
 * month is not a constant number of seconds at all. Flooring those from the
 * epoch would draw a bar that begins in the middle of somebody's session,
 * which is a wrong chart presented with full confidence. The venue calendar
 * lives in `venue-state.service.ts` and on the backend; the chart does not
 * get to guess at it. See `foldTickIntoBar` for what happens instead.
 *
 * Every value below divides an hour or a day evenly, and the unix epoch
 * begins on a UTC hour boundary, so flooring an epoch-second by these widths
 * lands exactly on a real bar boundary.
 */
const BUCKET_SECONDS: Partial<Record<TimeFrame, number>> = {
  oneMinute: 60,
  threeMinutes: 180,
  fiveMinutes: 300,
  fifteenMinutes: 900,
  thirtyMinutes: 1800,
  oneHour: 3600,
};

/** Bucket width in seconds, or `undefined` for the session-relative frames. */
export function bucketSeconds(timeFrame: TimeFrame): number | undefined {
  return BUCKET_SECONDS[timeFrame];
}

/**
 * Start of the bucket an epoch-second falls in, or `undefined` when the
 * timeframe has no fixed width (see `BUCKET_SECONDS`).
 */
export function bucketStart(epochSeconds: number, timeFrame: TimeFrame): number | undefined {
  const width = bucketSeconds(timeFrame);
  return width === undefined ? undefined : Math.floor(epochSeconds / width) * width;
}

/** An OHLC bar in the shape Lightweight Charts consumes (`time` in epoch SECONDS). */
export interface ChartBar {
  readonly time: number;
  readonly open: number;
  readonly high: number;
  readonly low: number;
  readonly close: number;
}

/**
 * Folds one live price into the bar series, returning the single bar that
 * should be handed to `series.update()` — or `undefined` when the tick
 * belongs before the bars we already hold and would move the series
 * backwards (Lightweight Charts rejects an out-of-order update).
 *
 * Two behaviours, and the difference matters:
 *
 * - **Fixed-width frames** (1m…1h): a tick past the current bar's window
 *   OPENS A NEW BAR at the floored boundary, so an intraday chart keeps
 *   forming bars live rather than piling an hour of ticks onto one candle.
 * - **Session-relative frames** (1d/1w/1M): the tick only ever updates the
 *   LAST bar's high/low/close. It never rolls to a new one, because "is this
 *   tick a new trading day" is a venue-calendar question this file has
 *   deliberately not answered (see `BUCKET_SECONDS`). The next session's
 *   first bar arrives from a history refetch, correct, instead of being
 *   invented here at the wrong instant.
 */
export function foldTickIntoBar(
  lastBar: ChartBar | undefined,
  price: number,
  tickEpochSeconds: number,
  timeFrame: TimeFrame,
): ChartBar | undefined {
  if (!Number.isFinite(price)) {
    return undefined;
  }

  // No history yet: a fixed-width frame can honestly open the current bucket;
  // a session-relative one has no boundary to place it on, so it waits.
  if (!lastBar) {
    const start = bucketStart(tickEpochSeconds, timeFrame);
    return start === undefined ? undefined : { time: start, open: price, high: price, low: price, close: price };
  }

  const width = bucketSeconds(timeFrame);

  if (width === undefined) {
    // Session-relative: extend the last bar, whenever the tick claims to be.
    return updateBar(lastBar, price);
  }

  const start = Math.floor(tickEpochSeconds / width) * width;

  if (start < lastBar.time) {
    // A late or clock-skewed tick for a bar we have already moved past.
    return undefined;
  }

  return start === lastBar.time
    ? updateBar(lastBar, price)
    : { time: start, open: price, high: price, low: price, close: price };
}

function updateBar(bar: ChartBar, price: number): ChartBar {
  return {
    time: bar.time,
    open: bar.open,
    high: Math.max(bar.high, price),
    low: Math.min(bar.low, price),
    close: price,
  };
}

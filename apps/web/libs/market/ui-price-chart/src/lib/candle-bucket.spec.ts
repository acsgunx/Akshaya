import { bucketSeconds, bucketStart, foldTickIntoBar, type ChartBar } from './candle-bucket';

describe('bucketSeconds', () => {
  it('gives a width for every fixed-width intraday frame', () => {
    expect(bucketSeconds('oneMinute')).toBe(60);
    expect(bucketSeconds('threeMinutes')).toBe(180);
    expect(bucketSeconds('fifteenMinutes')).toBe(900);
    expect(bucketSeconds('oneHour')).toBe(3600);
  });

  it('gives no width for the session-relative frames', () => {
    // A day/week/month boundary is a venue calendar question, not arithmetic.
    expect(bucketSeconds('oneDay')).toBeUndefined();
    expect(bucketSeconds('oneWeek')).toBeUndefined();
    expect(bucketSeconds('oneMonth')).toBeUndefined();
  });
});

describe('bucketStart', () => {
  it('floors to a real boundary', () => {
    // 2026-09-03T10:07:31Z
    const at = Math.floor(Date.parse('2026-09-03T10:07:31Z') / 1000);
    expect(bucketStart(at, 'fiveMinutes')).toBe(Math.floor(Date.parse('2026-09-03T10:05:00Z') / 1000));
    expect(bucketStart(at, 'oneHour')).toBe(Math.floor(Date.parse('2026-09-03T10:00:00Z') / 1000));
  });
});

describe('foldTickIntoBar', () => {
  const at = (iso: string) => Math.floor(Date.parse(iso) / 1000);

  const bar: ChartBar = {
    time: at('2026-09-03T10:05:00Z'),
    open: 100,
    high: 102,
    low: 99,
    close: 101,
  };

  it('extends the current bar while the tick is still inside its window', () => {
    const next = foldTickIntoBar(bar, 103, at('2026-09-03T10:07:31Z'), 'fiveMinutes');
    expect(next).toEqual({ time: bar.time, open: 100, high: 103, low: 99, close: 103 });
  });

  it('keeps the open and only widens the extremes it needs to', () => {
    const next = foldTickIntoBar(bar, 98, at('2026-09-03T10:07:31Z'), 'fiveMinutes');
    expect(next).toEqual({ time: bar.time, open: 100, high: 102, low: 98, close: 98 });
  });

  it('opens a new bar once the tick crosses the boundary', () => {
    const next = foldTickIntoBar(bar, 104, at('2026-09-03T10:10:02Z'), 'fiveMinutes');
    expect(next).toEqual({ time: at('2026-09-03T10:10:00Z'), open: 104, high: 104, low: 104, close: 104 });
  });

  it('drops a tick that belongs before the bar we already hold', () => {
    // Lightweight Charts rejects an out-of-order update; a late or
    // clock-skewed tick must not be able to rewind the series.
    expect(foldTickIntoBar(bar, 97, at('2026-09-03T09:55:00Z'), 'fiveMinutes')).toBeUndefined();
  });

  it('never rolls a daily bar, however far ahead the tick claims to be', () => {
    // The next session's first bar comes from a refetch, at the venue's real
    // boundary — inventing one here would place it at a UTC midnight that is
    // mid-session for somebody.
    const next = foldTickIntoBar(bar, 105, at('2026-09-05T03:00:00Z'), 'oneDay');
    expect(next).toEqual({ time: bar.time, open: 100, high: 105, low: 99, close: 105 });
  });

  it('opens the current bucket when there is no history yet on a fixed-width frame', () => {
    const next = foldTickIntoBar(undefined, 50, at('2026-09-03T10:07:31Z'), 'fiveMinutes');
    expect(next).toEqual({ time: at('2026-09-03T10:05:00Z'), open: 50, high: 50, low: 50, close: 50 });
  });

  it('waits for history rather than inventing a bar on a session-relative frame', () => {
    expect(foldTickIntoBar(undefined, 50, at('2026-09-03T10:07:31Z'), 'oneDay')).toBeUndefined();
  });

  it('ignores a price that is not a finite number', () => {
    expect(foldTickIntoBar(bar, Number.NaN, at('2026-09-03T10:07:31Z'), 'fiveMinutes')).toBeUndefined();
  });
});

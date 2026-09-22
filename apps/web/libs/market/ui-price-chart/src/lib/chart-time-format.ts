import { TickMarkType, type ChartOptions, type DeepPartial, type Time } from 'lightweight-charts';

import type { TimeFrame } from '@akshaya/shared/models';

/**
 * Chart options that label the crosshair and the time axis in `timeZone`.
 *
 * Lightweight Charts has no time-zone option — it formats UTC unless handed
 * formatters — so these are `Intl` formatters bound to the zone. `locale`
 * alone changes only how dates are WRITTEN: the library still draws every
 * label in UTC, so an NSE session showed as 03:45-10:00.
 *
 * Only the labels move. The bars stay in true UTC seconds, so live ticks
 * bucket into them exactly as before.
 */
export function timeAxisOptions(
  timeFrame: TimeFrame,
  timeZone: string | undefined,
  locale: string,
): DeepPartial<ChartOptions> {
  const format = (options: Intl.DateTimeFormatOptions): ((time: Time) => string) => {
    const formatter = new Intl.DateTimeFormat(locale, { ...options, timeZone });
    return (time) => (typeof time === 'number' ? formatter.format(time * 1000) : String(time));
  };

  const clock: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' };
  // Intraday: "Mon, 21 Sep, 14:23" — the weekday says more than a year would. A daily or
  // longer bar has no time of day worth showing (every NSE daily bar "opens" at 09:15), and
  // a year of bars spans a year boundary, so there it is the date with its year.
  const daily = ['oneDay', 'oneWeek', 'oneMonth'].includes(timeFrame);
  const crosshair = daily
    ? format({ weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
    : format({ weekday: 'short', day: 'numeric', month: 'short', ...clock });
  const year = format({ year: 'numeric' });
  const month = format({ month: 'short' });
  const day = format({ day: 'numeric', month: 'short' });
  const minute = format(clock);
  const second = format({ ...clock, second: '2-digit' });

  return {
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
  };
}

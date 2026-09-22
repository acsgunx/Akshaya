import {
  HistogramSeries,
  LineSeries,
  PriceScaleMode,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts';

import type { ChartBar } from './candle-bucket';
import { calculateStudies, type StudyId } from './chart-studies';

/** Resolves an `--ak-*` token to a colour the library can paint — see `ChartTokens`. */
type TokenColor = (name: string, fallback: string) => string;

/**
 * The indicator series: moving averages and bands over the price, RSI, MACD
 * and Stochastic RSI in panes of their own below it.
 *
 * The series are kept in one flat list in draw order, so a live tick can
 * update them in place. A full rebuild only happens when the selection, the
 * bars or the theme change: recreating a pane on every tick would throw away
 * the user's pan and zoom several times a second.
 */
export class StudyPanes {
  private readonly series: ISeriesApi<'Line' | 'Histogram'>[] = [];

  constructor(private readonly chart: IChartApi, private readonly token: TokenColor) {}

  /**
   * Redraws the enabled studies. `rebuild` false is the tick path: it keeps
   * every series and pushes only points at or after each one's last point.
   */
  sync(bars: readonly ChartBar[], enabled: readonly StudyId[], rebuild = true): void {
    const chart = this.chart;
    if (rebuild) {
      for (const series of this.series.splice(0).reverse()) { chart.removeSeries(series); }
    }
    let pane = 0;
    let index = 0;
    const colors = { brand: this.token('--ak-brand', 'currentColor'), buy: this.token('--ak-buy', 'currentColor'),
      sell: this.token('--ak-sell', 'currentColor') };
    for (const study of calculateStudies(bars, enabled)) {
      const paneIndex = study.pane ? ++pane : 0;
      for (const [plotIndex, plot] of study.plots.entries()) {
        const color = colors[plot.color];
        let series = this.series[index++];
        if (!series) {
          const options = { title: plot.title, color, lineWidth: 1 as const, priceLineVisible: false,
            lastValueVisible: study.pane, crosshairMarkerVisible: false };
          series = plot.histogram ? chart.addSeries(HistogramSeries, options, paneIndex) : chart.addSeries(LineSeries, options, paneIndex);
          this.series.push(series);
          // Oscillators are read against fixed 0-100 bounds; MACD is unbounded
          // and has to keep autoscaling to its own range.
          if (study.pane && study.id !== 'macd') {
            series.applyOptions({ autoscaleInfoProvider: () => ({ priceRange: { minValue: 0, maxValue: 100 } }) });
          }
          if (plotIndex === 0) {
            for (const level of study.levels ?? []) {
              series.createPriceLine({ price: level, color: this.token('--ak-text-tertiary', color), lineWidth: 1,
                lineStyle: 2, axisLabelVisible: false, title: String(level) });
            }
          }
        }
        const data = plot.points.map((point) => ({ ...point, time: point.time as UTCTimestamp,
          ...(plot.histogram ? { color: point.value >= 0 ? colors.buy : colors.sell } : {}) }));
        if (rebuild) { series.setData(data); }
        else {
          const lastTime = series.data().at(-1)?.time;
          for (const point of data) {
            if (typeof lastTime !== 'number' || point.time >= lastTime) { series.update(point); }
          }
        }
      }
    }
    if (rebuild) {
      // The price keeps four fifths of the height however many panes appear under it.
      chart.panes().forEach((item, i) => {
        item.setStretchFactor(i === 0 ? 4 : 1.2);
        if (i > 0) { item.priceScale('right').applyOptions({ mode: PriceScaleMode.Normal }); }
      });
    }
  }
}

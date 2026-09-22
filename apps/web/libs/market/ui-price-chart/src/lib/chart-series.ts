import {
  AreaSeries,
  BarSeries,
  CandlestickSeries,
  HistogramSeries,
  LineSeries,
  LineStyle,
  PriceScaleMode,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts';

import type { ChartBar } from './candle-bucket';
import type { VolumePoint } from './chart-data';
import type { ChartType } from './chart-studies';

/** Resolves an `--ak-*` token to a colour the library can paint — see `ChartTokens`. */
type TokenColor = (name: string, fallback: string) => string;

export type ScaleMode = 'normal' | 'log' | 'percentage';

/**
 * A horizontal price the caller wants marked — an average cost, a resting
 * order's limit or trigger. The chart only draws it; what it means, and
 * whether it should be shown at all, is the caller's business.
 */
export interface ChartPriceLevel {
  readonly price: number;
  /** Short, because it is drawn on the line beside the axis: "Long 50", "Buy 10 limit". */
  readonly title: string;
  readonly tone: 'buy' | 'sell' | 'brand';
  /**
   * Solid for what is already held, dashed for a resting limit, dotted for a
   * trigger that has not fired — the line says how real the price is before
   * anyone reads its label.
   */
  readonly kind: 'held' | 'limit' | 'trigger';
}

/**
 * The series drawn against the price scale: the price itself in whichever
 * form the user picked, the volume histogram under it, and the caller's
 * reference lines.
 *
 * Everything that talks to a series API lives here, including the options
 * each one needs to behave (a volume overlay's scale margins, a hollow
 * candle's transparent body, the tick-size precision). The component above
 * owns the chart, the bars and when any of this happens.
 */
export class ChartSeries {
  private price: ISeriesApi<'Candlestick' | 'Bar' | 'Line' | 'Area'> | undefined;
  private volume: ISeriesApi<'Histogram'> | undefined;
  private levels: IPriceLine[] = [];

  /**
   * `attach` is called with each newly created price series, so the drawing
   * primitive can move to it — the series is replaced whenever the chart type
   * changes, and drawings must survive that.
   */
  constructor(
    private readonly chart: IChartApi,
    private readonly token: TokenColor,
    private readonly attach: (previous: ISeriesApi<'Candlestick' | 'Bar' | 'Line' | 'Area'> | undefined, current: ISeriesApi<'Candlestick' | 'Bar' | 'Line' | 'Area'>) => void,
  ) {}

  /** The price series, for turning pixels into prices and back. Undefined until `setType`. */
  get priceSeries(): ISeriesApi<'Candlestick' | 'Bar' | 'Line' | 'Area'> | undefined {
    return this.price;
  }

  /** Creates the price series, or swaps it for another form, keeping the bars already drawn. */
  setType(type: ChartType, bars: readonly ChartBar[]): void {
    const previous = this.price;
    const options = { priceLineVisible: true, lastValueVisible: true };
    switch (type) {
      case 'line': this.price = this.chart.addSeries(LineSeries, options); break;
      case 'area': this.price = this.chart.addSeries(AreaSeries, options); break;
      case 'bars': this.price = this.chart.addSeries(BarSeries, options); break;
      default: this.price = this.chart.addSeries(CandlestickSeries, options);
    }
    this.price.setData(bars.map((bar) => pricePoint(bar)));
    // A price line belongs to its series and is removed with it, so the old
    // handles are dropped rather than removed a second time.
    this.levels = [];
    // Before the removal below: a detach from a series the chart has already
    // disposed has nothing left to detach from.
    this.attach(previous, this.price);
    if (previous) { this.chart.removeSeries(previous); }
    this.price.setSeriesOrder(0);
  }

  /** Replaces every bar: a new instrument, timeframe or history load. */
  setBars(bars: readonly ChartBar[], volumes: readonly VolumePoint[]): void {
    this.price?.setData(bars.map((bar) => pricePoint(bar)));

    if (volumes.some((point) => point.value > 0)) {
      this.volume ??= this.chart.addSeries(HistogramSeries, {
        priceScaleId: 'volume',
        priceFormat: { type: 'volume' },
        // The volume scale is an overlay with no axis of its own, so its last-value badge lands
        // on the PRICE axis — a "2.26M" (or a live bar's "0") sitting among the prices,
        // covering one. Volume is read off the bars; the badge only gets in the way.
        lastValueVisible: false,
        priceLineVisible: false,
      });
      // Pinned to the bottom fifth so volume reads as a footer to the price
      // action rather than competing with it for vertical space.
      this.chart.priceScale('volume').applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });
      const up = this.token('--ak-buy-surface', 'transparent');
      const down = this.token('--ak-sell-surface', 'transparent');
      this.volume.setData(volumes.map((point, i) => ({ time: point.time as UTCTimestamp, value: point.value,
        color: (bars[i]?.close ?? 0) >= (bars[i]?.open ?? 0) ? up : down })));
    } else if (this.volume) {
      this.chart.removeSeries(this.volume);
      this.volume = undefined;
    }
  }

  /** True when the bars carried volume worth drawing — the caller re-themes only then. */
  get hasVolume(): boolean {
    return this.volume !== undefined;
  }

  /** The forming bar, on every tick. `opened` adds the volume placeholder for a new bar. */
  updateBar(bar: ChartBar, opened: boolean): void {
    if (opened) { this.volume?.update({ time: bar.time as UTCTimestamp, value: 0 }); }
    this.price?.update(pricePoint(bar));
  }

  /** Pushes the current token values into the series. `type` decides the hollow candle's body. */
  applyTheme(type: ChartType): void {
    const up = this.token('--ak-buy', '#3b82f6');
    const down = this.token('--ak-sell', '#f59e0b');
    this.price?.applyOptions({
      upColor: type === 'hollow' ? 'transparent' : up,
      downColor: down,
      borderUpColor: up,
      borderDownColor: down,
      wickUpColor: up,
      wickDownColor: down,
      color: up,
      lineColor: up,
      topColor: this.token('--ak-buy-surface', 'transparent'),
      bottomColor: 'transparent',
    });
    this.volume?.applyOptions({ color: this.token('--ak-border', '#2a2f38') });
  }

  /** Scale, price format and whether volume is drawn — everything the settings menu changes. */
  applySettings(scaleMode: ScaleMode, precision: number, showVolume: boolean): void {
    this.price?.priceScale().applyOptions({
      mode: scaleMode === 'log' ? PriceScaleMode.Logarithmic
        : scaleMode === 'percentage' ? PriceScaleMode.Percentage : PriceScaleMode.Normal,
    });
    this.price?.applyOptions({ priceFormat: { type: 'price', precision, minMove: 10 ** -precision } });
    this.volume?.applyOptions({ visible: showVolume });
  }

  /** Rebuilds the caller's reference lines; cheap enough that there is no diffing. */
  setLevels(levels: readonly ChartPriceLevel[]): void {
    const series = this.price;
    for (const line of this.levels.splice(0)) { series?.removePriceLine(line); }
    if (!series) { return; }
    const style = { held: LineStyle.Solid, limit: LineStyle.Dashed, trigger: LineStyle.Dotted } as const;
    for (const level of levels) {
      if (!Number.isFinite(level.price) || level.price <= 0) { continue; }
      this.levels.push(series.createPriceLine({
        price: level.price,
        title: level.title,
        color: this.token(`--ak-${level.tone}`, 'currentColor'),
        lineWidth: level.kind === 'held' ? 2 : 1,
        lineStyle: style[level.kind],
        axisLabelVisible: true,
      }));
    }
  }
}

/** Every series form reads the same bar: OHLC for candles and bars, `value` for line and area. */
function pricePoint(bar: ChartBar) {
  return { ...bar, time: bar.time as UTCTimestamp, value: bar.close };
}

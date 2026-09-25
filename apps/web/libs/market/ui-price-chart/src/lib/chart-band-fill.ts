import type {
  IPrimitivePaneRenderer,
  IPrimitivePaneView,
  ISeriesPrimitive,
  SeriesAttachedParameter,
  UTCTimestamp,
} from 'lightweight-charts';

/** One shaded region: two value arrays, index-aligned with `times`. `NaN` breaks the band. */
export interface FillBand {
  readonly times: readonly number[];
  readonly upper: readonly number[];
  readonly lower: readonly number[];
  /** Already resolved to a colour the canvas can paint — see `ChartTokens`. */
  readonly color: string;
  readonly opacity: number;
}

/**
 * The translucent shading between two of a study's plots: the inside of
 * Bollinger Bands, a Keltner or Donchian channel, the Ichimoku cloud.
 *
 * Lightweight Charts has no band series, so this is a pane primitive that
 * paints below the price (`zOrder: 'bottom'`) from the same value arrays the
 * band's line series are drawn from. It is attached to whichever series owns
 * the pane, which is what gives it the right price scale to convert against.
 *
 * Only the visible slice is walked. Converting every value of a 10,000-bar
 * history to a pixel on each pan frame is a measurable stall, and the two extra
 * points either side of the viewport are enough for the edges of the shape to
 * leave the canvas cleanly.
 */
export class ChartBandFill implements ISeriesPrimitive {
  private attachedTo: SeriesAttachedParameter | undefined;
  private bands: readonly FillBand[] = [];
  private readonly view: IPrimitivePaneView = {
    zOrder: () => 'bottom',
    renderer: () => ({ draw: (target) => this.draw(target) }),
  };

  attached(parameters: SeriesAttachedParameter): void { this.attachedTo = parameters; }
  detached(): void { this.attachedTo = undefined; }
  paneViews(): readonly IPrimitivePaneView[] { return [this.view]; }

  update(bands: readonly FillBand[]): void {
    this.bands = bands;
    this.attachedTo?.requestUpdate();
  }

  private draw(target: Parameters<IPrimitivePaneRenderer['draw']>[0]): void {
    const attached = this.attachedTo;
    if (!attached || this.bands.length === 0) { return; }
    const range = attached.chart.timeScale().getVisibleLogicalRange();
    target.useMediaCoordinateSpace(({ context }) => {
      context.save();
      for (const band of this.bands) {
        const first = Math.max(0, Math.floor(range?.from ?? 0) - 2);
        const last = Math.min(band.times.length - 1, Math.ceil(range?.to ?? band.times.length) + 2);
        context.globalAlpha = band.opacity;
        context.fillStyle = band.color;
        // Each unbroken run of finite values either side is one polygon: a gap
        // in the band (a warm-up `NaN`, or a plot shifted past the last bar)
        // must not be bridged by a shape spanning it.
        let run: { x: number; top: number; bottom: number }[] = [];
        const flush = () => {
          if (run.length > 1) {
            context.beginPath();
            run.forEach((point, i) => (i === 0 ? context.moveTo(point.x, point.top) : context.lineTo(point.x, point.top)));
            for (let i = run.length - 1; i >= 0; i--) {
              const point = run[i] as { x: number; top: number; bottom: number };
              context.lineTo(point.x, point.bottom);
            }
            context.closePath();
            context.fill();
          }
          run = [];
        };
        for (let i = first; i <= last; i++) {
          const upper = band.upper[i];
          const lower = band.lower[i];
          const time = band.times[i];
          if (upper === undefined || lower === undefined || time === undefined
            || !Number.isFinite(upper) || !Number.isFinite(lower)) { flush(); continue; }
          const x = attached.chart.timeScale().timeToCoordinate(time as UTCTimestamp);
          const top = attached.series.priceToCoordinate(upper);
          const bottom = attached.series.priceToCoordinate(lower);
          if (x === null || top === null || bottom === null) { flush(); continue; }
          run.push({ x, top, bottom });
        }
        flush();
      }
      context.restore();
    });
  }
}

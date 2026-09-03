import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  input,
  viewChild,
} from '@angular/core';
import {
  CandlestickSeries,
  HistogramSeries,
  createChart,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts';

import { AppearanceStore } from '../../core/appearance.store';
import type { Candle, Tick, TimeFrame } from '../../core/models';
import { type ChartBar, foldTickIntoBar } from './candle-bucket';

/**
 * TradingView Lightweight Charts, wrapped as a dumb presentational component:
 * OHLC in via `candles`, live prices in via `tick`, nothing out. It fetches
 * nothing and knows about no broker — the feature that owns the data decides
 * where bars come from and hands them here.
 *
 * ============================================================================
 * WHY THE CANDLES ARE NOT RED AND GREEN
 * ============================================================================
 * Lightweight Charts defaults to `#26a69a` up / `#ef5350` down, which is the
 * red/green pair DESIGN.md rejects on purpose: roughly 8% of men have
 * red-green colour vision deficiency, and red-on-dark vs green-on-dark is
 * close to the worst case for them. Every colour below is pulled from the
 * app's OWN `--ak-*` tokens instead — the same blue/amber buy/sell pair the
 * order ticket and the watchlist use, including the colour-blind-safe
 * alternate. A candle body and a Buy button are therefore never two
 * different blues, and turning on Settings → Accessibility recolours this
 * chart along with everything else. Do not hardcode a hex value in this file.
 *
 * Theme changes are applied by asking the browser what the tokens currently
 * resolve to (see the `probe` field) rather than by keeping a second palette
 * in here, so the stylesheet stays the single source of truth.
 *
 * The library is ~50kB gzipped, so every consumer must reach it through a
 * lazy route or a `@defer` block; it must never enter the initial bundle
 * (see the `budgets` in `angular.json`).
 */
@Component({
  selector: 'ak-price-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="ak-chart-host" #host role="img" [attr.aria-label]="ariaLabel()"></div>`,
  // Genuinely not expressible as a utility: the canvas needs a definite box to
  // measure itself against, and the library writes its own inline sizing inside.
  styles: `
    :host {
      display: block;
    }
    .ak-chart-host {
      width: 100%;
      height: var(--ak-chart-height, 420px);
    }
  `,
})
export class PriceChartComponent {
  private readonly appearance = inject(AppearanceStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');

  /** Historical bars, oldest first. Replacing this input redraws the series. */
  readonly candles = input.required<readonly Candle[]>();

  /** The timeframe `candles` was requested at — decides how a live tick folds in. */
  readonly timeFrame = input.required<TimeFrame>();

  /** Latest live price. Each new tick updates (or opens) the most recent bar. */
  readonly tick = input<Tick | undefined>(undefined);

  /** Volume histogram under the price series; off for instruments that report none. */
  readonly showVolume = input(true);

  /**
   * A canvas is invisible to a screen reader, so the host carries a text
   * label describing what is plotted. It is not a substitute for the data
   * being available elsewhere on the page — the order ticket's live price
   * and the watchlist's LTP cell remain the accessible readings of "what is
   * this instrument doing right now".
   */
  readonly ariaLabel = input('Price chart');

  private chart: IChartApi | undefined;
  private priceSeries: ISeriesApi<'Candlestick'> | undefined;
  private volumeSeries: ISeriesApi<'Histogram'> | undefined;
  private lastBar: ChartBar | undefined;

  /**
   * Hidden element used to RESOLVE a custom property to a real colour.
   * `getComputedStyle(el).getPropertyValue('--ak-buy')` hands back the
   * literal token stream — `light-dark(#1d4ed8, #3b82f6)` — because a custom
   * property's computed value is its substitution value, not a used colour.
   * Lightweight Charts cannot parse that and silently paints the candles
   * near-black. Assigning the var to a REAL colour property and reading that
   * back forces the resolution, which is what this probe is for; it also
   * means `color-mix()` and any future token syntax resolve for free.
   */
  private readonly probe = document.createElement('span');

  constructor() {
    // The library measures its container, so it cannot be constructed until
    // that container is actually in the document with a box.
    afterNextRender(() => {
      this.create();
      this.applyTheme();
      this.drawHistory();
    });

    // Full redraw when the caller swaps the series (new instrument, new timeframe).
    effect(() => {
      this.candles();
      this.timeFrame();
      this.showVolume();
      if (this.chart) {
        this.drawHistory();
      }
    });

    // Incremental update on every tick — never a redraw, which would throw
    // away the user's pan/zoom on each price change.
    effect(() => {
      const tick = this.tick();
      if (!tick || !this.priceSeries) {
        return;
      }
      this.applyTick(tick);
    });

    // Re-read the tokens rather than keeping a second palette in here.
    effect(() => {
      this.appearance.theme();
      this.appearance.cvdSafe();
      if (this.chart) {
        this.applyTheme();
      }
    });
  }

  private create(): void {
    const element = this.host().nativeElement;

    // Inside the host so it inherits exactly the cascade the chart sits in.
    this.probe.style.display = 'none';
    element.appendChild(this.probe);

    this.chart = createChart(element, {
      autoSize: true,
      // Local time, not UTC: a trader reads a chart against the clock on
      // their own wall and the venue clock in the shell, not against GMT.
      localization: { locale: navigator.language },
      timeScale: { timeVisible: true, secondsVisible: false },
      handleScale: { axisPressedMouseMove: { time: true, price: false } },
    });

    this.priceSeries = this.chart.addSeries(CandlestickSeries, {});
    this.destroyRef.onDestroy(() => {
      // Disposes the canvases and the library's own resize listener; without
      // it, navigating between instruments leaks one chart per visit.
      this.chart?.remove();
      this.chart = undefined;
      this.priceSeries = undefined;
      this.volumeSeries = undefined;
    });
  }

  /**
   * Pushes the current values of the `--ak-*` tokens into the chart. Called on
   * creation and whenever an appearance preference changes.
   */
  private applyTheme(): void {
    const chart = this.chart;
    const price = this.priceSeries;
    if (!chart || !price) {
      return;
    }

    const up = this.token('--ak-buy', '#3b82f6');
    const down = this.token('--ak-sell', '#f59e0b');
    const text = this.token('--ak-text-secondary', '#98a1b3');
    const border = this.token('--ak-border', '#2a2f38');

    chart.applyOptions({
      layout: {
        // `transparent` lets the surrounding card supply the background,
        // so the chart cannot end up a slightly different dark than its card.
        background: { color: 'transparent' },
        textColor: text,
        attributionLogo: false,
      },
      grid: { vertLines: { color: border }, horzLines: { color: border } },
      rightPriceScale: { borderColor: border },
      timeScale: { borderColor: border },
      crosshair: { vertLine: { color: text, labelBackgroundColor: border }, horzLine: { color: text, labelBackgroundColor: border } },
    });

    price.applyOptions({
      upColor: up,
      downColor: down,
      borderUpColor: up,
      borderDownColor: down,
      wickUpColor: up,
      wickDownColor: down,
    });

    this.volumeSeries?.applyOptions({ color: border });
  }

  /**
   * Current resolved value of one `--ak-*` colour token, or `fallback` when
   * the property is not set at all. The sentinel round trip is how "not set"
   * is told apart from "set to something": an unresolvable `var()` leaves the
   * probe's colour at whatever was assigned before it, so the sentinel going
   * unchanged is the signal that nothing took.
   */
  private token(name: string, fallback: string): string {
    const sentinel = 'rgb(1, 2, 3)';
    this.probe.style.color = sentinel;
    this.probe.style.color = `var(${name})`;
    const resolved = getComputedStyle(this.probe).color;
    return !resolved || resolved === sentinel ? fallback : resolved;
  }

  private drawHistory(): void {
    const chart = this.chart;
    const price = this.priceSeries;
    if (!chart || !price) {
      return;
    }

    const bars: ChartBar[] = [];
    const volumes: { time: number; value: number }[] = [];

    for (const candle of this.candles()) {
      const time = Math.floor(Date.parse(candle.openTime) / 1000);
      if (!Number.isFinite(time)) {
        // A bar we cannot place on the time axis is dropped rather than
        // rendered at epoch zero, which would compress the whole chart.
        continue;
      }
      bars.push({ time, open: candle.open, high: candle.high, low: candle.low, close: candle.close });
      volumes.push({ time, value: candle.volume });
    }

    // The library requires strictly ascending time; a connector that returns
    // newest-first (or unsorted) must not silently render an empty chart.
    bars.sort((a, b) => a.time - b.time);
    volumes.sort((a, b) => a.time - b.time);

    price.setData(bars.map((bar) => ({ ...bar, time: bar.time as UTCTimestamp })));
    this.lastBar = bars.at(-1);

    if (this.showVolume() && volumes.some((v) => v.value > 0)) {
      this.volumeSeries ??= chart.addSeries(HistogramSeries, {
        priceScaleId: 'volume',
        priceFormat: { type: 'volume' },
      });
      // Pinned to the bottom fifth so volume reads as a footer to the price
      // action rather than competing with it for vertical space.
      chart.priceScale('volume').applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });
      this.volumeSeries.setData(volumes.map((v) => ({ time: v.time as UTCTimestamp, value: v.value })));
      this.applyTheme();
    } else if (this.volumeSeries) {
      chart.removeSeries(this.volumeSeries);
      this.volumeSeries = undefined;
    }

    chart.timeScale().fitContent();
  }

  private applyTick(tick: Tick): void {
    const price = Number(tick.lastPrice.amount);
    const at = Date.parse(tick.timestamp);
    // A tick whose timestamp we cannot read is still a real price; place it
    // now rather than discarding it.
    const seconds = Math.floor((Number.isFinite(at) ? at : Date.now()) / 1000);

    const bar = foldTickIntoBar(this.lastBar, price, seconds, this.timeFrame());
    if (!bar) {
      return;
    }

    this.lastBar = bar;
    this.priceSeries?.update({ ...bar, time: bar.time as UTCTimestamp });
  }
}

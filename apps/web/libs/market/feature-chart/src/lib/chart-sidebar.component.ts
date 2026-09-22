import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { MoneyPipe } from '@akshaya/shared/util';
import type { InstrumentDefinition, InstrumentKey, Tick } from '@akshaya/shared/models';
import { formatInstrumentLabel } from '@akshaya/shared/models';

import type { ExposureRow } from './chart-exposure';

/** One watchlist line: the instrument, its last tick, its day change, and whether that tick is old. */
export interface WatchRow {
  readonly instrument: InstrumentDefinition;
  readonly tick: Tick | undefined;
  readonly change: number | undefined;
  readonly stale: boolean;
}

/**
 * The chart's right-hand column: the watchlist to switch symbols with, what
 * the user holds or has resting in THIS symbol, and the symbol's own numbers.
 *
 * Presentational — every figure arrives as an input. Two things it is the
 * accessible reading of: the live quote a canvas cannot expose to a screen
 * reader, and the price lines drawn on that canvas, which are listed here as
 * text with a link to the screen that can act on each one.
 */
@Component({
  selector: 'ak-chart-sidebar',
  // The layout around it is the parent's: this host must not become the flex item.
  host: { class: 'contents' },
  standalone: true,
  imports: [DecimalPipe, MatButtonModule, MatIconModule, MatTooltipModule, RouterLink, MoneyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside class="ak-chart-sidebar w-64 shrink-0 border-l border-border max-lg:w-full max-lg:border-t max-lg:border-l-0" aria-label="Watchlist and symbol details">
      <div class="flex items-center justify-between border-b border-border px-3 py-2"><h2 class="text-sm font-semibold">Watchlist</h2><button mat-icon-button aria-label="Add current symbol to watchlist" matTooltip="Add current symbol" [disabled]="!definition()" (click)="pin.emit()"><mat-icon>add</mat-icon></button></div>
      <div class="grid grid-cols-[1fr_6rem_4rem] gap-1 px-3 py-2 text-[10px] font-semibold uppercase tracking-wider text-text-tertiary"><span>Symbol</span><span class="text-right">Last</span><span class="text-right">Chg %</span></div>
      <div class="max-h-72 overflow-y-auto">
        @for (row of rows(); track row.instrument.key) {
          <a class="ak-watch-row grid grid-cols-[1fr_6rem_4rem] items-center gap-1 px-3 py-2.5 text-xs" [class.ak-watch-row--active]="row.instrument.key === instrument()" [routerLink]="['/chart', brokerLinkId(), row.instrument.key]" [attr.aria-current]="row.instrument.key === instrument() ? 'page' : null">
            <span class="min-w-0 truncate font-semibold" [title]="row.instrument.name">{{ label(row.instrument.key) }} @if (row.stale) { <span class="text-[9px] font-normal text-warning">stale</span> }</span>
            <span class="text-right tabular-nums">{{ row.tick ? (row.tick.lastPrice | akMoney) : '—' }}</span>
            <span class="text-right tabular-nums" [class.text-buy]="(row.change ?? 0) >= 0" [class.text-sell]="(row.change ?? 0) < 0">{{ row.change === undefined ? '—' : ((row.change >= 0 ? '+' : '') + (row.change | number: '1.2-2') + '%') }}</span>
          </a>
        } @empty {
          <p class="px-4 py-6 text-xs leading-relaxed text-text-secondary">Your watchlist is empty. Search a symbol, then use + to pin it here.</p>
        }
      </div>
      <!--
        The text reading of the lines on the canvas, which a screen reader cannot see — and the
        way back: each row opens the screen where that position or order can be acted on.
      -->
      @if (exposure().length) {
        <section class="border-t border-border" aria-labelledby="ak-exposure-heading">
          <div class="flex items-center justify-between px-3 py-2">
            <h2 id="ak-exposure-heading" class="text-sm font-semibold">Positions &amp; orders</h2>
            <button mat-icon-button type="button" aria-label="Show positions and orders on the chart" [matTooltip]="showExposure() ? 'Hide from chart' : 'Show on chart'" [attr.aria-pressed]="showExposure()" (click)="exposureToggle.emit()"><mat-icon>{{ showExposure() ? 'visibility' : 'visibility_off' }}</mat-icon></button>
          </div>
          <ul role="list" class="pb-2 text-xs">
            @for (row of exposure(); track row.id) {
              <li>
                <a class="ak-watch-row flex items-baseline justify-between gap-2 px-3 py-2" [routerLink]="row.route">
                  <span class="min-w-0 truncate">
                    <span class="font-semibold" [class.text-buy]="row.tone === 'buy'" [class.text-sell]="row.tone === 'sell'" [class.text-brand]="row.tone === 'brand'">{{ row.title }}</span>
                    <span class="text-text-secondary"> · {{ row.detail }}</span>
                  </span>
                  <span class="shrink-0 tabular-nums">{{ row.price | akMoney }}</span>
                </a>
              </li>
            }
          </ul>
        </section>
      }
      <div class="border-t border-border p-4">
        <div class="flex items-center gap-2"><span class="flex size-8 items-center justify-center rounded bg-brand/15 font-semibold text-brand">{{ instrumentLabel().slice(0, 1) }}</span><h2 class="text-sm font-semibold">{{ instrumentLabel() }}</h2></div>
        <p class="mt-2 text-xs text-text-secondary">{{ definition()?.name }} · {{ venue() }}</p>
        @if (quote(); as tick) {
          <p class="mt-4 text-2xl font-semibold tabular-nums">{{ tick.lastPrice | akMoney }} <span class="text-xs font-normal text-text-tertiary">{{ tick.lastPrice.currency }}</span></p>
          @if (changePercent() !== undefined) { <p class="mt-1 text-sm tabular-nums" [class.text-buy]="changePercent()! >= 0" [class.text-sell]="changePercent()! < 0">{{ changePercent()! >= 0 ? '+' : '' }}{{ changePercent() | number: '1.2-2' }}% <span class="text-xs text-text-secondary">vs previous close</span></p> }
          <p class="mt-2 text-[11px] text-text-secondary">{{ isReplay() ? 'Live quote · independent of replay' : 'Last tick' }} · {{ formatTime(tick.timestamp) }}</p>
          <dl class="mt-5 grid grid-cols-2 gap-y-3 text-xs">
            <dt class="text-text-secondary">Open</dt><dd class="text-right tabular-nums">{{ tick.open ? (tick.open | akMoney) : '—' }}</dd>
            <dt class="text-text-secondary">Day high</dt><dd class="text-right tabular-nums">{{ tick.high ? (tick.high | akMoney) : '—' }}</dd>
            <dt class="text-text-secondary">Day low</dt><dd class="text-right tabular-nums">{{ tick.low ? (tick.low | akMoney) : '—' }}</dd>
            <dt class="text-text-secondary">Previous close</dt><dd class="text-right tabular-nums">{{ tick.previousClose ? (tick.previousClose | akMoney) : '—' }}</dd>
            <dt class="text-text-secondary">Volume</dt><dd class="text-right tabular-nums">{{ tick.volume === undefined ? '—' : (tick.volume | number: '1.0-0') }}</dd>
          </dl>
        } @else { <p class="my-4 text-xs text-text-secondary">Waiting for the first quote from this broker.</p> }
        <p class="mt-5 text-[11px] leading-relaxed text-text-tertiary">{{ source() }} · broker market data. Available history and intervals depend on your linked account.</p>
      </div>
    </aside>
  `,
  styles: `
    .ak-watch-row--active {
      color: var(--color-brand);
      background: color-mix(in srgb, var(--color-brand) 12%, var(--color-surface-1));
    }

    .ak-watch-row:hover { background-color: var(--color-surface-3); }
  `,
})
export class ChartSidebarComponent {
  protected readonly label = formatInstrumentLabel;

  readonly rows = input.required<readonly WatchRow[]>();
  readonly exposure = input.required<readonly ExposureRow[]>();
  readonly showExposure = input(true);

  readonly brokerLinkId = input.required<string>();
  readonly instrument = input.required<InstrumentKey>();
  readonly instrumentLabel = input.required<string>();
  readonly venue = input('');
  readonly definition = input<InstrumentDefinition | undefined>(undefined);
  readonly quote = input<Tick | undefined>(undefined);
  /** Day change against the previous close, already computed. */
  readonly changePercent = input<number | undefined>(undefined);
  readonly isReplay = input(false);
  /** The broker behind this data, named so nobody reads a paper price as a live one. */
  readonly source = input('');
  /** The venue's zone, so the tick time reads in exchange time like the axis does. */
  readonly timeZone = input<string | undefined>(undefined);

  readonly pin = output<void>();
  readonly exposureToggle = output<void>();

  protected formatTime(value: string): string {
    const date = new Date(value);
    if (!Number.isFinite(date.getTime())) { return '—'; }
    return new Intl.DateTimeFormat(undefined, {
      timeZone: this.timeZone(), hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    }).format(date);
  }
}

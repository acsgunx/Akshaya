import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { ClockService, InstrumentPipe, MoneyPipe } from '@akshaya/shared/util';
import { MarketDataService } from '@akshaya/shared/data-access';
import type { InstrumentDefinition } from '@akshaya/shared/models';
import { ConnectionStatusComponent } from '@akshaya/shared/ui';

type FlashDirection = 'up' | 'down' | undefined;

/**
 * One watchlist row: owns its own SignalR subscription (subscribe on
 * create, unsubscribe on destroy via `DestroyRef` — see
 * `market-data.service.ts`'s refcounting note) and its own flash-on-change
 * state.
 *
 * FLASH WITHOUT JITTER: the flash is a CSS background-colour animation
 * (`.ak-flash-up`/`.ak-flash-down`, defined once in `styles.scss`) applied
 * to a cell whose width is already fixed — by `.ak-col-price` in the grid,
 * by `min-w-price` in the compact card — see DESIGN.md "price cells must
 * never jitter". The direction is derived by
 * comparing consecutive ticks HERE, not guessed from `Tick.change` (which is
 * relative to the previous CLOSE, not the previous tick) — a flash means
 * "this number just moved", which is a different fact from "the day's
 * change is positive".
 */
@Component({
  selector: 'ak-watchlist-row',
  standalone: true,
  imports: [
    DecimalPipe,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    RouterLink,
    InstrumentPipe,
    MoneyPipe,
    ConnectionStatusComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (compact()) {
      <!--
        COMPACT: name and venue left, price and day change right — the two numbers anyone
        glancing at a watchlist wants. Everything else is one tap away rather than squeezed
        into 32px icon columns a finger cannot hit reliably.
      -->
      <button
        type="button"
        class="ak-focus-halo flex w-full items-center gap-3 px-4 py-3 text-left active:bg-surface-2"
        [attr.aria-expanded]="actionsOpen()"
        (click)="actionsOpen.set(!actionsOpen())"
      >
        <span class="block min-w-0 flex-1">
          <span class="block truncate text-[15px] font-semibold">{{ instrument().key | akInstrument }}</span>
          <span class="mt-0.5 flex min-w-0 items-center gap-2 text-xs text-text-tertiary">
            <span class="truncate">{{ instrument().key | akInstrument: 'detail' }} · {{ instrument().currency }}</span>
            <ak-connection-status label="" [streamState]="connectionState()" [isDataStale]="isStale()" />
          </span>
        </span>
        <span class="block shrink-0 text-right">
          <!-- Same fixed-width, background-only flash as the desktop cell — see the class doc. -->
          <span class="block min-w-price rounded-xs px-1 text-[15px] font-semibold tabular-nums" [class]="flashClass()">
            {{ quote()?.lastPrice | akMoney }}
          </span>
          <span
            class="block px-1 text-xs tabular-nums"
            [class.text-buy]="changeIsUp()"
            [class.text-sell]="changeIsUp() === false"
          >
            @if (changePercent(); as pct) {
              {{ pct > 0 ? '+' : '' }}{{ pct | number: '1.2-2' }}%
            } @else {
              &nbsp;
            }
          </span>
        </span>
      </button>

      @if (actionsOpen()) {
        <!-- As on desktop, Buy and Sell only open the ticket staged to that side; nothing is placed here. -->
        <div class="flex items-center gap-2 px-4 pb-3">
          <a
            mat-flat-button
            class="ak-btn-buy flex-1"
            [routerLink]="['/trade', brokerLinkId(), instrument().key]"
            [queryParams]="{ side: 'buy' }"
            [attr.aria-label]="'Buy ' + instrument().key"
            >Buy</a
          >
          <a
            mat-flat-button
            class="ak-btn-sell flex-1"
            [routerLink]="['/trade', brokerLinkId(), instrument().key]"
            [queryParams]="{ side: 'sell' }"
            [attr.aria-label]="'Sell ' + instrument().key"
            >Sell</a
          >
          <a
            mat-icon-button
            [routerLink]="['/chart', brokerLinkId(), instrument().key]"
            [attr.aria-label]="'Open chart for ' + instrument().key"
          >
            <mat-icon aria-hidden="true">candlestick_chart</mat-icon>
          </a>
          <button
            mat-icon-button
            type="button"
            (click)="remove.emit(instrument().key)"
            [attr.aria-label]="'Remove ' + instrument().key + ' from watchlist'"
          >
            <mat-icon aria-hidden="true">delete_outline</mat-icon>
          </button>
        </div>
      }
    } @else {
    <div class="ak-trow ak-wl-row" role="row">
      <span class="ak-truncate ak-strong" role="cell">{{ instrument().name || instrument().key }}</span>
      <span class="ak-caption" role="cell">{{ instrument().currency }}</span>

      <span class="ak-col-price ak-wl-price" role="cell" [class]="flashClass()">
        {{ quote()?.lastPrice | akMoney }}
      </span>

      <span class="ak-col-price" role="cell" [class.ak-buy-text]="changeIsUp()" [class.ak-sell-text]="changeIsUp() === false">
        @if (changePercent(); as pct) {
          {{ pct > 0 ? '+' : '' }}{{ pct | number: '1.2-2' }}%
        }
      </span>

      <ak-connection-status label="" [streamState]="connectionState()" [isDataStale]="isStale()" />

      <!--
        Buy and Sell open the ticket already staged to that side. They do not
        place anything — the ticket's review-then-confirm step is what turns
        an intention into an order, and a one-click trade from a scrolling
        list is exactly the mis-click this design refuses to allow.
      -->
      <a
        class="ak-iconbtn ak-focus-halo ak-buy-text"
        [routerLink]="['/trade', brokerLinkId(), instrument().key]"
        [queryParams]="{ side: 'buy' }"
        [attr.aria-label]="'Buy ' + instrument().key"
        matTooltip="Buy"
      >
        <mat-icon class="ak-i-sm" aria-hidden="true">add_circle_outline</mat-icon>
      </a>

      <a
        class="ak-iconbtn ak-focus-halo ak-sell-text"
        [routerLink]="['/trade', brokerLinkId(), instrument().key]"
        [queryParams]="{ side: 'sell' }"
        [attr.aria-label]="'Sell ' + instrument().key"
        matTooltip="Sell"
      >
        <mat-icon class="ak-i-sm" aria-hidden="true">remove_circle_outline</mat-icon>
      </a>

      <a
        class="ak-iconbtn ak-focus-halo"
        [routerLink]="['/chart', brokerLinkId(), instrument().key]"
        [attr.aria-label]="'Open chart for ' + instrument().key"
        matTooltip="Chart"
      >
        <mat-icon class="ak-i-sm" aria-hidden="true">candlestick_chart</mat-icon>
      </a>

      <button
        type="button"
        class="ak-iconbtn ak-focus-halo"
        (click)="remove.emit(instrument().key)"
        [attr.aria-label]="'Remove ' + instrument().key + ' from watchlist'"
        matTooltip="Remove"
      >
        <mat-icon class="ak-i-sm" aria-hidden="true">close</mat-icon>
      </button>
    </div>
    }
  `,
  // The grid columns come from the parent watchlist (`--ak-cols`), so the row can never
  // fall out of alignment with the header it sits under. Everything else is a primitive.
  styles: `
    .ak-wl-price {
      border-radius: var(--ak-radius-xs);
    }
  `,
})
export class WatchlistRowComponent {
  private readonly marketData = inject(MarketDataService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly clock = inject(ClockService);

  readonly instrument = input.required<InstrumentDefinition>();

  /** The linked account this row's stream runs through; the hub subscribes per link. */
  readonly brokerLinkId = input.required<string>();

  readonly remove = output<string>();

  /** Card layout for the compact screen, where the parent drops the grid header. */
  readonly compact = input(false);

  /** Compact only: whether this row's action strip (Buy, Sell, Chart, Remove) is showing. */
  protected readonly actionsOpen = signal(false);

  protected readonly quote = computed(() => this.marketData.tickFor(this.instrument().key)());
  /**
   * When this row's last tick ARRIVED, measured against the shared ticking
   * clock — never an age handed over by the service. An age computed at the
   * service only re-evaluates when the NEXT tick lands, so it sits at ≈0 for
   * as long as the feed is silent and this row shows a live dot over a dead
   * price. Same shape as the chart screen's `lastUpdatedAt`.
   */
  private readonly lastTickAt = computed(() => this.marketData.lastTickAtFor(this.instrument().key)());
  protected readonly isStale = this.clock.isOlderThan(this.lastTickAt, 15_000);
  protected readonly connectionState = this.marketData.connectionState;

  private readonly flash = signal<FlashDirection>(undefined);
  protected readonly flashClass = computed(() => {
    const dir = this.flash();
    return dir === 'up' ? 'ak-flash-up' : dir === 'down' ? 'ak-flash-down' : '';
  });

  /** Signed day-change percent, purely for the arrow/percent cell — independent of the flash logic above. */
  protected readonly changePercent = computed(() => {
    const q = this.quote();
    if (!q?.previousClose) {
      return undefined;
    }
    const prev = Number(q.previousClose.amount);
    if (!prev) {
      return undefined;
    }
    return ((Number(q.lastPrice.amount) - prev) / prev) * 100;
  });
  protected readonly changeIsUp = computed(() => {
    const pct = this.changePercent();
    return pct === undefined ? undefined : pct >= 0;
  });

  private lastPrice: number | undefined;

  constructor() {
    effect(() => {
      const brokerLinkId = this.brokerLinkId();
      if (!brokerLinkId) {
        return;
      }
      const unsubscribe = this.marketData.subscribe(brokerLinkId, this.instrument().key);
      this.destroyRef.onDestroy(unsubscribe);
    });

    effect(() => {
      const q = this.quote();
      if (!q) {
        return;
      }
      const price = Number(q.lastPrice.amount);
      if (this.lastPrice !== undefined && price !== this.lastPrice) {
        this.flash.set(price > this.lastPrice ? 'up' : 'down');
        // Clear after the flash animation's duration so the class can be
        // re-applied (and re-trigger the CSS animation) on the NEXT change,
        // even if it is the same direction as this one.
        setTimeout(() => this.flash.set(undefined), 900);
      }
      this.lastPrice = price;
    });
  }
}

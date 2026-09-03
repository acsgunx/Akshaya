import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { MarketDataService } from '../../core/market-data.service';
import { MoneyPipe } from '../../core/money.pipe';
import type { InstrumentDefinition } from '../../core/models';
import { ConnectionStatusComponent } from '../../shared/connection-status/connection-status.component';

type FlashDirection = 'up' | 'down' | undefined;

/**
 * One watchlist row: owns its own SignalR subscription (subscribe on
 * create, unsubscribe on destroy via `DestroyRef` — see
 * `market-data.service.ts`'s refcounting note) and its own flash-on-change
 * state.
 *
 * FLASH WITHOUT JITTER: the flash is a CSS background-colour animation
 * (`.ak-flash-up`/`.ak-flash-down`, defined once in `styles.scss`) applied
 * to a cell whose width is already fixed by `.ak-col-price` — see
 * DESIGN.md "price cells must never jitter". The direction is derived by
 * comparing consecutive ticks HERE, not guessed from `Tick.change` (which is
 * relative to the previous CLOSE, not the previous tick) — a flash means
 * "this number just moved", which is a different fact from "the day's
 * change is positive".
 */
@Component({
  selector: 'ak-watchlist-row',
  standalone: true,
  imports: [DecimalPipe, MatIconModule, MatTooltipModule, MoneyPipe, ConnectionStatusComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
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

  readonly instrument = input.required<InstrumentDefinition>();

  /** The linked account this row's stream runs through; the hub subscribes per link. */
  readonly brokerLinkId = input.required<string>();

  readonly remove = output<string>();

  protected readonly quote = computed(() => this.marketData.tickFor(this.instrument().key)());
  protected readonly ageMs = computed(() => this.marketData.ageMsFor(this.instrument().key)());
  protected readonly isStale = computed(() => (this.ageMs() ?? Number.POSITIVE_INFINITY) > 15_000);
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

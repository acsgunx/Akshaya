import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { DatePipe } from '@angular/common';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RouterLink } from '@angular/router';

import { sideLabel } from '../../core/labels';
import { MoneyPipe } from '../../core/money.pipe';
import { QuantityPipe } from '../../core/quantity.pipe';
import type { TradeRecord } from '../../core/models';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { FillsStore } from './fills.store';

/**
 * The fills (executions) blotter.
 *
 * WHY THIS EXISTS ALONGSIDE /orders: an order is an instruction, a fill is
 * what happened. The exchange fills a large order in whatever chunks the
 * book offers, so one order becomes many fills at many prices, and the
 * blotter's single average price cannot answer "what did I actually pay for
 * the first 200 shares". Anyone reconciling a contract note, computing a
 * realised cost basis, or arguing with a broker about a price needs the
 * chunks.
 *
 * The default range is today. Reaching further back routes to the broker's
 * history endpoint rather than the day's tradebook, which is a decision the
 * connector makes from the date window — see `MStockOrders.GetTradesAsync`.
 */
@Component({
  selector: 'ak-fills',
  standalone: true,
  imports: [
    ScrollingModule,
    DatePipe,
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    RouterLink,
    MoneyPipe,
    QuantityPipe,
    EmptyStateComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section>
      <header class="ak-page-head ak-row">
        <h1>Fills</h1>
        <div class="flex items-end gap-2">
          <form [formGroup]="range" class="flex items-end gap-2">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>From</mat-label>
              <input matInput type="date" formControlName="from" />
            </mat-form-field>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>To</mat-label>
              <input matInput type="date" formControlName="to" />
            </mat-form-field>
            <button mat-stroked-button type="button" (click)="applyRange()">Apply</button>
          </form>
          <a mat-stroked-button routerLink="/orders">Orders</a>
        </div>
      </header>

      @if (store.error(); as error) {
        <p class="mb-3 flex items-start gap-1.5 text-xs text-danger" role="alert">
          <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">error</mat-icon>
          {{ error }}
        </p>
      }

      @if (store.isPartial()) {
        <!--
          Named gaps, not a silent short list. Someone reconciling against a
          contract note has to know which account did not answer, or they
          will conclude the missing trades never happened.
        -->
        <div class="mb-3 rounded-sm border border-warning/40 p-2.5 text-xs text-warning" role="status">
          <p class="flex items-center gap-1.5 font-medium">
            <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">warning</mat-icon>
            Some accounts could not be read — this list is incomplete.
          </p>
          @for (warning of store.warnings(); track warning) {
            <p class="mt-0.5 pl-6">{{ warning }}</p>
          }
        </div>
      }

      @if (store.loading() && store.trades().length === 0) {
        <div class="ak-loading"><mat-spinner diameter="28" /> Loading fills…</div>
      } @else if (store.trades().length === 0) {
        <ak-empty-state
          icon="fact_check"
          title="No fills"
          description="Executions appear here once an order trades. Widen the date range to look further back."
        />
      } @else {
        <p class="mb-2 text-xs text-text-secondary">
          {{ store.trades().length }} execution(s) · {{ orderCount() }} order(s)
        </p>

        <div class="ak-thead" role="row">
          <span>Time</span>
          <span>Instrument</span>
          <span>Side</span>
          <span class="ak-col-qty">Qty</span>
          <span class="ak-col-price">Price</span>
          <span class="ak-col-price">Value</span>
          <span class="ak-col-price">Charges</span>
          <span>Order</span>
        </div>

        <cdk-virtual-scroll-viewport itemSize="48" class="ak-viewport">
          <div *cdkVirtualFor="let trade of store.trades(); trackBy: trackById" class="ak-trow" role="row">
            <span class="ak-muted tabular-nums">{{ trade.executedAt | date: 'dd MMM HH:mm:ss' }}</span>
            <span class="ak-truncate ak-strong">{{ trade.instrument }}</span>
            <span [class.ak-buy-text]="trade.side === 'buy'" [class.ak-sell-text]="trade.side === 'sell'">
              {{ sideLabel(trade.side) }}
            </span>
            <span class="ak-col-qty">{{ trade.quantity | akQuantity: { fractional: true } }}</span>
            <span class="ak-col-price">{{ trade.price | akMoney }}</span>
            <span class="ak-col-price">{{ valueOf(trade) | akMoney }}</span>
            <!--
              An em dash, never a zero. "This broker does not report per-trade
              charges" and "this trade cost nothing" are different claims, and
              only one of them is true.
            -->
            <span class="ak-col-price ak-muted">{{ trade.charges ? (trade.charges | akMoney) : '—' }}</span>
            <span class="ak-truncate ak-muted tabular-nums">{{ trade.brokerOrderId }}</span>
          </div>
        </cdk-virtual-scroll-viewport>
      }
    </section>
  `,
  styleUrl: '../orders/orders.component.scss',
})
export class FillsComponent {
  protected readonly store = inject(FillsStore);
  protected readonly sideLabel = sideLabel;

  protected readonly range = new FormGroup({
    from: new FormControl<string>('', { nonNullable: true }),
    to: new FormControl<string>('', { nonNullable: true }),
  });

  /** How many distinct orders produced these fills — the headline "3 fills across 1 order". */
  protected readonly orderCount = computed(
    () => new Set(this.store.trades().map((t) => t.brokerOrderId)).size,
  );

  protected trackById(_index: number, trade: TradeRecord): string {
    return trade.tradeId;
  }

  protected valueOf(trade: TradeRecord) {
    return {
      amount: String(Number(trade.quantity) * Number(trade.price.amount)),
      currency: trade.price.currency,
    };
  }

  protected applyRange(): void {
    const { from, to } = this.range.getRawValue();
    this.store.setRange(from || undefined, to || undefined);
  }
}

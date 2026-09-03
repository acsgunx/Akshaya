import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { PercentPipe } from '@angular/common';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { MoneyPipe } from '../../core/money.pipe';
import { QuantityPipe } from '../../core/quantity.pipe';
import type { BlendedHolding, CurrencyCode, Money } from '../../core/models';
import { DashboardStore } from '../dashboard/dashboard.store';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';

/** Invested, current value and return for every holding in ONE currency. */
export interface HoldingsTotal {
  readonly currency: CurrencyCode;
  readonly invested: Money;
  readonly current: Money;
  readonly unrealisedPnl: Money;
  /** Return on cost as a fraction (0.1234 = +12.34%), or undefined when nothing was invested. */
  readonly returnFraction?: number;
}

/**
 * The long-term book: delivery stock sitting in demat, as opposed to the intraday and
 * derivative exposure on the Positions screen. In India the distinction is not cosmetic —
 * holdings are settled shares you own, positions are what you are carrying today.
 *
 * Shares `DashboardStore`'s snapshot rather than issuing a second `GET /api/portfolio`, exactly
 * as the positions blotter does: two views of one snapshot cannot disagree with each other,
 * two independent fetches can.
 */
@Component({
  selector: 'ak-holdings',
  standalone: true,
  imports: [
    PercentPipe,
    ScrollingModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MoneyPipe,
    QuantityPipe,
    EmptyStateComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './holdings.component.html',
  styleUrl: './holdings.component.scss',
})
export class HoldingsComponent implements OnInit {
  protected readonly store = inject(DashboardStore);

  private readonly expanded = signal<ReadonlySet<string>>(new Set());

  protected readonly holdings = computed<readonly BlendedHolding[]>(() => this.store.snapshot()?.holdings ?? []);

  /**
   * Totals, ONE ROW PER CURRENCY — never a single blended figure.
   *
   * Summing a rupee holding and a dollar holding into one number requires picking an FX rate,
   * and a portfolio total that silently depends on an unstated rate is worse than no total. The
   * dashboard's P&L card takes the same line, and the backend's own blender refuses to merge
   * currencies for the same reason. An account at a single-currency broker sees exactly one row
   * here, which is the common case and reads as a plain total.
   */
  protected readonly totals = computed<readonly HoldingsTotal[]>(() => {
    const byCurrency = new Map<CurrencyCode, { invested: number; current: number }>();

    for (const holding of this.holdings()) {
      const quantity = Number(holding.quantity);
      const averagePrice = Number(holding.averagePrice.amount);
      if (!Number.isFinite(quantity) || !Number.isFinite(averagePrice)) {
        continue;
      }

      const bucket = byCurrency.get(holding.currency) ?? { invested: 0, current: 0 };
      bucket.invested += quantity * averagePrice;

      // Prefer the broker's own valuation; fall back to qty x last price, and finally to cost —
      // a holding with no live price is shown at what was paid rather than dropped from the
      // total, which would understate the book.
      const current = Number(holding.currentValue?.amount);
      const last = Number(holding.lastPrice?.amount);
      bucket.current += Number.isFinite(current)
        ? current
        : Number.isFinite(last)
          ? quantity * last
          : quantity * averagePrice;

      byCurrency.set(holding.currency, bucket);
    }

    return [...byCurrency.entries()]
      .map(([currency, { invested, current }]) => ({
        currency,
        invested: { amount: String(invested), currency },
        current: { amount: String(current), currency },
        unrealisedPnl: { amount: String(current - invested), currency },
        returnFraction: invested > 0 ? (current - invested) / invested : undefined,
      }))
      .sort((a, b) => a.currency.localeCompare(b.currency));
  });

  /** Return on cost for one holding, as a fraction. Undefined when it cannot be computed. */
  protected returnFor(holding: BlendedHolding): number | undefined {
    const quantity = Number(holding.quantity);
    const averagePrice = Number(holding.averagePrice.amount);
    const cost = quantity * averagePrice;

    if (!Number.isFinite(cost) || cost <= 0) {
      return undefined;
    }

    const pnl = Number(holding.unrealisedPnl?.amount);
    return Number.isFinite(pnl) ? pnl / cost : undefined;
  }

  /** True when any of this holding is pledged as collateral and therefore not freely sellable. */
  protected isPledged(holding: BlendedHolding): boolean {
    const pledged = Number(holding.pledgedQuantity);
    return Number.isFinite(pledged) && pledged > 0;
  }

  ngOnInit(): void {
    if (!this.store.snapshot()) {
      this.store.refresh(undefined);
    }
  }

  protected isExpanded(groupKey: string): boolean {
    return this.expanded().has(groupKey);
  }

  protected toggle(groupKey: string): void {
    const next = new Set(this.expanded());
    if (next.has(groupKey)) {
      next.delete(groupKey);
    } else {
      next.add(groupKey);
    }
    this.expanded.set(next);
  }

  protected trackByGroupKey(_index: number, holding: BlendedHolding): string {
    return holding.groupKey;
  }
}

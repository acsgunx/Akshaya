import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, TemplateRef, computed, inject, signal } from '@angular/core';
import { DatePipe, PercentPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { InstrumentPipe, LayoutService, MoneyPipe, QuantityPipe } from '@akshaya/shared/util';
import type { BlendedHolding, BrokerHoldingLeg, CurrencyCode, Money } from '@akshaya/shared/models';
import { DashboardStore } from '@akshaya/portfolio/data-access';
import {
  AK_DIALOG_DEFAULTS,
  ChartLinkComponent,
  EmptyStateComponent,
  LoadingStateComponent,
  PORTFOLIO_TABS,
  RefreshButtonComponent,
  RefreshingDirective,
  SectionTabsComponent,
  TradeCalculatorDialogComponent,
} from '@akshaya/shared/ui';
import { parseInstrumentKey } from '@akshaya/shared/models';
import type { TradeCalculatorSeed } from '@akshaya/shared/util';
import { DELIVERY_REFERENCE, deliveryBreakEven, estimateDelivery, validTariff } from './delivery-charges';
import type { DeliveryTariff, DeliveryTariffs } from './delivery-charges';

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
 *
 * Two layouts over the same state: the virtualised grid at `lg` and up, and a card list below
 * it (see `LayoutService`). They share the expanded set, so rotating a tablet keeps whatever
 * row was open.
 */
@Component({
  selector: 'ak-holdings',
  standalone: true,
  imports: [
    DatePipe,
    ReactiveFormsModule,
    MatCheckboxModule,
    MatDialogModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatInputModule,
    PercentPipe,
    ScrollingModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    RouterLink,
    InstrumentPipe,
    MoneyPipe,
    QuantityPipe,
    ChartLinkComponent,
    EmptyStateComponent,
    LoadingStateComponent,
    RefreshButtonComponent,
    RefreshingDirective,
    SectionTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './holdings.component.html',
  styleUrl: './holdings.component.scss',
})
export class HoldingsComponent implements OnInit {
  protected readonly store = inject(DashboardStore);
  protected readonly layout = inject(LayoutService);
  protected readonly portfolioTabs = PORTFOLIO_TABS;
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly includeBuyCharges = signal(true);
  protected readonly tariffs = signal<DeliveryTariffs>({});
  protected readonly validTariff = validTariff;
  protected readonly inr = (amount: number): Money => ({ amount: String(amount), currency: 'INR' });
  private readonly selectedKey = signal<string | undefined>(undefined);
  private readonly scenarioPrice = signal<number | null | undefined>(undefined);
  protected readonly sellPriceControl = new FormControl<number | null>(null, [Validators.required, Validators.min(0.01)]);
  private readonly accountForms = new Map<string, ReturnType<HoldingsComponent['createTariffForm']>>();

  protected readonly chargeAccounts = computed(() => {
    const accounts = new Map<string, BrokerHoldingLeg>();
    for (const holding of this.holdings().filter(item => item.currency === 'INR')) {
      for (const leg of holding.legs) accounts.set(leg.brokerLinkId, leg);
    }
    return [...accounts.values()].map(leg => ({
      id: leg.brokerLinkId, name: leg.displayName, form: this.tariffFormFor(leg.brokerLinkId),
    }));
  });

  protected readonly estimates = computed(() => new Map(this.holdings().map(holding => [
    holding.groupKey, estimateDelivery(holding, this.tariffs(), this.includeBuyCharges()),
  ])));

  protected readonly netTotals = computed(() => {
    const values = [...this.estimates().values()].flatMap(result => result.value ? [result.value] : []);
    if (!values.length) return undefined;
    const sum = (key: 'invested' | 'grossPnl' | 'buyCharges' | 'sellCharges' | 'totalCharges' | 'netPnl' | 'netProceeds') =>
      values.reduce((total, value) => total + Math.round(value[key] * 100), 0) / 100;
    return {
      count: values.length, gross: sum('grossPnl'), charges: sum('totalCharges'), buy: sum('buyCharges'),
      sell: sum('sellCharges'), net: sum('netPnl'), proceeds: sum('netProceeds'),
      returnFraction: sum('netPnl') / (sum('invested') + sum('buyCharges')),
    };
  });

  protected readonly selectedHolding = computed(() => this.holdings().find(holding => holding.groupKey === this.selectedKey()));
  protected readonly selectedEstimate = computed(() => {
    const holding = this.selectedHolding();
    return holding ? estimateDelivery(holding, this.tariffs(), this.includeBuyCharges(), this.scenarioPrice()) : undefined;
  });
  protected readonly breakEven = computed(() => {
    const holding = this.selectedHolding();
    return holding ? deliveryBreakEven(holding, this.tariffs(), this.includeBuyCharges()) : undefined;
  });

  constructor() {
    this.sellPriceControl.valueChanges.pipe(takeUntilDestroyed()).subscribe(value => this.scenarioPrice.set(value));
  }

  private createTariffForm(id: string) {
    const tariff = this.tariffs()[id] ?? DELIVERY_REFERENCE;
    const form = new FormGroup({
      brokeragePercent: new FormControl(tariff.brokeragePercent, [Validators.required, Validators.min(0), Validators.max(100)]),
      brokerageCap: new FormControl(tariff.brokerageCap, [Validators.required, Validators.min(0)]),
      dpCharge: new FormControl(tariff.dpCharge, [Validators.required, Validators.min(0)]),
    });
    form.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.tariffs.update(tariffs => ({ ...tariffs, [id]: form.getRawValue() }));
    });
    return form;
  }

  private tariffFormFor(id: string) {
    let form = this.accountForms.get(id);
    if (!form) {
      form = this.createTariffForm(id);
      this.accountForms.set(id, form);
    }
    return form;
  }

  protected tariffFor(id: string): DeliveryTariff {
    return this.tariffs()[id] ?? DELIVERY_REFERENCE;
  }

  protected resetTariffs(): void {
    for (const form of this.accountForms.values()) form.reset(DELIVERY_REFERENCE);
  }

  protected showCalculation(holding: BlendedHolding, template: TemplateRef<unknown>): void {
    this.selectedKey.set(holding.groupKey);
    this.resetSellPrice();
    this.dialog.open(template, { width: '780px', maxWidth: '96vw', ariaLabel: 'Delivery profit calculator' });
  }

  protected resetSellPrice(): void {
    const holding = this.selectedHolding();
    const estimate = holding ? this.estimates().get(holding.groupKey)?.value : undefined;
    this.scenarioPrice.set(undefined);
    this.sellPriceControl.setValue(estimate ? estimate.saleValue / estimate.quantity : null, { emitEvent: false });
  }

  protected openAllTypesCalculator(holding: BlendedHolding): void {
    const parsed = parseInstrumentKey(holding.instrument);
    const seed: TradeCalculatorSeed | undefined =
      parsed && (parsed.venue === 'XNSE' || parsed.venue === 'XBOM')
        ? {
            type: 'delivery',
            exchange: parsed.venue,
            buyPrice: Number(holding.averagePrice.amount),
            sellPrice: Number(holding.lastPrice?.amount),
            quantity: Number(holding.quantity),
            label: holding.instrument,
          }
        : undefined;
    this.dialog.open(TradeCalculatorDialogComponent, {
      ...AK_DIALOG_DEFAULTS, data: seed, width: '980px', ariaLabel: 'Profit and charges calculator for all trade types',
    });
  }

  /**
   * The quantity in this leg that can actually be sold: total less pledged.
   *
   * Pledged stock is collateral. The broker rejects an order against it, so
   * pre-filling the ticket with the full holding would walk the trader
   * straight into that rejection — see the pledged badge on the row above,
   * which exists for the same reason.
   */
  protected sellableOf(leg: BrokerHoldingLeg): number {
    const free = Number(leg.quantity) - Number(leg.pledgedQuantity ?? 0);
    return Number.isFinite(free) && free > 0 ? free : 0;
  }

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
    return this.estimates().get(holding.groupKey)?.value?.returnFraction;
  }

  /** True when any of this holding is pledged as collateral and therefore not freely sellable. */
  protected isPledged(holding: BlendedHolding): boolean {
    const pledged = Number(holding.pledgedQuantity);
    return Number.isFinite(pledged) && pledged > 0;
  }

  ngOnInit(): void {
    // Not `if (!snapshot())`: a snapshot taken before a broker was linked is
    // present and wrong. The store decides — it re-pulls only when the cache
    // is empty or a link has changed, so tab-to-tab navigation stays free.
    this.store.ensureFresh();
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

import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';

import { MoneyPipe, TRADE_CHARGE_TYPES, estimateTrade, referenceTradeTariff, tradeBreakEven } from '@akshaya/shared/util';
import type { TradeCalculatorSeed, TradeChargeType, TradeExchange, TradeScenario } from '@akshaya/shared/util';
import type { Money } from '@akshaya/shared/models';

function scenarioForm(type: TradeChargeType) {
  const tariff = referenceTradeTariff(type);
  return new FormGroup({
    exchange: new FormControl<TradeExchange>('XNSE', { nonNullable: true }),
    direction: new FormControl<'long' | 'short'>('long', { nonNullable: true }),
    buyPrice: new FormControl<number | null>(type === 'options' ? 100 : 1000, [Validators.required, Validators.min(0.01)]),
    sellPrice: new FormControl<number | null>(type === 'options' ? 110 : type === 'delivery' ? 1000 : 1100, [Validators.required, Validators.min(0.01)]),
    quantity: new FormControl<number | null>(400, [Validators.required, Validators.min(1)]),
    quantityMode: new FormControl<'units' | 'lots'>('units', { nonNullable: true }),
    lotSize: new FormControl<number | null>(null, [Validators.min(1)]),
    brokeragePercent: new FormControl<number | null>(tariff.brokeragePercent, [Validators.required, Validators.min(0), Validators.max(100)]),
    brokerageCap: new FormControl<number | null>(tariff.brokerageCap, [Validators.required, Validators.min(0)]),
    flatBrokerage: new FormControl<number | null>(tariff.flatBrokerage, [Validators.required, Validators.min(0)]),
    dpCharge: new FormControl<number | null>(tariff.dpCharge, [Validators.required, Validators.min(0)]),
    includeDp: new FormControl(true, { nonNullable: true }),
  });
}

@Component({
  selector: 'ak-trade-calculator',
  standalone: true,
  imports: [DecimalPipe, ReactiveFormsModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule,
    MatExpansionModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatTooltipModule, MoneyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './trade-calculator.component.html',
})
export class TradeCalculatorComponent implements OnInit {
  readonly seed = input<TradeCalculatorSeed>();
  protected readonly types = TRADE_CHARGE_TYPES;
  protected readonly selectedType = signal<TradeChargeType>('intraday');
  protected readonly inr = (amount: number): Money => ({ amount: String(amount), currency: 'INR' });
  private readonly revision = signal(0);
  private readonly forms = {
    intraday: scenarioForm('intraday'), delivery: scenarioForm('delivery'),
    futures: scenarioForm('futures'), options: scenarioForm('options'),
  };
  protected readonly form = computed(() => this.forms[this.selectedType()]);
  protected readonly values = computed(() => { this.revision(); return this.form().getRawValue(); });
  protected readonly derivative = computed(() => this.selectedType() === 'futures' || this.selectedType() === 'options');
  protected readonly scenario = computed(() => { this.revision(); return this.scenarioFor(this.selectedType()); });
  readonly estimate = computed(() => estimateTrade(this.scenario()));
  protected readonly breakEven = computed(() => tradeBreakEven(this.scenario()));
  protected readonly tradingOnly = computed(() => estimateTrade({
    ...this.scenario(), tariff: { ...this.scenario().tariff, dpCharge: 0 },
  }));
  protected readonly comparisons = computed(() => {
    this.revision();
    return this.types.map(type => ({ ...type, estimate: estimateTrade(this.scenarioFor(type.value)) }));
  });

  constructor() {
    for (const form of Object.values(this.forms)) {
      form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.revision.update(value => value + 1));
    }
  }

  ngOnInit(): void {
    const seed = this.seed();
    if (!seed) return;
    this.selectedType.set(seed.type);
    this.forms[seed.type].patchValue({
      exchange: seed.exchange, direction: seed.direction ?? 'long', buyPrice: seed.buyPrice,
      sellPrice: seed.sellPrice, quantity: seed.quantity,
    });
  }

  protected selectType(type: TradeChargeType): void {
    this.selectedType.set(type);
  }

  protected resetScenario(): void {
    this.form().reset(scenarioForm(this.selectedType()).getRawValue());
  }

  private scenarioFor(type: TradeChargeType): TradeScenario {
    const value = this.forms[type].getRawValue();
    const lots = (type === 'futures' || type === 'options') && value.quantityMode === 'lots';
    const quantity = value.quantity ?? NaN;
    const lotSize = value.lotSize ?? NaN;
    return {
      type, exchange: value.exchange, direction: type === 'delivery' ? 'long' : value.direction,
      buyPrice: value.buyPrice ?? NaN, sellPrice: value.sellPrice ?? NaN,
      quantity: lots ? (Number.isSafeInteger(quantity) && Number.isSafeInteger(lotSize) && lotSize > 0 ? quantity * lotSize : NaN) : quantity,
      tariff: {
        brokeragePercent: type === 'options' ? 0 : value.brokeragePercent ?? NaN,
        brokerageCap: type === 'options' ? 0 : value.brokerageCap ?? NaN,
        flatBrokerage: type === 'options' ? value.flatBrokerage ?? NaN : 0,
        dpCharge: type === 'delivery' && value.includeDp ? value.dpCharge ?? NaN : 0,
      },
    };
  }
}

@Component({
  selector: 'ak-trade-calculator-dialog',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule, TradeCalculatorComponent, MoneyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Profit &amp; charges calculator</h2>
    <mat-dialog-content><ak-trade-calculator #calculator [seed]="seed" /></mat-dialog-content>
    <mat-dialog-actions class="justify-between! gap-3!">
      <span class="text-sm text-text-secondary">Estimated net P&amp;L
        @if (calculator.estimate(); as estimate) {
          <strong class="ml-2 tabular-nums" [class.text-buy]="estimate.netPnl >= 0" [class.text-sell]="estimate.netPnl < 0">{{ inr(estimate.netPnl) | akMoney: { signDisplay: 'exceptZero' } }}</strong>
        } @else { <span class="ml-2">—</span> }
      </span>
      <button mat-button mat-dialog-close type="button">Close</button>
    </mat-dialog-actions>
  `,
})
export class TradeCalculatorDialogComponent {
  protected readonly seed = inject<TradeCalculatorSeed | null>(MAT_DIALOG_DATA, { optional: true }) ?? undefined;
  protected readonly inr = (amount: number): Money => ({ amount: String(amount), currency: 'INR' });
}

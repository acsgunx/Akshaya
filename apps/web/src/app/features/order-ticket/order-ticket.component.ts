import { DestroyRef, ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';

import { BrokerLinksStore } from '../../core/broker-links.store';
import { ConnectorStore } from '../../core/connector.store';
import { MarketDataService } from '../../core/market-data.service';
import { MoneyPipe } from '../../core/money.pipe';
import {
  orderTypeLabel,
  orderTypeNeedsLimitPrice,
  orderTypeNeedsTriggerPrice,
  orderVarietyLabel,
  positionEffectLabel,
  sideLabel,
  timeInForceLabel,
} from '../../core/labels';
import type { InstrumentKey, Money, OrderType, OrderVariety, PlaceOrderRequest, PositionEffect, Side, TimeInForce } from '../../core/models';
import { formatInstrumentLabel } from '../../core/models';
import { ConnectionStatusComponent } from '../../shared/connection-status/connection-status.component';
import { OrderTicketStore } from './order-ticket.store';

interface OrderTicketFormControls {
  side: FormControl<Side>;
  orderType: FormControl<OrderType>;
  timeInForce: FormControl<TimeInForce>;
  positionEffect: FormControl<PositionEffect>;
  variety: FormControl<OrderVariety>;
  quantity: FormControl<string>;
  disclosedQuantity: FormControl<string>;
  limitPrice: FormControl<string>;
  triggerPrice: FormControl<string>;
  goodTillDate: FormControl<string>;
}

/**
 * THE component that proves the architecture: it takes an instrument and a
 * connector id, reads THAT connector's manifest, and renders only what the
 * manifest says the broker can do. There is exactly one branch on
 * `connectorId` allowed anywhere in this file — the lookup into
 * `ConnectorStore` — and it exists precisely once, to fetch the manifest.
 * Every other decision (which order types appear, which time-in-force
 * options, which position effects, whether quantity accepts a fraction,
 * whether a price field is visible) reads the manifest, never the id.
 *
 * If you are about to add `if (connectorId === '...')` anywhere below: stop.
 * The fix is a new field on `ConnectorManifest` (backend) and a read of that
 * field here, not a conditional on which broker this is.
 */
@Component({
  selector: 'ak-order-ticket',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MoneyPipe,
    ConnectionStatusComponent,
  ],
  providers: [OrderTicketStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './order-ticket.component.html',
  styleUrl: './order-ticket.component.scss',
  host: {
    // Scopes the B/S/Esc shortcuts to "this panel has focus" rather than
    // making them page-wide hotkeys — see DESIGN.md "the keyboard model".
    // A page-wide 'B'/'S' would fire while the user is typing a nickname
    // into an unrelated field elsewhere in the app.
    tabindex: '-1',
    '(keydown)': 'onKeydown($event)',
  },
})
export class OrderTicketComponent {
  private readonly connectorStore = inject(ConnectorStore);
  private readonly brokerLinksStore = inject(BrokerLinksStore);
  private readonly marketData = inject(MarketDataService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly store = inject(OrderTicketStore);

  // Bound from the route (`/trade/:brokerLinkId/:instrument`) via
  // `withComponentInputBinding()` in app.config.ts. `brokerLinkId` names a
  // specific linked account; the connector — and therefore the manifest —
  // is resolved FROM it below, never assumed to equal it.
  readonly brokerLinkId = input.required<string>();
  readonly instrument = input.required<InstrumentKey>();

  protected readonly link = computed(() => this.brokerLinksStore.linkFor(this.brokerLinkId()));
  protected readonly manifest = computed(() => {
    const connectorId = this.link()?.connectorId;
    return connectorId ? this.connectorStore.manifestFor(connectorId) : undefined;
  });
  protected readonly instrumentLabel = computed(() => formatInstrumentLabel(this.instrument()));

  protected readonly form = new FormGroup<OrderTicketFormControls>({
    side: new FormControl<Side>('buy', { nonNullable: true }),
    orderType: new FormControl<OrderType>('market', { nonNullable: true }),
    timeInForce: new FormControl<TimeInForce>('day', { nonNullable: true }),
    positionEffect: new FormControl<PositionEffect>('intraday', { nonNullable: true }),
    variety: new FormControl<OrderVariety>('regular', { nonNullable: true }),
    quantity: new FormControl<string>('', { nonNullable: true, validators: [Validators.required] }),
    disclosedQuantity: new FormControl<string>('', { nonNullable: true }),
    limitPrice: new FormControl<string>('', { nonNullable: true }),
    triggerPrice: new FormControl<string>('', { nonNullable: true }),
    goodTillDate: new FormControl<string>('', { nonNullable: true }),
  });

  // Form-value bridges as signals, so the template's visibility rules
  // (price fields, quantity step) are plain computed signals rather than
  // re-reading `.value` inside the template on every check.
  private readonly orderType = toSignal(this.form.controls.orderType.valueChanges, {
    initialValue: this.form.controls.orderType.value,
  });
  private readonly sideValue = toSignal(this.form.controls.side.valueChanges, {
    initialValue: this.form.controls.side.value,
  });

  protected readonly showLimitPrice = computed(() => orderTypeNeedsLimitPrice(this.orderType()));
  protected readonly showTriggerPrice = computed(() => orderTypeNeedsTriggerPrice(this.orderType()));
  protected readonly isBuy = computed(() => this.sideValue() === 'buy');

  protected readonly quote = computed(() => this.marketData.tickFor(this.instrument())());
  protected readonly tickAgeMs = computed(() => this.marketData.ageMsFor(this.instrument())());
  protected readonly isFeedStale = computed(() => (this.tickAgeMs() ?? Number.POSITIVE_INFINITY) > 10_000);
  protected readonly connectionState = this.marketData.connectionState;

  protected readonly orderTypeLabel = orderTypeLabel;
  protected readonly timeInForceLabel = timeInForceLabel;
  protected readonly positionEffectLabel = positionEffectLabel;
  protected readonly orderVarietyLabel = orderVarietyLabel;
  protected readonly sideLabel = sideLabel;

  /** Estimated notional (qty × best-known price), in the instrument's own currency — shown pre-confirm. */
  protected readonly estimatedValue = computed<Money | undefined>(() => {
    const qty = Number(this.form.controls.quantity.value);
    if (!qty || !Number.isFinite(qty)) {
      return undefined;
    }
    const limitPrice = Number(this.form.controls.limitPrice.value);
    const lastPrice = this.quote()?.lastPrice;
    const price = this.showLimitPrice() && limitPrice > 0 ? limitPrice : lastPrice ? Number(lastPrice.amount) : undefined;
    if (price === undefined) {
      return undefined;
    }
    return { amount: String(qty * price), currency: lastPrice?.currency ?? this.store.instrument()?.currency ?? '' };
  });

  constructor() {
    // Reset per-connector/instrument defaults from the manifest the moment
    // either changes — this is the ONE place defaults are derived from the
    // manifest, so the form never silently keeps a value the new broker
    // doesn't support (e.g. a leftover `bracket` variety on a broker that
    // doesn't offer one).
    effect(() => {
      const manifest = this.manifest();
      if (!manifest) {
        return;
      }
      this.form.patchValue({
        orderType: manifest.orders.types[0] ?? 'market',
        timeInForce: manifest.orders.timeInForce[0] ?? 'day',
        positionEffect: manifest.orders.positionEffects[0] ?? 'intraday',
        variety: manifest.orders.varieties[0] ?? 'regular',
      });
    });

    effect(() => {
      const key = this.instrument();
      this.store.loadInstrument({ brokerLinkId: this.brokerLinkId(), instrument: key });
      const unsubscribe = this.marketData.subscribe(this.brokerLinkId(), key);
      this.destroyRef.onDestroy(unsubscribe);
    });
  }

  protected stageSide(side: Side): void {
    if (this.store.phase() === 'form') {
      this.form.controls.side.setValue(side);
    }
  }

  protected reviewOrder(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.store.requestEstimate(this.buildRequest());
  }

  protected backToForm(): void {
    this.store.backToForm();
  }

  protected confirmSubmit(): void {
    this.store.submit(this.buildRequest());
  }

  protected placeAnother(): void {
    this.store.resetTicket();
    this.form.reset({
      side: 'buy',
      orderType: this.manifest()?.orders.types[0] ?? 'market',
      timeInForce: this.manifest()?.orders.timeInForce[0] ?? 'day',
      positionEffect: this.manifest()?.orders.positionEffects[0] ?? 'intraday',
      variety: this.manifest()?.orders.varieties[0] ?? 'regular',
      quantity: '',
      disclosedQuantity: '',
      limitPrice: '',
      triggerPrice: '',
      goodTillDate: '',
    });
  }

  protected onKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    const isEditable =
      !!target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable);

    if (event.key === 'Escape') {
      event.preventDefault();
      if (this.store.phase() === 'reviewing') {
        this.store.backToForm();
      } else if (this.store.phase() === 'form') {
        this.router.navigate(['/watchlist']);
      }
      return;
    }

    if (isEditable || this.store.phase() !== 'form') {
      return;
    }

    if (event.key === 'b' || event.key === 'B') {
      event.preventDefault();
      this.stageSide('buy');
    } else if (event.key === 's' || event.key === 'S') {
      event.preventDefault();
      this.stageSide('sell');
    }
  }

  private buildRequest(): PlaceOrderRequest {
    const v = this.form.getRawValue();
    return {
      brokerLinkId: this.brokerLinkId(),
      clientOrderId: crypto.randomUUID(),
      instrument: this.instrument(),
      side: v.side,
      quantity: v.quantity,
      orderType: v.orderType,
      positionEffect: v.positionEffect,
      timeInForce: v.timeInForce,
      variety: v.variety,
      limitPrice: this.showLimitPrice() && v.limitPrice ? { amount: v.limitPrice, currency: this.store.instrument()?.currency ?? '' } : undefined,
      triggerPrice:
        this.showTriggerPrice() && v.triggerPrice ? { amount: v.triggerPrice, currency: this.store.instrument()?.currency ?? '' } : undefined,
      disclosedQuantity: v.disclosedQuantity || undefined,
      goodTillDate: v.timeInForce === 'gtd' && v.goodTillDate ? v.goodTillDate : undefined,
    };
  }
}

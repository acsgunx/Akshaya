import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { orderTypeLabel, orderTypeNeedsLimitPrice, orderTypeNeedsTriggerPrice, timeInForceLabel } from '../../core/labels';
import type { ConnectorManifest, ModifyOrderRequest, OrderRecord, OrderType, TimeInForce } from '../../core/models';
import { canModifyField, formatInstrumentLabel } from '../../core/models';

export interface ModifyOrderDialogData {
  readonly order: OrderRecord;
  /** The manifest of the connector THIS order was placed through. */
  readonly manifest: ConnectorManifest;
}

interface ModifyFormControls {
  quantity: FormControl<string>;
  limitPrice: FormControl<string>;
  triggerPrice: FormControl<string>;
  orderType: FormControl<OrderType>;
  timeInForce: FormControl<TimeInForce>;
  disclosedQuantity: FormControl<string>;
}

/**
 * Amends a live order.
 *
 * WHICH FIELDS APPEAR IS THE MANIFEST'S DECISION, not this component's.
 * `orders.modifiable` is a list of field names the broker will accept on an
 * amendment, and every control below is gated on it — a broker that only
 * lets you change the price renders one field. There is no branch on
 * `connectorId` anywhere in this file, and adding one is the wrong fix for
 * anything; the right fix is a manifest field and a read of it here.
 *
 * ONLY CHANGED FIELDS ARE SENT. The request is built by diffing the form
 * against the order as it stands, so an amendment that touches the price
 * does not also re-assert the quantity. That matters beyond tidiness: the
 * order may have partially filled between the blotter's last refresh and
 * this dialog's submit, and re-sending a stale quantity would silently
 * restore it — asking the broker for more than the trader still wants.
 */
@Component({
  selector: 'ak-modify-order-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Modify order</h2>

    <mat-dialog-content>
      <p class="mb-3 text-[13px] text-text-secondary">
        <span class="font-medium text-text-primary">{{ instrumentLabel() }}</span>
        · {{ data.order.side === 'buy' ? 'Buy' : 'Sell' }}
        · {{ orderTypeLabel(data.order.orderType) }}
      </p>

      @if (isPartiallyFilled()) {
        <!--
          Load-bearing warning. Amending a partially filled order changes the
          TOTAL quantity, not the remaining one, and a trader who reads "10"
          as "10 more" ends up with a smaller position than they intended.
        -->
        <p class="mb-3 flex items-start gap-1.5 rounded-sm bg-surface-2 p-2 text-xs text-warning">
          <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">warning</mat-icon>
          <span>
            {{ data.order.filledQuantity }} of {{ data.order.quantity }} has already filled. A new
            quantity replaces the ORDER TOTAL — it is not added to what has filled, and it cannot
            be less than {{ data.order.filledQuantity }}.
          </span>
        </p>
      }

      <form [formGroup]="form" class="flex flex-col gap-3 pt-1">
        @if (canModify('orderType')) {
          <mat-form-field appearance="outline" class="w-full">
            <mat-label>Order type</mat-label>
            <mat-select formControlName="orderType">
              @for (type of data.manifest.orders.types; track type) {
                <mat-option [value]="type">{{ orderTypeLabel(type) }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }

        @if (canModify('quantity')) {
          <mat-form-field appearance="outline" class="w-full">
            <mat-label>Quantity</mat-label>
            <input matInput type="number" inputmode="decimal" formControlName="quantity" min="0" />
          </mat-form-field>
        }

        @if (canModify('limitPrice') && showLimitPrice()) {
          <mat-form-field appearance="outline" class="w-full">
            <mat-label>Limit price</mat-label>
            <input matInput type="number" inputmode="decimal" formControlName="limitPrice" />
          </mat-form-field>
        }

        @if (canModify('triggerPrice') && showTriggerPrice()) {
          <mat-form-field appearance="outline" class="w-full">
            <mat-label>Trigger price</mat-label>
            <input matInput type="number" inputmode="decimal" formControlName="triggerPrice" />
          </mat-form-field>
        }

        @if (canModify('timeInForce')) {
          <mat-form-field appearance="outline" class="w-full">
            <mat-label>Time in force</mat-label>
            <mat-select formControlName="timeInForce">
              @for (tif of data.manifest.orders.timeInForce; track tif) {
                <mat-option [value]="tif">{{ timeInForceLabel(tif) }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }

        @if (canModify('disclosedQuantity')) {
          <mat-form-field appearance="outline" class="w-full">
            <mat-label>Disclosed quantity (optional)</mat-label>
            <input matInput type="number" inputmode="decimal" formControlName="disclosedQuantity" min="0" />
            @if (disclosedTooSmall()) {
              <mat-hint class="text-warning">
                Indian exchanges require a disclosed quantity of at least 30% of the order.
              </mat-hint>
            }
          </mat-form-field>
        }
      </form>

      @if (validationError(); as error) {
        <p class="mt-2 flex items-start gap-1.5 text-xs text-danger" role="alert">
          <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">error</mat-icon>
          {{ error }}
        </p>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="dialogRef.close()">Cancel</button>
      <button mat-flat-button type="button" [disabled]="!canSubmit()" (click)="submit()">Amend order</button>
    </mat-dialog-actions>
  `,
})
export class ModifyOrderDialogComponent {
  protected readonly data = inject<ModifyOrderDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject<MatDialogRef<ModifyOrderDialogComponent, ModifyOrderRequest>>(MatDialogRef);

  protected readonly orderTypeLabel = orderTypeLabel;
  protected readonly timeInForceLabel = timeInForceLabel;

  protected readonly form = new FormGroup<ModifyFormControls>({
    quantity: new FormControl<string>('', { nonNullable: true }),
    limitPrice: new FormControl<string>('', { nonNullable: true }),
    triggerPrice: new FormControl<string>('', { nonNullable: true }),
    orderType: new FormControl<OrderType>('market', { nonNullable: true }),
    timeInForce: new FormControl<TimeInForce>('day', { nonNullable: true }),
    disclosedQuantity: new FormControl<string>('', { nonNullable: true }),
  });

  /** Bumped on every form change so the computed signals below re-evaluate. */
  private readonly revision = signal(0);

  protected readonly instrumentLabel = computed(() => formatInstrumentLabel(this.data.order.instrument));

  protected readonly isPartiallyFilled = computed(() => Number(this.data.order.filledQuantity) > 0);

  protected readonly showLimitPrice = computed(() => {
    this.revision();
    return orderTypeNeedsLimitPrice(this.form.controls.orderType.value);
  });

  protected readonly showTriggerPrice = computed(() => {
    this.revision();
    return orderTypeNeedsTriggerPrice(this.form.controls.orderType.value);
  });

  protected readonly disclosedTooSmall = computed(() => {
    this.revision();
    const disclosed = Number(this.form.controls.disclosedQuantity.value);
    const quantity = Number(this.form.controls.quantity.value);
    if (!disclosed || !quantity) {
      return false;
    }
    return disclosed < quantity * 0.3;
  });

  /**
   * Blocks the amendment the broker would reject anyway, with a message that
   * says why — a round trip to hear "invalid quantity" teaches nobody
   * anything.
   */
  protected readonly validationError = computed<string | undefined>(() => {
    this.revision();
    const raw = this.form.getRawValue();

    const quantity = Number(raw.quantity);
    if (raw.quantity && (!Number.isFinite(quantity) || quantity <= 0)) {
      return 'Quantity must be greater than zero.';
    }

    const filled = Number(this.data.order.filledQuantity);
    if (raw.quantity && quantity < filled) {
      return `Quantity cannot be below the ${filled} already filled — cancel the order instead.`;
    }

    if (this.showLimitPrice() && raw.limitPrice && Number(raw.limitPrice) <= 0) {
      return 'The limit price must be greater than zero.';
    }

    if (this.showTriggerPrice() && raw.triggerPrice && Number(raw.triggerPrice) <= 0) {
      return 'The trigger price must be greater than zero.';
    }

    return undefined;
  });

  protected readonly canSubmit = computed(() => {
    this.revision();
    return !this.validationError() && Object.keys(this.buildRequest()).length > 0;
  });

  constructor() {
    const order = this.data.order;

    this.form.patchValue({
      quantity: String(order.quantity),
      limitPrice: order.limitPrice ? String(order.limitPrice.amount) : '',
      triggerPrice: order.triggerPrice ? String(order.triggerPrice.amount) : '',
      orderType: order.orderType,
      timeInForce: order.timeInForce,
    });

    this.form.valueChanges.subscribe(() => this.revision.update((n) => n + 1));
  }

  /**
   * Straight off the manifest — see the class doc for why this is never a
   * connector check, and `canModifyField` for why it must not be a bare
   * `.includes()` either.
   */
  protected canModify(field: string): boolean {
    return canModifyField(this.data.manifest, field);
  }

  protected submit(): void {
    const request = this.buildRequest();
    if (Object.keys(request).length > 0) {
      this.dialogRef.close(request);
    }
  }

  /**
   * The DIFF against the live order, not the whole form. An unchanged field
   * is omitted entirely so the broker leaves it alone.
   */
  private buildRequest(): ModifyOrderRequest {
    const raw = this.form.getRawValue();
    const order = this.data.order;
    const request: Record<string, unknown> = {};

    if (this.canModify('orderType') && raw.orderType !== order.orderType) {
      request['orderType'] = raw.orderType;
    }

    if (this.canModify('quantity') && raw.quantity && raw.quantity !== String(order.quantity)) {
      request['quantity'] = raw.quantity;
    }

    if (this.canModify('timeInForce') && raw.timeInForce !== order.timeInForce) {
      request['timeInForce'] = raw.timeInForce;
    }

    // Prices carry their currency, and it is the ORDER'S currency — never a
    // display currency and never assumed. A cross-border amendment priced in
    // the wrong unit is rejected by the connector, which is the correct place
    // for that to fail, but there is no reason to send it.
    if (this.canModify('limitPrice') && this.showLimitPrice() && raw.limitPrice) {
      const currency = order.limitPrice?.currency ?? order.averagePrice?.currency;
      if (currency && raw.limitPrice !== String(order.limitPrice?.amount ?? '')) {
        request['limitPrice'] = { amount: raw.limitPrice, currency };
      }
    }

    if (this.canModify('triggerPrice') && this.showTriggerPrice() && raw.triggerPrice) {
      const currency = order.triggerPrice?.currency ?? order.limitPrice?.currency ?? order.averagePrice?.currency;
      if (currency && raw.triggerPrice !== String(order.triggerPrice?.amount ?? '')) {
        request['triggerPrice'] = { amount: raw.triggerPrice, currency };
      }
    }

    if (this.canModify('disclosedQuantity') && raw.disclosedQuantity) {
      request['disclosedQuantity'] = raw.disclosedQuantity;
    }

    return request as ModifyOrderRequest;
  }
}

import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { positionEffectLabel } from '../../core/labels';
import type { ConnectorManifest, ConvertPositionRequest, InstrumentKey, PositionEffect, Side } from '../../core/models';

export interface ConvertPositionDialogData {
  readonly brokerLinkId: string;
  readonly instrument: InstrumentKey;
  /** Signed net quantity: negative means the position is short. */
  readonly netQuantity: string;
  readonly currentProduct: PositionEffect;
  readonly manifest: ConnectorManifest;
}

/**
 * Moves an open position between margin products — the intraday-to-delivery
 * rescue, most often, taken at 15:15 by someone who has decided not to be
 * squared off.
 *
 * NOT A TRADE, and the dialog says so out loud. Nothing is bought or sold,
 * no fill appears in the blotter, and the position keeps its original entry
 * price. What changes is the settlement basis, and therefore the margin the
 * broker blocks — which is exactly why the direction matters: converting
 * intraday to delivery INCREASES the capital required, because delivery is
 * not leveraged. It can fail on margin, and it fails at the worst moment,
 * so the warning is shown before the button rather than after the error.
 *
 * The target products come from the MANIFEST, minus the one the position is
 * already in. No connector is named anywhere in this file.
 */
@Component({
  selector: 'ak-convert-position-dialog',
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
    <h2 mat-dialog-title>Convert position</h2>

    <mat-dialog-content>
      <p class="mb-3 text-[13px] text-text-secondary">
        <span class="font-medium text-text-primary">{{ data.instrument }}</span>
        · {{ absQuantity() }} {{ isShort() ? 'short' : 'long' }}
        · currently {{ positionEffectLabel(data.currentProduct) }}
      </p>

      <p class="mb-3 flex items-start gap-1.5 rounded-sm bg-surface-2 p-2 text-xs text-text-secondary">
        <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">info</mat-icon>
        <span>
          This does not buy or sell anything. Your entry price and position size stay exactly as
          they are — only the product they settle under changes, which changes how much margin
          your broker blocks against them.
        </span>
      </p>

      <form [formGroup]="form" class="flex flex-col gap-3">
        <mat-form-field appearance="outline" class="w-full">
          <mat-label>Convert to</mat-label>
          <mat-select formControlName="to">
            @for (effect of targets(); track effect) {
              <mat-option [value]="effect">{{ positionEffectLabel(effect) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline" class="w-full">
          <mat-label>Quantity</mat-label>
          <input matInput type="number" inputmode="decimal" formControlName="quantity" min="0" />
          <!--
            Partial conversion is a real and common choice: take delivery of
            some, let the rest square off. So this is an editable field
            defaulted to the whole position, not a fixed label.
          -->
          <mat-hint>Up to {{ absQuantity() }}. Converting part of a position is allowed.</mat-hint>
        </mat-form-field>
      </form>

      @if (increasesMargin()) {
        <p class="mt-3 flex items-start gap-1.5 text-xs text-warning">
          <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">warning</mat-icon>
          <span>
            Moving out of an intraday product needs the full, unleveraged capital for this
            position. If your account cannot cover it the broker will refuse the conversion.
          </span>
        </p>
      }

      @if (validationError(); as error) {
        <p class="mt-2 flex items-start gap-1.5 text-xs text-danger" role="alert">
          <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">error</mat-icon>
          {{ error }}
        </p>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="dialogRef.close()">Cancel</button>
      <button mat-flat-button type="button" [disabled]="!!validationError()" (click)="submit()">Convert</button>
    </mat-dialog-actions>
  `,
})
export class ConvertPositionDialogComponent {
  protected readonly data = inject<ConvertPositionDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef =
    inject<MatDialogRef<ConvertPositionDialogComponent, ConvertPositionRequest>>(MatDialogRef);

  protected readonly positionEffectLabel = positionEffectLabel;

  private readonly revision = signal(0);

  protected readonly isShort = computed(() => Number(this.data.netQuantity) < 0);

  /** Quantity is always sent positive; direction rides on `side`. */
  protected readonly absQuantity = computed(() => Math.abs(Number(this.data.netQuantity)));

  /** Every product the broker offers except the one the position already sits in. */
  protected readonly targets = computed<readonly PositionEffect[]>(() =>
    this.data.manifest.orders.positionEffects.filter((effect) => effect !== this.data.currentProduct),
  );

  protected readonly form = new FormGroup({
    to: new FormControl<PositionEffect>('delivery', { nonNullable: true }),
    quantity: new FormControl<string>('', { nonNullable: true }),
  });

  /**
   * Leaving an intraday product means giving up the leverage that made the
   * position affordable in the first place.
   */
  protected readonly increasesMargin = computed(() => {
    this.revision();
    return this.data.currentProduct === 'intraday' && this.form.controls.to.value !== 'intraday';
  });

  protected readonly validationError = computed<string | undefined>(() => {
    this.revision();
    const quantity = Number(this.form.controls.quantity.value);

    if (!Number.isFinite(quantity) || quantity <= 0) {
      return 'Enter a quantity greater than zero.';
    }

    if (quantity > this.absQuantity()) {
      return `You only hold ${this.absQuantity()}.`;
    }

    if (this.targets().length === 0) {
      return 'This broker offers no other product to convert into.';
    }

    return undefined;
  });

  constructor() {
    this.form.patchValue({
      to: this.targets()[0] ?? 'delivery',
      quantity: String(this.absQuantity()),
    });

    this.form.valueChanges.subscribe(() => this.revision.update((n) => n + 1));
  }

  protected submit(): void {
    if (this.validationError()) {
      return;
    }

    const raw = this.form.getRawValue();

    this.dialogRef.close({
      brokerLinkId: this.data.brokerLinkId,
      instrument: this.data.instrument,
      // A short position was opened with a SELL, and that is the side the
      // broker keys the conversion on — not the side that would close it.
      side: (this.isShort() ? 'sell' : 'buy') satisfies Side,
      quantity: raw.quantity,
      from: this.data.currentProduct,
      to: raw.to,
    });
  }
}

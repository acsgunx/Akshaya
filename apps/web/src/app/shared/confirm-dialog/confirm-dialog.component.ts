import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';

export interface ConfirmDialogData {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel?: string;
  readonly cancelLabel?: string;
  /** Styles the confirm button as destructive (kill switch, cancel-all, unlink). */
  readonly danger?: boolean;
  /**
   * When set, the confirm button stays disabled until the user types this
   * text into the field the dialog renders — used ONLY for the highest-
   * consequence actions (engaging the kill switch, cancel-all across every
   * broker). A plain "are you sure" click is too cheap an action for those;
   * typing forces a half-second of deliberate attention.
   */
  readonly typeToConfirm?: string;
}

/**
 * One generic confirmation dialog for the whole app — order submission,
 * kill-switch engagement, cancel-all, unlinking a broker all use this same
 * component with different `ConfirmDialogData`, rather than each screen
 * rolling its own modal markup and (inevitably) its own slightly-different
 * keyboard behaviour.
 *
 * KEYBOARD NOTE: `Enter` does NOT submit this dialog when `danger` is true.
 * See DESIGN.md "the keyboard model" — a destructive confirmation must
 * require an explicit pointer/Space activation of the confirm button so a
 * reflexive double-Enter (open the dialog, then immediately confirm it)
 * cannot fire a kill-switch or cancel-all by accident.
 */
@Component({
  selector: 'ak-confirm-dialog',
  standalone: true,
  imports: [MatDialogModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p>{{ data.message }}</p>
      @if (data.typeToConfirm) {
        <label class="ak-type-confirm">
          <span>Type "{{ data.typeToConfirm }}" to confirm</span>
          <input
            type="text"
            class="ak-focus-halo"
            [attr.aria-label]="'Type ' + data.typeToConfirm + ' to confirm'"
            (input)="onTypedChange($event)"
          />
        </label>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button (click)="dialogRef.close(false)" (keydown.enter)="dialogRef.close(false)">
        {{ data.cancelLabel ?? 'Cancel' }}
      </button>
      <button
        mat-flat-button
        [class.ak-btn-danger]="data.danger"
        [disabled]="!canConfirm()"
        (click)="dialogRef.close(true)"
        (keydown.enter)="data.danger ? null : dialogRef.close(true)"
      >
        {{ data.confirmLabel ?? 'Confirm' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .ak-type-confirm {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin-top: 12px;
      font-size: 12px;
      color: var(--ak-text-secondary);
    }
    .ak-type-confirm input {
      padding: 8px 10px;
      background: var(--ak-surface-2);
      border: 1px solid var(--ak-border);
      border-radius: var(--ak-radius-sm);
      color: var(--ak-text-primary);
      font: inherit;
    }
  `,
})
export class ConfirmDialogComponent {
  readonly dialogRef = inject(MatDialogRef<ConfirmDialogComponent, boolean>);
  readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);

  private typedValue = '';

  canConfirm(): boolean {
    return !this.data.typeToConfirm || this.typedValue === this.data.typeToConfirm;
  }

  onTypedChange(event: Event): void {
    this.typedValue = (event.target as HTMLInputElement).value;
  }
}

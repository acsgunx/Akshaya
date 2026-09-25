import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';

import {
  PRICE_SOURCES,
  STUDY_COLORS,
  indicatorFor,
  normalizeParams,
  studyLabel,
  type StudyInstance,
  type StudyParams,
} from '@akshaya/market/ui-price-chart';

/** What comes back when the sheet closes: the edited study, `'remove'`, or nothing. */
export type ChartStudySettingsResult = StudyInstance | 'remove' | undefined;

/**
 * One study's settings: its parameters, its colour, and the button that takes
 * it off the chart.
 *
 * Every field here is generated from the indicator's own `params` metadata, so a
 * new indicator gets a working settings sheet the moment it is added to
 * `INDICATORS` — there is no per-indicator form anywhere.
 *
 * Values are clamped by `normalizeParams` on the way out as well as on the way
 * in. A number field will hand back an empty string, a pasted `1e9` or a typed
 * `-4`, and none of those may reach a rolling window.
 */
@Component({
  selector: 'ak-chart-study-settings',
  standalone: true,
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ title() }}</h2>

    <mat-dialog-content>
      @if (definition(); as definition) {
        <p class="mb-3 text-[13px] text-text-secondary">{{ definition.summary }}</p>

        <div class="flex flex-col gap-3">
          @for (param of definition.params; track param.key) {
            @if (param.kind === 'source') {
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>{{ param.label }}</mat-label>
                <mat-select [value]="params()[param.key]" (valueChange)="set(param.key, $event)">
                  @for (option of sources; track option.value) {
                    <mat-option [value]="option.value">{{ option.label }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
            } @else {
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>{{ param.label }}</mat-label>
                <input
                  matInput
                  type="number"
                  [min]="param.min"
                  [max]="param.max"
                  [step]="param.step"
                  [value]="params()[param.key]"
                  (input)="set(param.key, $any($event.target).value)"
                />
                <mat-hint>{{ param.min }} to {{ param.max }}</mat-hint>
              </mat-form-field>
            }
          } @empty {
            <p class="text-[13px] text-text-secondary">This indicator has nothing to tune.</p>
          }

          <fieldset>
            <legend class="mb-1.5 text-xs text-text-secondary">Line colour</legend>
            <div class="flex flex-wrap gap-1.5" role="radiogroup" aria-label="Line colour">
              @for (option of colors; track option.token) {
                <button
                  type="button"
                  class="ak-swatch"
                  role="radio"
                  [class.ak-swatch--on]="color() === option.token"
                  [attr.aria-checked]="color() === option.token"
                  [attr.aria-label]="option.label"
                  [matTooltip]="option.label"
                  [style.background]="'var(' + option.token + ')'"
                  (click)="color.set(option.token)"
                ></button>
              }
            </div>
            <p class="mt-1.5 text-[11px] leading-snug text-text-secondary">
              A study with more than one line takes its others from the colours
              beside this one, so they can never come out the same.
            </p>
          </fieldset>
        </div>
      } @else {
        <p class="text-sm text-text-secondary">This indicator is no longer available.</p>
      }
    </mat-dialog-content>

    <mat-dialog-actions>
      <button mat-button class="text-danger" (click)="remove()">
        <mat-icon aria-hidden="true">delete_outline</mat-icon>
        Remove
      </button>
      <span class="flex-1"></span>
      <button mat-button (click)="reset()" [disabled]="!definition()">Reset</button>
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!definition()" (click)="apply()">Apply</button>
    </mat-dialog-actions>
  `,
  styles: `
    @reference '../../../../../src/styles/tailwind.css';

    .ak-swatch {
      width: 1.5rem;
      height: 1.5rem;
      border: 2px solid transparent;
      border-radius: var(--radius-sm);
      outline-offset: 2px;
    }

    .ak-swatch--on {
      border-color: var(--color-text-primary);
    }
  `,
})
export class ChartStudySettingsComponent {
  private readonly study = inject<StudyInstance>(MAT_DIALOG_DATA);
  private readonly dialog = inject(MatDialogRef<ChartStudySettingsComponent, ChartStudySettingsResult>);

  protected readonly sources = PRICE_SOURCES;
  protected readonly colors = STUDY_COLORS;
  protected readonly definition = computed(() => indicatorFor(this.study.kind));

  protected readonly params = signal<Record<string, number | string>>({ ...this.study.params });
  protected readonly color = signal(this.study.color);
  protected readonly title = computed(() => studyLabel(this.study.kind, this.params()));

  /**
   * Keeps the raw field value while the user is typing, so a half-typed "2" on
   * the way to "20" is not snapped to the minimum under the cursor. The clamp
   * happens in `apply`.
   */
  protected set(key: string, value: unknown): void {
    const parsed = typeof value === 'string' && value.trim() !== '' && !Number.isNaN(Number(value))
      ? Number(value)
      : value;
    this.params.update((params) => ({ ...params, [key]: parsed as number | string }));
  }

  protected reset(): void {
    const definition = this.definition();
    if (definition) { this.params.set({ ...definition.defaults }); }
  }

  protected apply(): void {
    const definition = this.definition();
    if (!definition) { return; }
    const params: StudyParams = normalizeParams(definition, this.params());
    this.dialog.close({ ...this.study, params, color: this.color() });
  }

  protected remove(): void {
    this.dialog.close('remove');
  }
}

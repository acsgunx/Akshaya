import { ChangeDetectionStrategy, Component, computed, inject, signal, type Signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';

import {
  INDICATORS,
  INDICATOR_CATEGORIES,
  studyLabel,
  type IndicatorCategory,
  type IndicatorDefinition,
  type IndicatorKind,
  type StudyInstance,
} from '@akshaya/market/ui-price-chart';

/**
 * The picker edits the chart's study list LIVE, through the parent's own
 * signal and mutators, rather than handing back a list when it closes. Adding
 * four moving averages one after another and watching each land is the whole
 * point of the dialog; a modal that only commits on OK would make that four
 * open-and-close cycles.
 */
export interface ChartIndicatorPickerData {
  readonly studies: Signal<readonly StudyInstance[]>;
  readonly add: (kind: IndicatorKind) => void;
  readonly remove: (id: string) => void;
}

interface Group {
  readonly category: IndicatorCategory;
  readonly items: readonly IndicatorDefinition[];
}

/**
 * The indicator library: search it, see what it measures, add it.
 *
 * It replaces a flat `mat-menu` of six checkboxes. With thirty-odd indicators a
 * menu stops working — you cannot skim it, you cannot search it, and a checkbox
 * cannot express "two SMAs at different lengths", which is the most ordinary
 * thing anyone does with a moving average.
 *
 * The list comes from `INDICATORS`, so nothing here needs touching when one is
 * added; the categories come from the definitions' own `category`.
 */
@Component({
  selector: 'ak-chart-indicator-picker',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Indicators</h2>

    <mat-dialog-content class="ak-picker">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="w-full">
        <mat-label>Search indicators</mat-label>
        <mat-icon matPrefix aria-hidden="true">search</mat-icon>
        <input
          matInput
          type="search"
          autocomplete="off"
          [value]="query()"
          (input)="query.set($any($event.target).value)"
          placeholder="RSI, volume, Bollinger…"
        />
      </mat-form-field>

      @if (active().length) {
        <section class="mt-3" aria-labelledby="ak-picker-active">
          <h3 id="ak-picker-active" class="mb-1 text-[11px] font-semibold uppercase tracking-wide text-text-secondary">
            On this chart ({{ active().length }})
          </h3>
          <ul class="flex flex-wrap gap-1">
            @for (study of active(); track study.id) {
              <li>
                <button
                  type="button"
                  class="ak-picker-chip"
                  [attr.aria-label]="'Remove ' + label(study)"
                  [matTooltip]="'Remove ' + label(study)"
                  (click)="data.remove(study.id)"
                >
                  <span class="size-2 rounded-full" [style.background]="'var(' + study.color + ')'" aria-hidden="true"></span>
                  {{ label(study) }}
                  <mat-icon class="ak-picker-chip-icon" aria-hidden="true">close</mat-icon>
                </button>
              </li>
            }
          </ul>
        </section>
      }

      @for (group of groups(); track group.category) {
        <section class="mt-4" [attr.aria-label]="group.category">
          <h3 class="mb-1 text-[11px] font-semibold uppercase tracking-wide text-text-secondary">{{ group.category }}</h3>
          <ul class="flex flex-col">
            @for (item of group.items; track item.kind) {
              <li>
                <button type="button" class="ak-picker-row" (click)="data.add(item.kind)">
                  <mat-icon class="shrink-0 text-text-secondary" aria-hidden="true">
                    {{ item.overlay ? 'show_chart' : 'align_vertical_bottom' }}
                  </mat-icon>
                  <span class="min-w-0">
                    <span class="flex items-center gap-1.5 text-[13px] font-medium">
                      {{ item.label }}
                      @if (countOf(item.kind); as count) {
                        <span class="rounded bg-surface-3 px-1 text-[10px] text-text-secondary">{{ count }} on chart</span>
                      }
                    </span>
                    <small class="block text-[11px] leading-snug text-text-secondary">{{ item.summary }}</small>
                  </span>
                  <mat-icon class="ml-auto shrink-0 text-text-secondary" aria-hidden="true">add</mat-icon>
                </button>
              </li>
            }
          </ul>
        </section>
      } @empty {
        <p class="mt-4 text-sm text-text-secondary">No indicator matches “{{ query() }}”.</p>
      }

      <p class="mt-4 rounded-sm bg-surface-2 p-2 text-[11px] leading-relaxed text-text-secondary">
        Every study is calculated from the bars this chart has loaded, and needs
        enough of them to warm up — a 200-period average shows nothing until the
        201st bar. Values on a bar that is still forming change with the price.
        Volume-based studies hold their last value on the forming bar, because a
        live tick carries no per-bar volume.
      </p>
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-flat-button (click)="close()">Done</button>
    </mat-dialog-actions>
  `,
  styles: `
    @reference '../../../../../src/styles/tailwind.css';

    .ak-picker {
      /* Tall enough to skim a category without scrolling, short enough to leave
         the chart visible behind it on a laptop. */
      min-height: 22rem;
    }

    .ak-picker-row {
      display: flex;
      width: 100%;
      align-items: flex-start;
      gap: 0.625rem;
      border-radius: var(--radius-sm);
      padding: 0.375rem 0.5rem;
      text-align: left;
    }

    .ak-picker-row:hover,
    .ak-picker-row:focus-visible {
      background: var(--color-surface-2);
    }

    .ak-picker-chip {
      display: inline-flex;
      align-items: center;
      gap: 0.375rem;
      border: 1px solid var(--color-border);
      border-radius: 999px;
      padding: 0.125rem 0.5rem;
      font-size: 11px;
    }

    .ak-picker-chip:hover {
      border-color: var(--color-danger);
      color: var(--color-danger);
    }

    .ak-picker-chip-icon {
      width: 13px;
      height: 13px;
      font-size: 13px;
    }
  `,
})
export class ChartIndicatorPickerComponent {
  protected readonly data = inject<ChartIndicatorPickerData>(MAT_DIALOG_DATA);
  private readonly dialog = inject(MatDialogRef<ChartIndicatorPickerComponent>);

  protected readonly query = signal('');
  protected readonly active = computed(() => this.data.studies());

  /**
   * Matches the search against the name, the category and the summary. The
   * summary matters: someone looking for "volatility" should find ATR and
   * Bollinger Bands without already knowing either name.
   */
  protected readonly groups = computed<readonly Group[]>(() => {
    const needle = this.query().trim().toLowerCase();
    const matches = (item: IndicatorDefinition) => needle.length === 0
      || `${item.label} ${item.category} ${item.summary}`.toLowerCase().includes(needle);
    return INDICATOR_CATEGORIES
      .map((category) => ({ category, items: INDICATORS.filter((item) => item.category === category && matches(item)) }))
      .filter((group) => group.items.length > 0);
  });

  protected label(study: StudyInstance): string {
    return studyLabel(study.kind, study.params);
  }

  /** How many of this indicator are already on the chart; 0 renders nothing. */
  protected countOf(kind: IndicatorKind): number {
    return this.data.studies().filter((study) => study.kind === kind).length;
  }

  protected close(): void {
    this.dialog.close();
  }
}

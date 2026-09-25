import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { studyLabel, type StudyInstance, type StudyLegendEntry } from '@akshaya/market/ui-price-chart';

/** One legend row: the study, and what it reads at the cursor. */
interface LegendRow {
  readonly id: string;
  readonly label: string;
  readonly visible: boolean;
  readonly color: string;
  readonly values: readonly { readonly title: string; readonly color: string; readonly text: string }[];
}

/**
 * The values legend over the top-left of the chart: one row per study, showing
 * what it reads AT THE CURSOR rather than only its name.
 *
 * This is the thing the screen was most obviously missing. Before it, an
 * indicator announced itself as a chip reading "RSI 14" and the only way to
 * find out what RSI 14 actually was on the bar you were looking at was to
 * squint at the pane's axis. A study you cannot read a number off is decoration.
 *
 * Hidden studies stay listed, greyed, with their eye crossed out — otherwise
 * turning one off would be the same gesture as losing it, and the settings you
 * spent a minute on would have to be typed again.
 *
 * It sits over the canvas the way TradingView's does, which is a real
 * trade-off: it covers the top-left corner of the price action. Settings →
 * "Values legend" turns it off, and it is deliberately narrow and one line per
 * study so that corner stays small.
 */
@Component({
  selector: 'ak-chart-legend',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="pointer-events-none absolute left-2 top-2 z-10 flex max-w-[min(calc(100%-1rem),26rem)] flex-col gap-0.5" role="list" aria-label="Indicator values at the cursor">
      @for (row of rows(); track row.id) {
        <div
          class="ak-legend-row pointer-events-auto flex items-center gap-1.5 overflow-hidden rounded-sm px-1.5 py-0.5 text-[11px] leading-tight tabular-nums"
          [class.opacity-55]="!row.visible"
          role="listitem"
        >
          <button
            type="button"
            class="ak-legend-action"
            [attr.aria-label]="(row.visible ? 'Hide ' : 'Show ') + row.label"
            [matTooltip]="row.visible ? 'Hide this indicator' : 'Show this indicator'"
            (click)="visibilityToggle.emit(row.id)"
          >
            <mat-icon class="ak-legend-icon">{{ row.visible ? 'visibility' : 'visibility_off' }}</mat-icon>
          </button>
          <span class="size-2 shrink-0 rounded-full" [style.background]="'var(' + row.color + ')'" aria-hidden="true"></span>
          <!--
            The values clip rather than push the row wider than the chart. A
            five-plot Ichimoku on a phone is wider than the canvas it is drawn
            over, and a legend that overflows puts its own controls off-screen
            and its numbers over the price scale.
          -->
          <span class="flex min-w-0 flex-1 items-center gap-1.5 overflow-hidden whitespace-nowrap">
            <span class="shrink-0 font-semibold">{{ row.label }}</span>
            @for (value of row.values; track value.title) {
              <span class="shrink-0" [style.color]="'var(' + value.color + ')'">
                @if (row.values.length > 1) { <span class="opacity-70">{{ value.title }}</span> }
                {{ value.text }}
              </span>
            }
            @if (!row.visible) { <span class="shrink-0 text-text-tertiary">hidden</span> }
          </span>
          <button
            type="button"
            class="ak-legend-action"
            [attr.aria-label]="'Settings for ' + row.label"
            matTooltip="Indicator settings"
            (click)="configure.emit(row.id)"
          >
            <mat-icon class="ak-legend-icon">settings</mat-icon>
          </button>
          <button
            type="button"
            class="ak-legend-action"
            [attr.aria-label]="'Remove ' + row.label"
            matTooltip="Remove this indicator"
            (click)="remove.emit(row.id)"
          >
            <mat-icon class="ak-legend-icon">close</mat-icon>
          </button>
        </div>
      }
    </div>
  `,
  styles: `
    @reference '../../../../../src/styles/tailwind.css';

    /*
      The row has to be legible over whatever the candles are doing underneath
      it, so it carries its own scrim rather than a flat panel. Mixing the card's
      OWN surface token with transparency keeps it the right colour in both
      themes, instead of a grey that is right in one of them.
    */
    .ak-legend-row {
      background: color-mix(in srgb, var(--color-surface-1) 82%, transparent);
      backdrop-filter: blur(2px);
    }

    .ak-legend-action {
      display: inline-flex;
      align-items: center;
      color: var(--color-text-secondary);
      opacity: 0;
      transition: opacity var(--duration-ak) var(--ease-ak);
    }

    /* Revealed on hover like TradingView's, but never hidden from the keyboard
       or from a pointer that cannot hover — an action nobody can reach is not an
       action. */
    .ak-legend-row:hover .ak-legend-action,
    .ak-legend-row:focus-within .ak-legend-action,
    .ak-legend-action:focus-visible {
      opacity: 1;
    }

    @media (hover: none) {
      .ak-legend-action {
        opacity: 1;
      }
    }

    .ak-legend-action:hover {
      color: var(--color-text-primary);
    }

    .ak-legend-icon {
      width: 14px;
      height: 14px;
      font-size: 14px;
    }
  `,
})
export class ChartLegendComponent {
  /** Every study on the chart, visible or not — the legend lists them all. */
  readonly studies = input.required<readonly StudyInstance[]>();
  /** Values at the cursor, for the visible ones. Keyed by study id. */
  readonly entries = input.required<readonly StudyLegendEntry[]>();
  /** Decimals for a study that reads in the instrument's own price units. */
  readonly precision = input(2);

  readonly visibilityToggle = output<string>();
  readonly configure = output<string>();
  readonly remove = output<string>();

  protected readonly rows = computed<readonly LegendRow[]>(() => {
    const values = new Map(this.entries().map((entry) => [entry.id, entry]));
    return this.studies().map((study) => ({
      id: study.id,
      label: studyLabel(study.kind, study.params),
      visible: study.visible,
      color: study.color,
      values: (values.get(study.id)?.values ?? []).map((value) => ({
        title: value.title,
        color: value.color,
        text: this.format(value.value, value.precision),
      })),
    }));
  });

  /**
   * A study's value as text. `precision` undefined means the study reads in the
   * instrument's own units, so it gets the instrument's tick precision; a study
   * with its own scale (RSI, CMF, OBV) brings its own.
   *
   * On-balance volume runs to eight or nine digits within a session, which would
   * be the widest thing on the chart, so anything that large is abbreviated.
   */
  private format(value: number, precision: number | undefined): string {
    const decimals = precision ?? this.precision();
    if (Math.abs(value) >= 1e6 && decimals === 0) {
      return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 2 }).format(value);
    }
    return new Intl.NumberFormat(undefined, {
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals,
    }).format(value);
  }
}

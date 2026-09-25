import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { DRAWING_TOOLS, type DrawingTool } from '@akshaya/market/ui-price-chart';

/** Whether undo and redo have anything to act on, and how many drawings are on the chart. */
export interface DrawingToolState {
  readonly undo: boolean;
  readonly redo: boolean;
  readonly count: number;
  /** A single drawing is selected, so it can be removed on its own. */
  readonly selected: boolean;
}

/**
 * The drawing rail down the left of the chart: the tools themselves, then
 * undo/redo and the two controls that act on every drawing at once.
 *
 * The tools come from `DRAWING_TOOLS` rather than being listed here, so the
 * rail and the chart that interprets a click can never disagree about what
 * exists.
 */
@Component({
  selector: 'ak-chart-tools',
  // The layout around it is the parent's: this host must not become the flex item.
  host: { class: 'contents' },
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="flex w-12 shrink-0 flex-col items-center gap-0.5 border-r border-border bg-surface-2 py-2" aria-label="Drawing tools">
      @for (item of tools; track item.id) {
        <button mat-icon-button class="ak-tool" [class.ak-tool--active]="tool() === item.id" [attr.aria-label]="item.label" [matTooltip]="item.label" matTooltipPosition="right" [attr.aria-pressed]="tool() === item.id" [disabled]="!ready()" (click)="toolSelect.emit(item.id)"><mat-icon>{{ item.icon }}</mat-icon></button>
      }
      <span class="my-2 w-6 border-t border-border" aria-hidden="true"></span>
      <button mat-icon-button aria-label="Undo last drawing" matTooltip="Undo drawing" [disabled]="!state().undo || !ready()" (click)="undo.emit()"><mat-icon>undo</mat-icon></button>
      <button mat-icon-button aria-label="Redo drawing" matTooltip="Redo drawing" [disabled]="!state().redo || !ready()" (click)="redo.emit()"><mat-icon>redo</mat-icon></button>
      <button mat-icon-button aria-label="Toggle drawing visibility" matTooltip="Show / hide drawings" [attr.aria-pressed]="visible()" (click)="visibilityToggle.emit()"><mat-icon>{{ visible() ? 'visibility' : 'visibility_off' }}</mat-icon></button>
      <!-- Click a drawing with the crosshair tool to select it; this removes
           that one. "Remove drawings" below still clears the lot. -->
      <button mat-icon-button aria-label="Remove the selected drawing" matTooltip="Remove selected drawing · Delete" [disabled]="!state().selected || !ready()" (click)="removeSelected.emit()"><mat-icon>backspace</mat-icon></button>
      <button mat-icon-button aria-label="Remove all drawings" matTooltip="Remove all drawings" [disabled]="!state().count || !ready()" (click)="remove.emit()"><mat-icon>delete_outline</mat-icon></button>
    </nav>
  `,
  styles: `
    @reference '../../../../../src/styles/tailwind.css';

    .ak-tool--active {
      color: var(--color-brand);
      background: color-mix(in srgb, var(--color-brand) 12%, var(--color-surface-1));
    }
  `,
})
export class ChartToolsComponent {
  protected readonly tools = DRAWING_TOOLS;

  readonly tool = input.required<DrawingTool>();
  readonly state = input.required<DrawingToolState>();
  readonly visible = input(true);
  /** False until there are bars to draw on; every tool is dead without them. */
  readonly ready = input(false);

  readonly toolSelect = output<DrawingTool>();
  readonly undo = output<void>();
  readonly redo = output<void>();
  readonly visibilityToggle = output<void>();
  readonly removeSelected = output<void>();
  readonly remove = output<void>();
}

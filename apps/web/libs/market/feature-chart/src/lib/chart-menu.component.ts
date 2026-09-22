import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, inject, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

import type { Candle, InstrumentKey } from '@akshaya/shared/models';

/** Where the chart was right-clicked, and the chart values under that point. */
export interface ChartMenuAnchor {
  readonly x: number;
  readonly y: number;
  readonly price: number | undefined;
  readonly time: number | undefined;
}

/**
 * The chart's right-click menu.
 *
 * Not a `MatMenu`: its anchor is a pointer position on a canvas, not an
 * element, so a CDK overlay would open at the wrong place. It is a real
 * `role="menu"` list instead, which keeps the keyboard contract — arrows,
 * Home and End here; Escape is the chart's, because it also cancels drawing.
 *
 * The menu acts on the chart through its outputs and never reaches for it,
 * with one exception: copying is a clipboard write with nothing to decide, so
 * it happens here and reports through `notice`.
 */
@Component({
  selector: 'ak-chart-menu',
  standalone: true,
  imports: [DecimalPipe, MatIconModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents', '(keydown)': 'onKeydown($event)' },
  template: `
    <!-- Swallows the click that dismisses the menu, so it cannot also reach the chart. -->
    <div class="fixed inset-0 z-20 cursor-default" aria-hidden="true"></div>
    <div
      role="menu"
      aria-label="Chart actions"
      tabindex="-1"
      class="ak-menu absolute z-30 w-60 rounded-md border border-border bg-surface-2 py-1 text-xs"
      [style.left]="'min(' + anchor().x + 'px, calc(100% - 15.5rem))'"
      [style.top]="'min(' + anchor().y + 'px, calc(100% - 19rem))'"
    >
      <button type="button" role="menuitem" class="ak-menu-item" (click)="resetView.emit()"><mat-icon aria-hidden="true">fit_screen</mat-icon>Reset chart view</button>
      <!-- An explicit undefined check, not a truthiness one: a price of 0 is still a point on the chart. -->
      @if (anchor().price !== undefined) {
        <button type="button" role="menuitem" class="ak-menu-item" (click)="copy(anchor().price!.toFixed(precision()), 'Price')"><mat-icon aria-hidden="true">content_copy</mat-icon>Copy price {{ anchor().price | number: numberFormat() }}</button>
        <button type="button" role="menuitem" class="ak-menu-item" (click)="addLine.emit(anchor().price!)"><mat-icon aria-hidden="true">horizontal_rule</mat-icon>Line at {{ anchor().price | number: numberFormat() }}</button>
        <button type="button" role="menuitem" class="ak-menu-item" (click)="measure.emit()"><mat-icon aria-hidden="true">straighten</mat-icon>Measure from here</button>
      }
      @if (bar(); as bar) {
        <button type="button" role="menuitem" class="ak-menu-item" (click)="copy(barText(bar), 'Bar values')"><mat-icon aria-hidden="true">content_copy</mat-icon>Copy bar OHLC</button>
      }
      <div class="my-1 border-t border-border" role="separator"></div>
      <button type="button" role="menuitem" class="ak-menu-item" (click)="drawingsToggle.emit()"><mat-icon aria-hidden="true">{{ drawingsVisible() ? 'visibility_off' : 'visibility' }}</mat-icon>{{ drawingsVisible() ? 'Hide' : 'Show' }} drawings</button>
      @if (drawingCount() > 0) {
        <button type="button" role="menuitem" class="ak-menu-item text-danger" (click)="removeDrawings.emit()"><mat-icon aria-hidden="true">delete_outline</mat-icon>Remove {{ drawingCount() }} drawings</button>
      }
      <div class="my-1 border-t border-border" role="separator"></div>
      <button type="button" role="menuitem" class="ak-menu-item" [routerLink]="['/trade', brokerLinkId(), instrument()]" (click)="dismiss.emit()"><mat-icon aria-hidden="true">north_east</mat-icon>Trade {{ instrumentLabel() }}</button>
    </div>
  `,
  styles: `
    @reference '../../../../../src/styles/tailwind.css';

    .ak-menu { box-shadow: var(--shadow-ak-2); }

    .ak-menu-item {
      @apply flex w-full cursor-pointer items-center gap-2 px-3 py-2 text-left;

      &:hover, &:focus-visible { background-color: var(--color-surface-3); }
      mat-icon { font-size: 16px; width: 16px; height: 16px; }
    }
  `,
})
export class ChartMenuComponent {
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);

  readonly anchor = input.required<ChartMenuAnchor>();
  /** The bar under the pointer, when the click landed on one. */
  readonly bar = input<Candle | undefined>(undefined);
  readonly precision = input(2);
  readonly numberFormat = input('1.2-2');
  readonly drawingsVisible = input(true);
  readonly drawingCount = input(0);
  readonly instrument = input.required<InstrumentKey>();
  readonly instrumentLabel = input.required<string>();
  readonly brokerLinkId = input.required<string>();

  readonly resetView = output<void>();
  readonly addLine = output<number>();
  readonly measure = output<void>();
  readonly drawingsToggle = output<void>();
  readonly removeDrawings = output<void>();
  /** Something self-contained happened here; the chart should close the menu. */
  readonly dismiss = output<void>();
  readonly notice = output<string>();

  constructor() {
    // Opened by a right-click, so the keyboard has to be given somewhere to
    // start; the arrow keys below carry on from there.
    afterNextRender(() => this.host.nativeElement.querySelector('button')?.focus());
  }

  protected barText(bar: Candle): string {
    const format = (value: number) => value.toFixed(this.precision());
    return `${bar.openTime} O ${format(bar.open)} H ${format(bar.high)} L ${format(bar.low)} C ${format(bar.close)}`;
  }

  protected copy(text: string, label: string): void {
    this.dismiss.emit();
    const clipboard = navigator.clipboard;
    if (!clipboard) { this.notice.emit('Clipboard is not available in this browser context.'); return; }
    void clipboard.writeText(text).then(
      () => this.notice.emit(`${label} copied to clipboard.`),
      () => this.notice.emit('Clipboard is not available in this browser context.'),
    );
  }

  /** Arrow-key navigation: a plain list, not a MatMenu overlay, so it is ours to implement. */
  protected onKeydown(event: KeyboardEvent): void {
    const items = [...this.host.nativeElement.querySelectorAll<HTMLElement>('[role="menuitem"]')];
    const index = items.indexOf(document.activeElement as HTMLElement);
    if (event.key === 'ArrowDown' && items.length) { event.preventDefault(); items.at((index + 1) % items.length)?.focus(); }
    if (event.key === 'ArrowUp' && items.length) { event.preventDefault(); items.at((index - 1 + items.length) % items.length)?.focus(); }
    if (event.key === 'Home') { event.preventDefault(); items.at(0)?.focus(); }
    if (event.key === 'End') { event.preventDefault(); items.at(-1)?.focus(); }
  }
}

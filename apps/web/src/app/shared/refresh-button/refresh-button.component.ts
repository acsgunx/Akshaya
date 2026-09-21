import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/** One turn of Tailwind's `animate-spin`. The spin only ever stops on a multiple of this. */
const TURN_MS = 1000;

/**
 * The refresh control for a screen whose data is already on screen.
 *
 * Three things the plain icon button it replaces got wrong:
 *
 * - It was `disabled` while busy, which greys a Material icon button out to
 *   38%. A faint spinning glyph reads as "unavailable" (DESIGN.md, rule 4).
 *   This one stays full strength — brand-coloured while it works — and
 *   ignores clicks via `aria-disabled` instead.
 * - A fast refresh gave no feedback at all. The spin starts on the click
 *   itself, not when `busy` arrives: a 5ms response can come back before the
 *   parent re-renders, so `busy` may never be seen as true here. It then runs
 *   for at least one full turn, so every click is visibly answered.
 * - It stopped mid-turn, snapping the arrow back upright. Stopping is only
 *   checked at turn boundaries.
 *
 * Refreshes this button did not start (a stale screen refetching on entry)
 * still spin it, via the `busy` effect.
 */
@Component({
  selector: 'ak-refresh-button',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      mat-icon-button
      type="button"
      class="ak-focus-halo"
      [style.color]="spinning() ? 'var(--color-brand)' : null"
      [attr.aria-label]="(spinning() ? 'Refreshing ' : 'Refresh ') + what()"
      [attr.aria-disabled]="busy() || null"
      (click)="onClick()"
    >
      <mat-icon [class.animate-spin]="spinning()" aria-hidden="true">refresh</mat-icon>
    </button>
  `,
})
export class RefreshButtonComponent {
  /** The data behind this button is being fetched, whoever asked for it. */
  readonly busy = input.required<boolean>();
  /** What gets refreshed, for the accessible name: "portfolio" → "Refresh portfolio". */
  readonly what = input.required<string>();
  readonly refresh = output<void>();

  protected readonly spinning = signal(false);
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect(() => {
      if (this.busy()) {
        untracked(() => this.spin());
      }
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected onClick(): void {
    if (this.busy()) {
      return;
    }
    this.spin();
    this.refresh.emit();
  }

  private spin(): void {
    if (this.spinning()) {
      return;
    }
    this.spinning.set(true);
    const endOfTurn = (): void => {
      this.timer = setTimeout(() => (this.busy() ? endOfTurn() : this.spinning.set(false)), TURN_MS);
    };
    endOfTurn();
  }
}

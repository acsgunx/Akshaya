import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * One generic "nothing here" surface — no positions, no orders, no linked
 * brokers, no search results — so empty states look and read consistently
 * instead of each table inventing its own placeholder markup.
 */
@Component({
  selector: 'ak-empty-state',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="flex flex-col items-center justify-center text-center text-text-secondary"
      [class]="compact() ? 'gap-1.5 px-4 py-5' : 'gap-2 px-6 py-12'"
      role="status"
    >
      <mat-icon
        class="shrink-0 text-text-tertiary"
        [class]="compact() ? 'size-6 text-2xl' : 'size-8 text-[32px]'"
        aria-hidden="true"
        >{{ icon() }}</mat-icon
      >
      <p class="text-sm font-semibold text-text-primary">{{ title() }}</p>
      @if (description()) {
        <p class="max-w-[40ch] text-xs">{{ description() }}</p>
      }
      <ng-content></ng-content>
    </div>
  `,
})
export class EmptyStateComponent {
  readonly icon = input<string>('inbox');
  readonly title = input.required<string>();
  readonly description = input<string | undefined>(undefined);
  /** Tighter padding for use inside a dashboard card rather than a full page. */
  readonly compact = input(false);
}

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
  host: { '[class.ak-empty--compact]': 'compact()' },
  template: `
    <div class="ak-empty" role="status">
      <mat-icon aria-hidden="true">{{ icon() }}</mat-icon>
      <p class="ak-empty-title">{{ title() }}</p>
      @if (description()) {
        <p class="ak-empty-desc">{{ description() }}</p>
      }
      <ng-content></ng-content>
    </div>
  `,
  styles: `
    .ak-empty {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 8px;
      padding: 48px 24px;
      text-align: center;
      color: var(--ak-text-secondary);
    }
    :host(.ak-empty--compact) .ak-empty {
      padding: 20px 16px;
      gap: 6px;
    }
    :host(.ak-empty--compact) mat-icon {
      font-size: 24px;
      width: 24px;
      height: 24px;
    }
    mat-icon {
      font-size: 32px;
      width: 32px;
      height: 32px;
      color: var(--ak-text-tertiary);
    }
    .ak-empty-title {
      font-size: 14px;
      font-weight: 600;
      color: var(--ak-text-primary);
    }
    .ak-empty-desc {
      font-size: 12px;
      max-width: 40ch;
    }
  `,
})
export class EmptyStateComponent {
  readonly icon = input<string>('inbox');
  readonly title = input.required<string>();
  readonly description = input<string | undefined>(undefined);
  /** Tighter padding for use inside a dashboard card rather than a full page. */
  readonly compact = input(false);
}

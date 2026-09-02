import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { MoneyPipe } from '../../core/money.pipe';
import { QuantityPipe } from '../../core/quantity.pipe';
import { positionEffectLabel } from '../../core/labels';
import type { BlendedPosition } from '../../core/models';
import { DashboardStore } from '../dashboard/dashboard.store';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';

/**
 * Virtualised positions blotter. Shares `DashboardStore`'s snapshot (it's a
 * `providedIn: 'root'` SignalStore — see its own doc comment) rather than
 * issuing a second `GET /api/portfolio`; the dashboard and this table are
 * two views of the same one snapshot, not two independent fetches that could
 * disagree.
 *
 * Rows expand to show the per-broker legs of a blended position — the point
 * of `IsSplitAcrossBrokers` on the backend type.
 */
@Component({
  selector: 'ak-positions',
  standalone: true,
  imports: [ScrollingModule, MatIconModule, MatProgressSpinnerModule, MoneyPipe, QuantityPipe, EmptyStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './positions.component.html',
  styleUrl: './positions.component.scss',
})
export class PositionsComponent implements OnInit {
  protected readonly store = inject(DashboardStore);
  protected readonly positionEffectLabel = positionEffectLabel;

  private readonly expanded = signal<ReadonlySet<string>>(new Set());

  protected readonly positions = computed<readonly BlendedPosition[]>(() => this.store.snapshot()?.positions ?? []);

  ngOnInit(): void {
    if (!this.store.snapshot()) {
      this.store.refresh(undefined);
    }
  }

  protected isExpanded(groupKey: string): boolean {
    return this.expanded().has(groupKey);
  }

  protected toggle(groupKey: string): void {
    const next = new Set(this.expanded());
    if (next.has(groupKey)) {
      next.delete(groupKey);
    } else {
      next.add(groupKey);
    }
    this.expanded.set(next);
  }

  protected trackByGroupKey(_index: number, pos: BlendedPosition): string {
    return pos.groupKey;
  }
}

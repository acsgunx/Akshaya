import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ConnectorStore } from '../../core/connector.store';
import { MoneyPipe } from '../../core/money.pipe';
import { QuantityPipe } from '../../core/quantity.pipe';
import { ConnectionStatusComponent } from '../../shared/connection-status/connection-status.component';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { VenueClockComponent } from '../../shared/venue-clock/venue-clock.component';
import { DashboardStore } from './dashboard.store';

/**
 * Blended, multi-currency P&L; per-venue session strip; open-positions
 * summary; funds by currency. Every figure keeps its native currency next
 * to it (via `akMoney`, which formats per-currency — see `money.pipe.ts`)
 * because a converted total alone hides exactly the information a trader
 * needs when it disagrees with what their broker's own screen shows.
 */
@Component({
  selector: 'ak-dashboard',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, MoneyPipe, QuantityPipe, ConnectionStatusComponent, EmptyStateComponent, VenueClockComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.component.html',
})
export class DashboardComponent {
  protected readonly store = inject(DashboardStore);
  private readonly connectorStore = inject(ConnectorStore);

  /** Unique venues across every manifest this tenant can reach — drives the session strip, never a hardcoded exchange list. */
  protected readonly venues = computed(() => {
    const set = new Set<string>();
    for (const manifest of this.connectorStore.manifests()) {
      for (const venue of manifest.venues) {
        set.add(venue);
      }
    }
    return [...set].sort();
  });

  protected refresh(): void {
    this.store.refresh(this.store.snapshot()?.displayCurrency);
  }
}

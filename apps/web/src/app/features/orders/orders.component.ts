import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';

import { MoneyPipe } from '../../core/money.pipe';
import { QuantityPipe } from '../../core/quantity.pipe';
import { orderTypeLabel, sideLabel } from '../../core/labels';
import { isOrderStateTerminal, isOrderStateWorking, isOrderUnresolved } from '../../core/models';
import type { OrderRecord } from '../../core/models';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { OrdersStore } from './orders.store';

/**
 * Virtualised order blotter. Two statuses are shown side by side for every
 * row on purpose: the CANONICAL `state`/`status` (what the platform's state
 * machine believes) and the broker's own `statusMessage`, verbatim — per
 * `OrderDto`'s own doc comment, "show it unedited". A trader disputing a
 * fill needs the broker's exact words, not our paraphrase of them.
 */
@Component({
  selector: 'ak-orders',
  standalone: true,
  imports: [
    ScrollingModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MoneyPipe,
    QuantityPipe,
    EmptyStateComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orders.component.html',
  styleUrl: './orders.component.scss',
})
export class OrdersComponent {
  protected readonly store = inject(OrdersStore);
  protected readonly orderTypeLabel = orderTypeLabel;
  protected readonly sideLabel = sideLabel;
  protected readonly isOrderStateWorking = isOrderStateWorking;
  protected readonly isOrderStateTerminal = isOrderStateTerminal;
  protected readonly isOrderUnresolved = isOrderUnresolved;

  protected trackById(_index: number, order: OrderRecord): string {
    return order.id;
  }

  protected cancel(order: OrderRecord): void {
    this.store.cancel(order.id);
  }
}

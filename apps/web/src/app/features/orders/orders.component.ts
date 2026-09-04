import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { filter } from 'rxjs';

import { BrokerLinksStore } from '../../core/broker-links.store';
import { ConnectorStore } from '../../core/connector.store';
import { MoneyPipe } from '../../core/money.pipe';
import { QuantityPipe } from '../../core/quantity.pipe';
import { orderTypeLabel, sideLabel } from '../../core/labels';
import { canModifyField, isOrderStateTerminal, isOrderStateWorking, isOrderUnresolved } from '../../core/models';
import type { ConnectorManifest, ModifyOrderRequest, OrderRecord } from '../../core/models';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { ModifyOrderDialogComponent, ModifyOrderDialogData } from './modify-order-dialog.component';
import { OrdersStore } from './orders.store';

/**
 * The fields the modify dialog knows how to render. Used to decide whether
 * the amend action is worth offering at all: a broker whose `modifiable`
 * list contains only fields this dialog has no control for would otherwise
 * open an empty dialog.
 */
const ModifiableFields = ['quantity', 'limitPrice', 'triggerPrice', 'orderType', 'timeInForce', 'disclosedQuantity'];

/**
 * Virtualised order blotter. Two statuses are shown side by side for every
 * row on purpose: the CANONICAL `state`/`status` (what the platform's state
 * machine believes) and the broker's own `statusMessage`, verbatim — per
 * `OrderDto`'s own doc comment, "show it unedited". A trader disputing a
 * fill needs the broker's exact words, not our paraphrase of them.
 *
 * The per-row actions (amend, cancel) and the cancel-all sweep are all gated
 * on the MANIFEST of the connector each order was placed through, resolved
 * via that order's `brokerLinkId`. Two orders in the same blotter can offer
 * different actions because they went to different brokers, and that falls
 * out of the manifest read rather than out of any branch on connector id.
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
    RouterLink,
    MoneyPipe,
    QuantityPipe,
    EmptyStateComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orders.component.html',
  styleUrl: './orders.component.scss',
})
export class OrdersComponent implements OnInit {
  private readonly dialog = inject(MatDialog);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly connectorStore = inject(ConnectorStore);
  private readonly brokerLinksStore = inject(BrokerLinksStore);

  protected readonly store = inject(OrdersStore);
  protected readonly orderTypeLabel = orderTypeLabel;
  protected readonly sideLabel = sideLabel;
  protected readonly isOrderStateWorking = isOrderStateWorking;
  protected readonly isOrderStateTerminal = isOrderStateTerminal;
  protected readonly isOrderUnresolved = isOrderUnresolved;

  /** True when at least one order is still working, i.e. there is something to sweep. */
  protected readonly hasWorkingOrders = computed(() =>
    this.store.orders().some((order) => !isOrderStateTerminal(order.state) && !isOrderUnresolved(order)),
  );

  ngOnInit(): void {
    // Picks up orders from a broker linked while this screen was off-router;
    // a no-op otherwise. See `OrdersStore.ensureFresh`.
    this.store.ensureFresh();
  }

  protected trackById(_index: number, order: OrderRecord): string {
    return order.id;
  }

  protected cancel(order: OrderRecord): void {
    this.store.cancel(order.id);
  }

  /** Amend is offered only where the broker accepts at least one amendable field. */
  protected canModify(order: OrderRecord): boolean {
    const manifest = this.manifestFor(order);
    return !!manifest && ModifiableFields.some((field) => canModifyField(manifest, field));
  }

  protected openModify(order: OrderRecord): void {
    const manifest = this.manifestFor(order);
    if (!manifest) {
      return;
    }

    const ref = this.dialog.open<ModifyOrderDialogComponent, ModifyOrderDialogData, ModifyOrderRequest>(
      ModifyOrderDialogComponent,
      { data: { order, manifest }, width: '440px', autoFocus: 'first-tabbable', restoreFocus: true },
    );

    ref
      .afterClosed()
      .pipe(filter((request): request is ModifyOrderRequest => !!request))
      .subscribe((request) => this.store.modify({ orderId: order.id, request }));
  }

  /**
   * The panic button.
   *
   * Guarded by a TYPE-TO-CONFIRM dialog, not a plain "are you sure". This
   * sweeps every working order across every linked account at once, and per
   * the confirm dialog's own note a reflexive double-Enter must not be able
   * to fire it — the typing is the point.
   */
  protected cancelAll(): void {
    this.confirm
      .confirm({
        title: 'Cancel every working order?',
        message:
          'This cancels every working order across ALL of your linked accounts, not just the ones ' +
          'shown here. Orders that have already filled cannot be cancelled, and an order can still ' +
          'fill in the moment between this request and the broker acting on it.',
        confirmLabel: 'Cancel all orders',
        danger: true,
        typeToConfirm: 'CANCEL ALL',
      })
      .pipe(filter(Boolean))
      // No brokerLinkId: deliberately every link. The dialog above says so in
      // as many words, because `CancelAllRequest` warns that omitting it must
      // never be implicit.
      .subscribe(() => this.store.cancelAll({}));
  }

  /**
   * The manifest of the connector THIS order was placed through.
   *
   * Resolved through the order's broker link, never from the order's
   * `connectorId` directly — a user can hold two links against the same
   * connector, and the link is what identifies the account.
   */
  private manifestFor(order: OrderRecord): ConnectorManifest | undefined {
    const connectorId = this.brokerLinksStore.linkFor(order.brokerLinkId)?.connectorId ?? order.connectorId;
    return connectorId ? this.connectorStore.manifestFor(connectorId) : undefined;
  }
}

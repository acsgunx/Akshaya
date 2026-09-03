import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { filter } from 'rxjs';

import { ApiService } from '../../core/api.service';
import { BrokerLinksStore } from '../../core/broker-links.store';
import { ConnectorStore } from '../../core/connector.store';
import { MoneyPipe } from '../../core/money.pipe';
import { QuantityPipe } from '../../core/quantity.pipe';
import { positionEffectLabel } from '../../core/labels';
import type { BlendedPosition, ConnectorManifest, ConvertPositionRequest, BrokerPositionLeg } from '../../core/models';
import { DashboardStore } from '../dashboard/dashboard.store';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { ConvertPositionDialogComponent, ConvertPositionDialogData } from './convert-position-dialog.component';

/**
 * Virtualised positions blotter. Shares `DashboardStore`'s snapshot (it's a
 * `providedIn: 'root'` SignalStore — see its own doc comment) rather than
 * issuing a second `GET /api/portfolio`; the dashboard and this table are
 * two views of the same one snapshot, not two independent fetches that could
 * disagree.
 *
 * Rows expand to show the per-broker legs of a blended position — the point
 * of `IsSplitAcrossBrokers` on the backend type.
 *
 * ACTIONS LIVE ON THE LEG, NOT THE ROW, and that is deliberate. A blended
 * position is an analytical sum across brokers; you cannot square off "the
 * sum", because there is no single account holding it. Every action here
 * therefore hangs off a leg, which names exactly one broker link and one
 * product — the two things an order or a conversion needs.
 */
@Component({
  selector: 'ak-positions',
  standalone: true,
  imports: [
    ScrollingModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MoneyPipe,
    QuantityPipe,
    EmptyStateComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './positions.component.html',
  styleUrl: './positions.component.scss',
})
export class PositionsComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly api = inject(ApiService);
  private readonly connectorStore = inject(ConnectorStore);
  private readonly brokerLinksStore = inject(BrokerLinksStore);

  protected readonly store = inject(DashboardStore);
  protected readonly positionEffectLabel = positionEffectLabel;

  private readonly expanded = signal<ReadonlySet<string>>(new Set());

  protected readonly positions = computed<readonly BlendedPosition[]>(() => this.store.snapshot()?.positions ?? []);

  /** Set while a conversion is in flight, so the leg's buttons disable rather than double-fire. */
  protected readonly converting = signal<ReadonlySet<string>>(new Set());

  protected readonly conversionError = signal<string | undefined>(undefined);

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

  /**
   * Opens the order ticket already pointed the other way.
   *
   * It does NOT place the order. A square-off is a real trade with real
   * costs and a real price, and the ticket's review-then-confirm flow exists
   * precisely so that no trade happens on a single click — see the "no
   * optimistic UI" note in `order-ticket.store.ts`. This fills the form in;
   * the human still confirms it.
   *
   * The product is carried across too, because exiting an intraday position
   * with a delivery order does not close it — it opens a second, opposite
   * delivery position and leaves the intraday one to square off on its own.
   */
  protected exit(pos: BlendedPosition, leg: BrokerPositionLeg): void {
    const quantity = Math.abs(Number(leg.netQuantity));
    if (!quantity) {
      return;
    }

    void this.router.navigate(['/trade', leg.brokerLinkId, pos.instrument], {
      queryParams: {
        side: Number(leg.netQuantity) > 0 ? 'sell' : 'buy',
        quantity,
        product: leg.positionEffect,
      },
    });
  }

  /** Opens the ticket to add to a position, in the same direction and product. */
  protected addTo(pos: BlendedPosition, leg: BrokerPositionLeg): void {
    void this.router.navigate(['/trade', leg.brokerLinkId, pos.instrument], {
      queryParams: {
        side: Number(leg.netQuantity) >= 0 ? 'buy' : 'sell',
        product: leg.positionEffect,
      },
    });
  }

  /** Straight off the manifest — hidden entirely where the broker cannot convert. */
  protected canConvert(leg: BrokerPositionLeg): boolean {
    return this.manifestFor(leg)?.orders.positionConversion === true;
  }

  protected isConverting(leg: BrokerPositionLeg): boolean {
    return this.converting().has(leg.brokerLinkId);
  }

  protected openConvert(pos: BlendedPosition, leg: BrokerPositionLeg): void {
    const manifest = this.manifestFor(leg);
    if (!manifest) {
      return;
    }

    const ref = this.dialog.open<
      ConvertPositionDialogComponent,
      ConvertPositionDialogData,
      ConvertPositionRequest
    >(ConvertPositionDialogComponent, {
      data: {
        brokerLinkId: leg.brokerLinkId,
        instrument: pos.instrument,
        netQuantity: leg.netQuantity,
        currentProduct: leg.positionEffect,
        manifest,
      },
      width: '440px',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });

    ref
      .afterClosed()
      .pipe(filter((request): request is ConvertPositionRequest => !!request))
      .subscribe((request) => this.convert(request));
  }

  private convert(request: ConvertPositionRequest): void {
    this.conversionError.set(undefined);
    this.converting.update((set) => new Set([...set, request.brokerLinkId]));

    this.api.convertPosition(request).subscribe({
      next: () => {
        this.clearConverting(request.brokerLinkId);
        // The broker acknowledges a conversion with an empty body, so the
        // only way to know it landed is to re-read the positions.
        this.store.refresh(undefined);
      },
      error: (err: unknown) => {
        this.clearConverting(request.brokerLinkId);
        this.conversionError.set(
          err instanceof Error ? err.message : 'The broker refused the conversion. The position is unchanged.',
        );
      },
    });
  }

  private clearConverting(brokerLinkId: string): void {
    this.converting.update((set) => {
      const next = new Set(set);
      next.delete(brokerLinkId);
      return next;
    });
  }

  /** The manifest of the connector behind THIS leg, resolved through its link. */
  private manifestFor(leg: BrokerPositionLeg): ConnectorManifest | undefined {
    const connectorId = this.brokerLinksStore.linkFor(leg.brokerLinkId)?.connectorId;
    return connectorId ? this.connectorStore.manifestFor(connectorId) : undefined;
  }
}

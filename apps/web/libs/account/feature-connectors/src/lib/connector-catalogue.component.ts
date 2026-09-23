import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';

import { ApiService, BrokerLinksStore, ConnectorStore } from '@akshaya/shared/data-access';
import type { BrokerLink } from '@akshaya/shared/models';
import {
  ACCOUNT_TABS,
  ConfirmDialogService,
  EmptyStateComponent,
  LoadingStateComponent,
  SectionTabsComponent,
} from '@akshaya/shared/ui';
import { ClockService } from '@akshaya/shared/util';

/** How close to session expiry the countdown switches on — same window `ak-connection-status` uses. */
const SESSION_WARN_WITHIN_MS = 15 * 60_000;

/**
 * Past this, an expiry is a sentinel, not information — a connector that hands back
 * year-9999 ("session effectively never ends") must not render "Session until Jan 1"
 * and pretend that date means something.
 */
const SESSION_SENTINEL_MS = 366 * 24 * 60 * 60 * 1000;

type LinkState = 'connected' | 'expired' | 'inactive';

interface LinkStatus {
  readonly state: LinkState;
  /** The one-word status beside the account name. */
  readonly label: string;
  readonly labelClass: string;
  readonly dotClass: string;
  /** The secondary line under the name: when the session dies, or what to do once it has. */
  readonly detail: string;
  /** True turns the detail line warning-amber — an expiry that is minutes away, not hours. */
  readonly urgent: boolean;
}

/**
 * Lists every broker the platform knows about, purely from their manifests.
 * Adding a connector to the backend makes it appear here with zero frontend
 * changes — that is the acceptance test for "no broker-specific code" as
 * much as the order ticket is.
 *
 * Each card ALSO renders the accounts the user has already linked to that
 * broker (`BrokerLinksStore`), because "did the link work, and is the session
 * still alive" is the question this page exists to answer after the wizard
 * returns. A `BrokerLink` — not the manifest — carries that state: one card
 * can hold several links for the same connector, each alive or dead on its
 * own. `linkStatus` maps `isActive`/`hasSession`/`sessionExpiresAt` onto the
 * same three tokens `ak-connection-status` uses — success / warning /
 * tertiary — deliberately not a binary "linked" dot: a session that expires
 * at venue midnight is normal daily operation for some brokers, and showing
 * it as an error would train the user to ignore the badge.
 */
@Component({
  selector: 'ak-connector-catalogue',
  standalone: true,
  imports: [
    RouterLink,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    EmptyStateComponent,
    LoadingStateComponent,
    SectionTabsComponent,
  ],
  providers: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './connector-catalogue.component.html',
})
export class ConnectorCatalogueComponent implements OnInit {
  protected readonly store = inject(ConnectorStore);
  protected readonly links = inject(BrokerLinksStore);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly clock = inject(ClockService);
  private readonly date = inject(DatePipe);
  protected readonly accountTabs = ACCOUNT_TABS;

  /**
   * Links grouped by connector id, live sessions before dead ones and newest
   * first within that — the account the user can trade on right now should
   * never sort below the expired one it replaced.
   */
  protected readonly linksByConnector = computed(() => {
    const map = new Map<string, BrokerLink[]>();
    for (const link of this.links.links()) {
      const list = map.get(link.connectorId) ?? [];
      list.push(link);
      map.set(link.connectorId, list);
    }
    for (const list of map.values()) {
      list.sort(
        (a, b) =>
          Number(b.isActive && b.hasSession) - Number(a.isActive && a.hasSession) ||
          b.createdAt.localeCompare(a.createdAt),
      );
    }
    return map;
  });

  /**
   * The links answer has not arrived yet. `signature` is only set by a load
   * that succeeded; a failed load drops through to "no links shown", and the
   * error toast from the interceptor already said why. See DESIGN.md — never
   * declare "nothing linked" for a question nobody has answered.
   */
  protected readonly linksPending = computed(
    () => this.links.signature() === undefined && this.links.error() === undefined,
  );

  ngOnInit(): void {
    if (this.store.isEmpty()) {
      this.store.load();
    }
    // The shell loads links on sign-in, but a cold start can land on this page
    // before that finishes — ask only when nobody has, so revisits stay free.
    if (this.links.signature() === undefined && !this.links.loading()) {
      this.links.load();
    }
  }

  protected linksFor(connectorId: string): readonly BrokerLink[] {
    return this.linksByConnector().get(connectorId) ?? [];
  }

  /**
   * One link's status, resolved against the shared clock so the "expires in
   * m:ss" line actually ticks — `Date.now()` inside a computed would freeze
   * at whatever value the last render saw.
   */
  protected linkStatus(link: BrokerLink): LinkStatus {
    if (!link.isActive) {
      return {
        state: 'inactive',
        label: 'Inactive',
        labelClass: 'text-text-tertiary',
        dotClass: 'bg-text-tertiary',
        detail: 'Not monitored or tradable. Sign in again to revive it, or remove it.',
        urgent: false,
      };
    }

    const expiresAt = link.sessionExpiresAt ? new Date(link.sessionExpiresAt).getTime() : undefined;
    const msLeft = expiresAt === undefined ? undefined : expiresAt - this.clock.now();

    if (!link.hasSession || (msLeft !== undefined && msLeft <= 0)) {
      return {
        state: 'expired',
        label: 'Session expired',
        labelClass: 'text-warning',
        dotClass: 'bg-warning',
        detail: 'Sign in again to trade and stream on this account.',
        urgent: false,
      };
    }

    if (msLeft !== undefined && msLeft <= SESSION_WARN_WITHIN_MS) {
      const mins = Math.floor(msLeft / 60_000);
      const secs = Math.floor((msLeft % 60_000) / 1000);
      return {
        state: 'connected',
        label: 'Connected',
        labelClass: 'text-success',
        dotClass: 'bg-success ring-2 ring-success/25',
        detail: `Session expires in ${mins}:${secs.toString().padStart(2, '0')}`,
        urgent: true,
      };
    }

    return {
      state: 'connected',
      label: 'Connected',
      labelClass: 'text-success',
      dotClass: 'bg-success ring-2 ring-success/25',
      detail:
        (expiresAt !== undefined && msLeft! <= SESSION_SENTINEL_MS
          ? `Session until ${this.date.transform(link.sessionExpiresAt!, 'MMM d, h:mm a')}`
          : undefined) ??
        (link.lastAuthenticatedAt
          ? `Signed in ${this.date.transform(link.lastAuthenticatedAt, 'MMM d, h:mm a')}`
          : 'Ready to trade and stream.'),
      urgent: false,
    };
  }

  protected async unlink(link: BrokerLink, connectorName: string): Promise<void> {
    const name = link.nickname ?? connectorName;
    const confirmed = await firstValueFrom(
      this.confirm.confirm({
        title: `Unlink ${name}?`,
        message:
          'This signs the account out at the broker and removes it here. Nothing is placed or cancelled — ' +
          'orders already at the broker are untouched. You can link the account again at any time.',
        confirmLabel: 'Unlink',
        danger: true,
      }),
    );
    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.unlink(link.id));
    } catch {
      // The error interceptor has already toasted what the API said.
      return;
    }
    this.links.load();
  }
}

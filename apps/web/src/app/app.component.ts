import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { AuthStore } from './core/auth.store';
import { BrokerLinksStore } from './core/broker-links.store';
import { ConnectorStore } from './core/connector.store';
import { AppearanceMenuComponent } from './shared/appearance/appearance-menu.component';
import { KillSwitchComponent } from './shared/kill-switch/kill-switch.component';

/**
 * The app shell: persistent nav plus the one always-visible kill switch (see
 * its own doc comment for why it lives here and not inside a feature).
 * Connector manifests are loaded ONCE, here, at startup — every feature
 * downstream reads them from `ConnectorStore` and none of them re-fetches.
 */
@Component({
  selector: 'ak-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatIconModule, AppearanceMenuComponent, KillSwitchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ak-shell">
      <!--
        The chrome is for signed-in users. On the sign-in and sign-up screens
        there is no nav to offer and no kill switch to press, and rendering a
        disabled shell around them just makes the app look broken.
      -->
      @if (auth.isAuthenticated()) {
        <header class="ak-topbar">
          <span class="ak-brand">Akshaya</span>
          <nav class="ak-nav" aria-label="Primary">
            @for (item of navItems; track item.path) {
              <a class="ak-navlink" [routerLink]="item.path" routerLinkActive="active">{{ item.label }}</a>
            }
          </nav>
          <div class="ak-topbar-actions">
            <ak-kill-switch />
            <!--
              Deferred: the appearance menu is the only thing in the shell that needs
              Material's menu and checkbox, and pulling those into the initial bundle to
              render one icon button costs ~100kB on first paint. Idle-loading keeps it off
              the critical path, so it is present long before anyone reaches for it and
              no first click is wasted merely triggering the download.
            -->
            @defer (on idle) {
              <ak-appearance-menu />
            } @placeholder {
              <span class="ak-appearance-slot" aria-hidden="true"></span>
            }
            <a
              routerLink="/account"
              routerLinkActive="active"
              class="ak-navlink ak-account-link"
              [attr.aria-label]="'Account: ' + auth.displayName()"
            >
              <mat-icon class="ak-i-sm" aria-hidden="true">account_circle</mat-icon>
              <span class="ak-truncate">{{ auth.displayName() }}</span>
            </a>
          </div>
        </header>
      }
      <main class="ak-content" [class.ak-content--bare]="!auth.isAuthenticated()">
        <router-outlet />
      </main>
    </div>
  `,
  styles: `
    :host {
      display: block;
      min-height: 100vh;
      background: var(--ak-bg);
    }
    .ak-shell {
      display: flex;
      flex-direction: column;
      min-height: 100vh;
    }
    .ak-topbar {
      position: sticky;
      top: 0;
      z-index: 10;
      display: flex;
      align-items: center;
      gap: 24px;
      height: 56px;
      padding: 0 20px;
      background: var(--ak-surface-1);
      border-bottom: 1px solid var(--ak-border);
    }
    .ak-brand {
      font-size: 16px;
      font-weight: 700;
    }
    .ak-nav {
      display: flex;
      flex: 1;
      gap: 4px;
    }
    /* One rule for the primary nav and the account link — they are the same control. */
    .ak-navlink {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 8px 12px;
      border-radius: var(--ak-radius-sm);
      color: var(--ak-text-secondary);
      font-size: 13px;
      font-weight: 500;
      text-decoration: none;
    }
    .ak-navlink:hover {
      background: var(--ak-surface-2);
      color: var(--ak-text-primary);
    }
    .ak-navlink.active {
      background: var(--ak-surface-3);
      color: var(--ak-text-primary);
    }
    .ak-account-link {
      max-width: 200px;
      padding: 6px 10px;
    }
    /* Reserves the trigger's footprint so the topbar does not shift when it loads. */
    .ak-appearance-slot {
      display: inline-block;
      width: 40px;
    }
    .ak-topbar-actions {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .ak-content {
      flex: 1;
      width: 100%;
      max-width: 1440px;
      margin: 0 auto;
      padding: 20px;
    }
    /* The auth screens centre themselves and own their whole viewport. */
    .ak-content--bare {
      max-width: none;
      padding: 0;
    }
  `,
})
export class AppComponent implements OnInit {
  /** The primary nav, as data — adding a screen is one entry, not a hand-copied anchor. */
  protected readonly navItems = [
    { path: '/dashboard', label: 'Dashboard' },
    { path: '/watchlist', label: 'Watchlist' },
    { path: '/positions', label: 'Positions' },
    { path: '/holdings', label: 'Holdings' },
    { path: '/orders', label: 'Orders' },
    { path: '/connectors', label: 'Brokers' },
  ] as const;

  private readonly connectorStore = inject(ConnectorStore);
  private readonly brokerLinksStore = inject(BrokerLinksStore);
  protected readonly auth = inject(AuthStore);

  async ngOnInit(): Promise<void> {
    // The session has to be known before anything else is fetched: every store
    // below calls an endpoint that 401s for an anonymous caller, and firing
    // them first just fills the console with errors on the sign-in screen.
    await this.auth.restore();

    if (this.auth.isAuthenticated()) {
      this.connectorStore.load();
      this.brokerLinksStore.load();
    }
  }
}

import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { BrokerLinksStore } from './core/broker-links.store';
import { ConnectorStore } from './core/connector.store';
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
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatIconModule, KillSwitchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ak-shell">
      <header class="ak-topbar">
        <span class="ak-brand">Akshaya</span>
        <nav class="ak-nav" aria-label="Primary">
          <a routerLink="/dashboard" routerLinkActive="active">Dashboard</a>
          <a routerLink="/watchlist" routerLinkActive="active">Watchlist</a>
          <a routerLink="/positions" routerLinkActive="active">Positions</a>
          <a routerLink="/orders" routerLinkActive="active">Orders</a>
          <a routerLink="/connectors" routerLinkActive="active">Brokers</a>
        </nav>
        <div class="ak-topbar-actions">
          <ak-kill-switch />
        </div>
      </header>
      <main class="ak-content">
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
      display: flex;
      align-items: center;
      gap: 24px;
      padding: 0 20px;
      height: 56px;
      background: var(--ak-surface-1);
      border-bottom: 1px solid var(--ak-border);
      position: sticky;
      top: 0;
      z-index: 10;
    }
    .ak-brand {
      font-weight: 700;
      font-size: 16px;
      letter-spacing: 0.01em;
      color: var(--ak-text-primary);
    }
    .ak-nav {
      display: flex;
      gap: 4px;
      flex: 1;
    }
    .ak-nav a {
      padding: 8px 12px;
      border-radius: var(--ak-radius-sm);
      color: var(--ak-text-secondary);
      text-decoration: none;
      font-size: 13px;
      font-weight: 500;
    }
    .ak-nav a:hover {
      background: var(--ak-surface-2);
      color: var(--ak-text-primary);
    }
    .ak-nav a.active {
      background: var(--ak-surface-3);
      color: var(--ak-text-primary);
    }
    .ak-topbar-actions {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .ak-content {
      flex: 1;
      padding: 20px;
      max-width: 1440px;
      width: 100%;
      margin: 0 auto;
    }
  `,
})
export class AppComponent implements OnInit {
  private readonly connectorStore = inject(ConnectorStore);
  private readonly brokerLinksStore = inject(BrokerLinksStore);

  ngOnInit(): void {
    this.connectorStore.load();
    this.brokerLinksStore.load();
  }
}

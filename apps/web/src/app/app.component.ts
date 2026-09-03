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
    <div class="flex min-h-screen flex-col">
      <!--
        The chrome is for signed-in users. On the sign-in and sign-up screens
        there is no nav to offer and no kill switch to press, and rendering a
        disabled shell around them just makes the app look broken.
      -->
      @if (auth.isAuthenticated()) {
        <header
          class="sticky top-0 z-10 flex h-14 items-center gap-6 border-b border-border bg-surface-1 px-5"
        >
          <span class="text-base font-bold">Akshaya</span>
          <nav class="flex flex-1 gap-1" aria-label="Primary">
            @for (item of navItems; track item.path) {
              <a class="ak-navlink" [routerLink]="item.path" routerLinkActive="active">{{ item.label }}</a>
            }
          </nav>
          <div class="flex items-center gap-3">
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
              <!-- Reserves the trigger's footprint so the topbar does not shift when it loads. -->
              <span class="inline-block w-10" aria-hidden="true"></span>
            }
            <a
              routerLink="/account"
              routerLinkActive="active"
              class="ak-navlink max-w-50 px-2.5! py-1.5!"
              [attr.aria-label]="'Account: ' + auth.displayName()"
            >
              <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">account_circle</mat-icon>
              <span class="min-w-0 truncate">{{ auth.displayName() }}</span>
            </a>
          </div>
        </header>
      }
      <main class="ak-content w-full flex-1" [class.ak-content--bare]="!auth.isAuthenticated()">
        <router-outlet />
      </main>
    </div>
  `,
  styles: `
    /*
      The one place in the shell that is a class rather than utilities in the
      template: \`routerLinkActive\` applies a bare \`active\` class name, and a
      \`[class]\` binding on the same element fights it for ownership of
      classList. \`@apply\` keeps the definition in Tailwind's vocabulary and,
      more importantly, keeps ONE definition shared by the primary nav and the
      account link, which are the same control.
    */
    @reference '../styles/tailwind.css';

    .ak-navlink {
      @apply inline-flex items-center gap-1.5 rounded-sm px-3 py-2 text-[13px] font-medium
        text-text-secondary no-underline hover:bg-surface-2 hover:text-text-primary;
    }

    .ak-navlink.active {
      @apply bg-surface-3 text-text-primary;
    }

    .ak-content {
      @apply mx-auto max-w-[1440px] p-5;
    }

    /* The auth screens centre themselves and own their whole viewport. */
    .ak-content--bare {
      @apply max-w-none p-0;
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

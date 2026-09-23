import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  OnInit,
  afterNextRender,
  effect,
  inject,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  Event,
  NavigationCancel,
  NavigationCancellationCode,
  NavigationEnd,
  NavigationError,
  NavigationSkipped,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { catchError, filter, map, of, take } from 'rxjs';

import { ActivityService, ApiService, AuthStore, BrokerLinksStore, ConnectorStore } from '@akshaya/shared/data-access';
import { formatBuildInfo, type BuildInfo } from '@akshaya/shared/models';
import { BrandMarkComponent } from '@akshaya/shared/ui';
import { dismissBootSplash } from './shell/boot-splash';
import { ActivityBarComponent } from './shell/activity-bar/activity-bar.component';
import { AppearanceMenuComponent } from './shell/appearance/appearance-menu.component';
import { KillSwitchComponent } from './shell/kill-switch/kill-switch.component';

/** One bottom-bar destination on the compact layout. */
interface TabItem {
  readonly path: string;
  readonly label: string;
  readonly icon: string;
  /** Every route this tab owns — it lights up on any of them. */
  readonly owns: readonly string[];
}

/**
 * The app shell: persistent nav plus the one always-visible kill switch (see
 * its own doc comment for why it lives here and not inside a feature).
 * Connector manifests are loaded ONCE, here, at startup — every feature
 * downstream reads them from `ConnectorStore` and none of them re-fetches.
 *
 * TWO NAVS, ONE AT A TIME. At `lg` and up the six destinations sit in the top
 * bar. Below it they cannot — the top bar needs ~960px, so on a phone the
 * last links, the account link and (worst of all) the kill switch scrolled off
 * the right edge. Compact screens get a bottom tab bar instead: in the thumb's
 * reach, and with the kill switch left alone in the top bar where it is
 * always visible. The switch is pure CSS (`lg:` / `max-lg:`), matching
 * `COMPACT_QUERY` in `layout.service.ts`, so there is no frame where both or
 * neither render.
 *
 * WAITING, AT THREE SCOPES. Until the first screen has painted, the static
 * splash in `index.html` covers everything (see `src/app/shell/boot-splash.ts`). After
 * that, `<ak-activity-bar>` on the header's bottom edge covers any navigation
 * or request in flight, and each screen shows its own data loading in place.
 * DESIGN.md, "Loading and waiting", has the rules.
 */
@Component({
  selector: 'ak-root',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatIconModule,
    ActivityBarComponent,
    AppearanceMenuComponent,
    BrandMarkComponent,
    KillSwitchComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex min-h-dvh flex-col">
      <!--
        The chrome is for signed-in users. On the sign-in and sign-up screens
        there is no nav to offer and no kill switch to press, and rendering a
        disabled shell around them just makes the app look broken.
      -->
      @if (auth.isAuthenticated()) {
        <header class="px-safe sticky top-0 z-20 border-b border-border bg-surface-1 pt-[env(safe-area-inset-top)] lg:px-5">
          <div class="flex h-14 items-center gap-3 lg:gap-6">
            <!--
              The wordmark is what gives way on a very narrow phone (320px, kill
              switch engaged): the kill switch and its state never may.
            -->
            <ak-brand-mark class="size-7" />
            <span class="min-w-0 truncate text-base font-bold">Akshaya</span>
            @if (buildInfo(); as info) {
              <!-- Compact has no room for it; the account screen shows the same label there. -->
              <span
                class="shrink-0 text-[11px] text-text-tertiary max-lg:hidden"
                [title]="info.commit ? 'commit ' + info.commit : null"
              >{{ formatBuildInfo(info) }}</span>
            }
            <nav class="hidden flex-1 gap-1 lg:flex" aria-label="Primary">
              @for (item of navItems; track item.path) {
                <a class="ak-navlink" [routerLink]="item.path" routerLinkActive="active">{{ item.label }}</a>
              }
            </nav>
            <div class="ml-auto flex items-center gap-1.5 lg:gap-3">
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
              <!-- On compact screens the account is a bottom-bar tab instead. -->
              <a
                routerLink="/account"
                routerLinkActive="active"
                class="ak-navlink max-w-50 px-2.5! py-1.5! max-lg:hidden!"
                [attr.aria-label]="'Account: ' + auth.displayName()"
              >
                <mat-icon class="size-[18px] shrink-0 text-[18px]" aria-hidden="true">account_circle</mat-icon>
                <span class="min-w-0 truncate">{{ auth.displayName() }}</span>
              </a>
            </div>
          </div>
          <!-- Over the header's bottom border: right above the content that is loading. -->
          <ak-activity-bar class="absolute inset-x-0 -bottom-px" />
        </header>
      } @else {
        <!-- No header on the sign-in screens, so the bar pins to the top of the viewport. -->
        <ak-activity-bar class="fixed inset-x-0 top-[env(safe-area-inset-top)] z-30" />
        <!-- "Did the deploy land" is checkable before signing in too. -->
        @if (buildInfo(); as info) {
          <span class="fixed right-4 bottom-3 z-10 text-[11px] text-text-tertiary">{{ formatBuildInfo(info) }}</span>
        }
      }

      <main
        class="ak-content w-full flex-1"
        [class.ak-content--bare]="!auth.isAuthenticated()"
        [attr.aria-busy]="activity.navigating() || null"
      >
        <router-outlet />
      </main>

      @if (auth.isAuthenticated()) {
        <!--
          Compact-only bottom tab bar. Five destinations is the most a phone
          fits at a legible label size, so related screens share a tab and
          switch between themselves with <ak-section-tabs> at the top of each
          (Holdings | Positions, Orders | Fills, Account | Brokers).
        -->
        <nav
          class="ak-bottom-nav fixed inset-x-0 bottom-0 z-20 border-t border-border bg-surface-1 pr-[env(safe-area-inset-right)]
                 pb-[env(safe-area-inset-bottom)] pl-[env(safe-area-inset-left)] lg:hidden"
          aria-label="Primary"
        >
          <ul class="grid h-16 grid-cols-5" role="list">
            @for (tab of tabItems; track tab.path) {
              <li class="flex">
                <a
                  class="ak-tab"
                  [routerLink]="tab.path"
                  [class.active]="isCurrent(tab)"
                  [attr.aria-current]="isCurrent(tab) ? 'page' : null"
                >
                  <span class="ak-tab-icon"><mat-icon aria-hidden="true">{{ tab.icon }}</mat-icon></span>
                  <span class="text-[11px]/none font-medium">{{ tab.label }}</span>
                </a>
              </li>
            }
          </ul>
        </nav>
      }
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

    /*
      Bottom-bar tab. The active state is a pill behind the icon (Material 3's
      navigation-bar indicator) AND a label colour change — never the colour
      alone, per DESIGN.md. Full cell height so the whole column is the tap
      target, not just the glyph.
    */
    .ak-tab {
      @apply flex flex-1 flex-col items-center justify-center gap-1 text-text-secondary no-underline;
    }

    .ak-tab-icon {
      @apply flex h-7 w-14 items-center justify-center rounded-full;

      /* Plain, not \`@apply transition-colors\`, which registers ~1kB of @property rules for one fade. */
      transition: background-color var(--duration-ak) var(--ease-ak);
    }

    .ak-tab.active {
      @apply text-text-primary;
    }

    .ak-tab.active .ak-tab-icon {
      @apply bg-surface-3;
    }

    /*
      \`overflow-x: clip\` on compact is a safety net, not a layout tool. On a phone,
      anything wider than the screen widens the LAYOUT viewport, and the fixed
      bottom bar is positioned against that — it slides off the bottom of the
      screen and takes all navigation with it. \`clip\` rather than \`hidden\`: it
      creates no scroll container, so nothing sticky inside breaks.
    */
    .ak-content {
      @apply px-safe mx-auto max-w-[1440px] pt-4 pb-[calc(--spacing(20)+env(safe-area-inset-bottom))]
        max-lg:overflow-x-clip lg:p-5;
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

  /**
   * The same destinations, folded into five tabs for the compact layout.
   * Each tab's `owns` must match the `SectionTab` sets in
   * `section-tabs.component.ts`, or a screen will be reachable but no tab
   * will light up while you are on it.
   */
  protected readonly tabItems: readonly TabItem[] = [
    { path: '/dashboard', label: 'Dashboard', icon: 'space_dashboard', owns: ['/dashboard'] },
    { path: '/watchlist', label: 'Watchlist', icon: 'visibility', owns: ['/watchlist'] },
    { path: '/holdings', label: 'Portfolio', icon: 'account_balance_wallet', owns: ['/holdings', '/positions'] },
    { path: '/orders', label: 'Orders', icon: 'receipt_long', owns: ['/orders', '/fills'] },
    { path: '/account', label: 'Account', icon: 'account_circle', owns: ['/account', '/connectors'] },
  ];

  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly connectorStore = inject(ConnectorStore);
  private readonly brokerLinksStore = inject(BrokerLinksStore);
  protected readonly auth = inject(AuthStore);
  protected readonly activity = inject(ActivityService);

  /**
   * The deployed build's identity, fetched even before sign-in — the endpoint
   * is anonymous, so the sign-in screen can answer "did that deploy land" too.
   * A failure just means no label; nothing on screen could act on it anyway.
   */
  protected readonly buildInfo = toSignal(
    inject(ApiService).getBuildInfo().pipe(catchError(() => of<BuildInfo | undefined>(undefined))),
  );
  protected readonly formatBuildInfo = formatBuildInfo;

  constructor() {
    // The splash stays up until the FIRST navigation has settled and painted:
    // the guard behind it awaits the session, so by then the shell knows
    // whether to draw the nav, and the screen underneath is already drawn.
    // Subscribed here, in the constructor, because the router starts that
    // navigation straight after this component is created and its events
    // are not replayed to late subscribers.
    this.router.events
      .pipe(filter(settlesFirstScreen), take(1), takeUntilDestroyed())
      .subscribe(() => afterNextRender(dismissBootSplash, { injector: this.injector }));

    // Loaded whenever someone BECOMES signed in, not once at startup: signing
    // in on the sign-in screen does not reload the page, and a one-shot load
    // in `ngOnInit` (which is what this was) left every screen that reads
    // these two stores — the order ticket, the chart, the watchlist — waiting
    // for data nobody had asked for. Not before sign-in, either: both
    // endpoints 401 for an anonymous caller. Sign-out reloads the page, so
    // there is no signed-out transition to handle here.
    effect(() => {
      if (this.auth.isAuthenticated()) {
        untracked(() => {
          this.connectorStore.load();
          this.brokerLinksStore.load();
        });
      }
    });
  }

  /** The current path, without query or fragment — what the bottom bar highlights against. */
  private readonly path = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
      map(stripQuery),
    ),
    { initialValue: stripQuery(this.router.url) },
  );

  /**
   * A tab is current on any route it owns, including nested ones
   * (`/connectors/paper/link` still lights Account). `routerLinkActive` cannot
   * express this: it only matches the link's OWN url, and the Portfolio tab
   * has to stay lit on Positions, which it does not link to.
   */
  protected isCurrent(tab: TabItem): boolean {
    const path = this.path();
    return tab.owns.some((owned) => path === owned || path.startsWith(`${owned}/`));
  }

  async ngOnInit(): Promise<void> {
    // The session has to be known before anything else is fetched — see the
    // effect in the constructor, which this settles.
    await this.auth.restore();
  }
}

function stripQuery(url: string): string {
  return url.split(/[?#]/, 1)[0] ?? url;
}

/**
 * True for the router event after which the screen will not change on its
 * own. A redirect (signed out, so off to /sign-in) or a superseded navigation
 * cancels one navigation only to start another, so neither counts.
 *
 * An error counts on purpose. A failed lazy chunk leaves the page blank
 * either way, and a blank page with the nav and the kill switch on it beats a
 * splash that never lifts.
 */
function settlesFirstScreen(event: Event): boolean {
  if (event instanceof NavigationCancel) {
    return (
      event.code !== NavigationCancellationCode.Redirect &&
      event.code !== NavigationCancellationCode.SupersededByNewNavigation
    );
  }
  return event instanceof NavigationEnd || event instanceof NavigationError || event instanceof NavigationSkipped;
}

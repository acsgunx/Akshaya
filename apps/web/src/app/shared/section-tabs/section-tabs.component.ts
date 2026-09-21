import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

export interface SectionTab {
  readonly path: string;
  readonly label: string;
}

/** Holdings and Positions — the two halves of the Portfolio tab. */
export const PORTFOLIO_TABS: readonly SectionTab[] = [
  { path: '/holdings', label: 'Holdings' },
  { path: '/positions', label: 'Positions' },
];

/** Orders and the fills they produced — the two halves of the Orders tab. */
export const ORDERS_TABS: readonly SectionTab[] = [
  { path: '/orders', label: 'Orders' },
  { path: '/fills', label: 'Fills' },
];

/** The signed-in user and the broker accounts they link — the Account tab. */
export const ACCOUNT_TABS: readonly SectionTab[] = [
  { path: '/account', label: 'Account' },
  { path: '/connectors', label: 'Brokers' },
];

/**
 * A segmented switch between sibling screens that share ONE bottom-bar tab on
 * a phone.
 *
 * The compact layout has room for five bottom-bar destinations, and the app
 * has eight screens. Rather than hide three behind a "More" menu (where
 * nobody finds them), related screens share a tab and this control sits at
 * the top of each — the same Holdings | Positions split every Indian broker's
 * mobile app uses, so it reads as familiar rather than novel.
 *
 * These are LINKS, not an in-page tab set: each screen keeps its own route,
 * its own deep link and its own back-button entry, exactly as on desktop.
 * Hidden at `lg` and up, where the top nav already reaches every screen.
 */
@Component({
  selector: 'ak-section-tabs',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block lg:hidden' },
  template: `
    <nav class="flex gap-1 rounded-md bg-surface-2 p-1" [attr.aria-label]="label()">
      @for (tab of tabs(); track tab.path) {
        <a
          class="ak-segment"
          [routerLink]="tab.path"
          routerLinkActive="active"
          ariaCurrentWhenActive="page"
          >{{ tab.label }}</a
        >
      }
    </nav>
  `,
  // `routerLinkActive` applies a bare `active` class, so the base and the
  // modifier have to be composable selectors — case 2 in DESIGN.md.
  //
  // The shadow and transition are plain declarations against the same tokens,
  // not `@apply shadow-ak-1 transition-colors`: those two utilities drag ~2kB
  // of `@property` registrations into every component stylesheet that applies
  // them, for one hairline shadow and one fade.
  styles: `
    @reference '../../../styles/tailwind.css';

    .ak-segment {
      @apply flex min-h-9 flex-1 items-center justify-center rounded-sm px-3 text-[13px] font-semibold
        text-text-secondary no-underline;

      transition: background-color var(--duration-ak) var(--ease-ak), color var(--duration-ak) var(--ease-ak);
    }

    /* Lifted, not sunk: white on grey in light, a step LIGHTER than the track in dark. */
    .ak-segment.active {
      @apply bg-surface-1 text-text-primary dark:bg-surface-3;

      box-shadow: var(--shadow-ak-1);
    }
  `,
})
export class SectionTabsComponent {
  readonly tabs = input.required<readonly SectionTab[]>();
  /** Accessible name of the switch, e.g. "Portfolio". */
  readonly label = input.required<string>();
}

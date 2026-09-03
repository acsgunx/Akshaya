import { Routes } from '@angular/router';

import { anonymousOnlyGuard, authGuard } from './core/auth.guard';

/**
 * Every feature is a lazy standalone route (`loadComponent`), never an
 * NgModule. Route param names are stable, opaque ids/keys — `connectorId`
 * and `instrument` (the canonical `InstrumentKey` string) — so a route never
 * has to special-case which broker it's dealing with either.
 *
 * Everything that shows or touches money sits behind `authGuard`. That guard
 * is a redirect, not a lock: the API rejects unauthenticated calls on its own
 * (see the fail-closed fallback policy in the composition root), so bypassing
 * it in a browser yields an empty page of 401s rather than anyone's data.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'sign-in',
    title: 'Sign in · Akshaya',
    canActivate: [anonymousOnlyGuard],
    data: { mode: 'sign-in' },
    loadComponent: () => import('./features/account/sign-in.component').then((m) => m.SignInComponent),
  },
  {
    path: 'register',
    title: 'Create account · Akshaya',
    canActivate: [anonymousOnlyGuard],
    data: { mode: 'register' },
    loadComponent: () => import('./features/account/sign-in.component').then((m) => m.SignInComponent),
  },
  {
    path: 'account',
    title: 'Account · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/account/profile.component').then((m) => m.ProfileComponent),
  },
  {
    path: 'dashboard',
    title: 'Dashboard · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'watchlist',
    title: 'Watchlist · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/watchlist/watchlist.component').then((m) => m.WatchlistComponent),
  },
  {
    path: 'positions',
    title: 'Positions · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/positions/positions.component').then((m) => m.PositionsComponent),
  },
  {
    path: 'holdings',
    title: 'Holdings · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/holdings/holdings.component').then((m) => m.HoldingsComponent),
  },
  {
    path: 'orders',
    title: 'Orders · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/orders/orders.component').then((m) => m.OrdersComponent),
  },
  {
    path: 'connectors',
    title: 'Brokers · Akshaya',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/connectors/connector-catalogue.component').then((m) => m.ConnectorCatalogueComponent),
  },
  {
    path: 'connectors/:connectorId/link',
    title: 'Link broker · Akshaya',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/broker-link/broker-link-wizard.component').then((m) => m.BrokerLinkWizardComponent),
  },
  {
    // Deep-linkable chart: /chart/<brokerLinkId>/XNSE:INFY:Equity — same
    // param contract as the trade route below, so a screen holding a link and
    // an instrument can send the user to either without extra lookups.
    // Lazy on purpose and load-bearing: this is the only route that pulls in
    // `lightweight-charts` (~50kB gzipped), which must stay out of the
    // initial bundle — see the `budgets` block in angular.json.
    path: 'chart/:brokerLinkId/:instrument',
    title: 'Chart · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/chart/chart.component').then((m) => m.ChartComponent),
  },
  {
    // Deep-linkable order ticket: /trade/<brokerLinkId>/XNSE:INFY:Equity
    // NOTE: `brokerLinkId` identifies a specific LINKED ACCOUNT, not a
    // connector/broker type — a user can hold two links for the same
    // connector. The order ticket resolves the manifest FROM the link's
    // `connectorId`, not from this param directly. See `BrokerLinksStore`.
    path: 'trade/:brokerLinkId/:instrument',
    title: 'Trade · Akshaya',
    canActivate: [authGuard],
    loadComponent: () => import('./features/order-ticket/order-ticket.component').then((m) => m.OrderTicketComponent),
  },
  { path: '**', redirectTo: 'dashboard' },
];

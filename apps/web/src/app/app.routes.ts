import { Routes } from '@angular/router';

/**
 * Every feature is a lazy standalone route (`loadComponent`), never an
 * NgModule. Route param names are stable, opaque ids/keys — `connectorId`
 * and `instrument` (the canonical `InstrumentKey` string) — so a route never
 * has to special-case which broker it's dealing with either.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'dashboard',
    title: 'Dashboard · Akshaya',
    loadComponent: () => import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'watchlist',
    title: 'Watchlist · Akshaya',
    loadComponent: () => import('./features/watchlist/watchlist.component').then((m) => m.WatchlistComponent),
  },
  {
    path: 'positions',
    title: 'Positions · Akshaya',
    loadComponent: () => import('./features/positions/positions.component').then((m) => m.PositionsComponent),
  },
  {
    path: 'orders',
    title: 'Orders · Akshaya',
    loadComponent: () => import('./features/orders/orders.component').then((m) => m.OrdersComponent),
  },
  {
    path: 'connectors',
    title: 'Brokers · Akshaya',
    loadComponent: () =>
      import('./features/connectors/connector-catalogue.component').then((m) => m.ConnectorCatalogueComponent),
  },
  {
    path: 'connectors/:connectorId/link',
    title: 'Link broker · Akshaya',
    loadComponent: () =>
      import('./features/broker-link/broker-link-wizard.component').then((m) => m.BrokerLinkWizardComponent),
  },
  {
    // Deep-linkable order ticket: /trade/<brokerLinkId>/XNSE:INFY:Equity
    // NOTE: `brokerLinkId` identifies a specific LINKED ACCOUNT, not a
    // connector/broker type — a user can hold two links for the same
    // connector. The order ticket resolves the manifest FROM the link's
    // `connectorId`, not from this param directly. See `BrokerLinksStore`.
    path: 'trade/:brokerLinkId/:instrument',
    title: 'Trade · Akshaya',
    loadComponent: () => import('./features/order-ticket/order-ticket.component').then((m) => m.OrderTicketComponent),
  },
  { path: '**', redirectTo: 'dashboard' },
];

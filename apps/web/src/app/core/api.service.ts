import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import type {
  AuthCredentials,
  AuthStepWire,
  BrokerLink,
  CancelAllRequest,
  CancelAllResult,
  ConnectorHealth,
  ConnectorManifest,
  InstrumentDefinition,
  KillSwitchState,
  ModifyOrderRequest,
  OrderActionResult,
  OrderEstimate,
  OrderQuery,
  OrderRecord,
  PlaceOrderRequest,
  PortfolioSnapshot,
  Quote,
} from './models';

/**
 * Typed HTTP boundary for the whole app. Nothing outside this file (and the
 * SignalStores that call it) touches `HttpClient` directly — every response
 * shape here is one of the wire mirrors in `core/models`, so a shape drift
 * against the backend contracts fails to compile instead of surfacing as an
 * `undefined` deep in a template at runtime.
 *
 * ENDPOINT NOTE: routes below follow the shape of the DTOs in
 * `Akshaya.Api.Contracts` (`PlaceOrderRequestDto`, `BeginLinkRequestDto`,
 * `AuthStepDto`, `BrokerLinkDto`, …) since the API project has not yet wired
 * up its minimal-API endpoints at the time this frontend was built; adjust
 * the string literals below, not the method signatures or call sites, if the
 * backend lands on different paths.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // ---- Connectors (the manifest is the whole point) --------------------------

  getConnectors(): Observable<readonly ConnectorManifest[]> {
    return this.http.get<readonly ConnectorManifest[]>('/api/connectors');
  }

  getConnector(connectorId: string): Observable<ConnectorManifest> {
    return this.http.get<ConnectorManifest>(`/api/connectors/${encodeURIComponent(connectorId)}`);
  }

  getConnectorHealth(connectorId: string): Observable<ConnectorHealth> {
    return this.http.get<ConnectorHealth>(`/api/connectors/${encodeURIComponent(connectorId)}/health`);
  }

  // ---- Broker links / auth (drives the generic link wizard) ------------------

  getLinks(): Observable<readonly BrokerLink[]> {
    return this.http.get<readonly BrokerLink[]>('/api/links');
  }

  beginLink(connectorId: string, credentials: AuthCredentials, nickname?: string, redirectUri?: string): Observable<AuthStepWire> {
    return this.http.post<AuthStepWire>('/api/links', { connectorId, credentials, nickname, redirectUri });
  }

  continueLink(linkId: string, response: string, state: Readonly<Record<string, string>>): Observable<AuthStepWire> {
    return this.http.post<AuthStepWire>(`/api/links/${encodeURIComponent(linkId)}/continue`, { response, state });
  }

  unlink(linkId: string): Observable<void> {
    return this.http.delete<void>(`/api/links/${encodeURIComponent(linkId)}`);
  }

  // ---- Orders ------------------------------------------------------------------

  placeOrder(request: PlaceOrderRequest): Observable<OrderActionResult> {
    return this.http.post<OrderActionResult>('/api/orders', request);
  }

  modifyOrder(orderId: string, request: ModifyOrderRequest): Observable<OrderActionResult> {
    return this.http.put<OrderActionResult>(`/api/orders/${encodeURIComponent(orderId)}`, request);
  }

  cancelOrder(orderId: string): Observable<OrderActionResult> {
    return this.http.delete<OrderActionResult>(`/api/orders/${encodeURIComponent(orderId)}`);
  }

  cancelAll(request: CancelAllRequest): Observable<CancelAllResult> {
    return this.http.post<CancelAllResult>('/api/orders/cancel-all', request);
  }

  estimateOrder(request: PlaceOrderRequest): Observable<OrderEstimate> {
    return this.http.post<OrderEstimate>('/api/orders/estimate', request);
  }

  getOrders(query: OrderQuery = {}): Observable<readonly OrderRecord[]> {
    let params = new HttpParams();
    if (query.from) params = params.set('from', query.from);
    if (query.to) params = params.set('to', query.to);
    if (query.instrument) params = params.set('instrument', query.instrument);
    if (query.openOnly !== undefined) params = params.set('openOnly', String(query.openOnly));
    return this.http.get<readonly OrderRecord[]>('/api/orders', { params });
  }

  getOrder(orderId: string): Observable<OrderRecord> {
    return this.http.get<OrderRecord>(`/api/orders/${encodeURIComponent(orderId)}`);
  }

  // ---- Portfolio -----------------------------------------------------------

  getPortfolio(displayCurrency?: string): Observable<PortfolioSnapshot> {
    const params = displayCurrency ? new HttpParams().set('displayCurrency', displayCurrency) : undefined;
    return this.http.get<PortfolioSnapshot>('/api/portfolio', params ? { params } : {});
  }

  // ---- Instruments / market data (REST fallback; live prices come over SignalR) --

  searchInstruments(query: string, limit = 20): Observable<readonly InstrumentDefinition[]> {
    const params = new HttpParams().set('q', query).set('limit', String(limit));
    return this.http.get<readonly InstrumentDefinition[]>('/api/instruments/search', { params });
  }

  getInstrument(instrument: string): Observable<InstrumentDefinition> {
    return this.http.get<InstrumentDefinition>(`/api/instruments/${encodeURIComponent(instrument)}`);
  }

  getQuote(instrument: string): Observable<Quote> {
    return this.http.get<Quote>(`/api/market-data/quotes/${encodeURIComponent(instrument)}`);
  }

  // ---- Kill switch (per-tenant global trading halt) ---------------------------

  getKillSwitch(): Observable<KillSwitchState> {
    return this.http.get<KillSwitchState>('/api/risk/kill-switch');
  }

  setKillSwitch(engaged: boolean, reason?: string): Observable<KillSwitchState> {
    return this.http.put<KillSwitchState>('/api/risk/kill-switch', { engaged, reason });
  }
}

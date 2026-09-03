import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import type {
  AuthCredentials,
  AuthStepWire,
  BrokerLink,
  CancelAllRequest,
  CancelAllResult,
  CandleSeries,
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
  RegisterRequest,
  SavedCredential,
  SignInRequest,
  TimeFrame,
  UserProfile,
} from './models';

/**
 * Typed HTTP boundary for the whole app. Nothing outside this file (and the
 * SignalStores that call it) touches `HttpClient` directly — every response
 * shape here is one of the wire mirrors in `core/models`, so a shape drift
 * against the backend contracts fails to compile instead of surfacing as an
 * `undefined` deep in a template at runtime.
 *
 * ENDPOINT NOTE: the paths below must match the minimal-API routes in
 * `Akshaya.Api/Endpoints` exactly. They are plain strings on both sides, so
 * nothing catches a drift at compile time — a wrong path is a 404 at runtime,
 * which surfaces as a feature that silently does nothing (an autocomplete
 * that never populates, a detail panel stuck loading). If you change a route
 * on the backend, change it here in the same commit.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // ---- Account and the saved-login vault -------------------------------------

  register(request: RegisterRequest): Observable<UserProfile> {
    return this.http.post<UserProfile>('/api/account/register', request);
  }

  signIn(request: SignInRequest): Observable<UserProfile> {
    return this.http.post<UserProfile>('/api/account/sign-in', request);
  }

  signOut(): Observable<void> {
    return this.http.post<void>('/api/account/sign-out', {});
  }

  /**
   * The signed-in user, or `null` when nobody is. A 204 (not a 401) for a
   * signed-out visitor, because "nobody is signed in" is the expected answer
   * to this question on a cold load, not an error.
   */
  me(): Observable<UserProfile | null> {
    return this.http
      .get<UserProfile>('/api/account/me', { observe: 'response' })
      .pipe(map((response) => response.body ?? null));
  }

  getSavedCredentials(): Observable<readonly SavedCredential[]> {
    return this.http.get<readonly SavedCredential[]>('/api/account/credentials');
  }

  deleteSavedCredential(id: string): Observable<void> {
    return this.http.delete<void>(`/api/account/credentials/${encodeURIComponent(id)}`);
  }

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

  /**
   * Starts a link.
   *
   * `savedCredentialId` lets the server fill in fields the user asked it to
   * remember; anything in `credentials` is layered on top. The saved values
   * themselves never travel — the browser sends an id, and the server holds
   * the only key that turns it back into secrets.
   *
   * `rememberFields` names the field keys to store once the broker ACCEPTS
   * this login. Nothing is saved on a failed attempt.
   */
  beginLink(request: {
    connectorId: string;
    credentials: AuthCredentials;
    nickname?: string;
    redirectUri?: string;
    savedCredentialId?: string;
    rememberFields?: readonly string[];
  }): Observable<AuthStepWire> {
    return this.http.post<AuthStepWire>('/api/links', request);
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

  /**
   * Instrument search, ALWAYS through one specific broker link. There is no
   * standalone instrument master yet — `MarketDataEndpoints` answers this out
   * of the connector's own reference facet — so "which broker" is a required
   * question here, not an optional filter.
   */
  searchInstruments(
    brokerLinkId: string,
    query: string,
    limit = 20,
  ): Observable<readonly InstrumentDefinition[]> {
    const params = new HttpParams()
      .set('brokerLinkId', brokerLinkId)
      .set('query', query)
      .set('limit', String(limit));
    return this.http.get<readonly InstrumentDefinition[]>('/api/market-data/instruments/search', { params });
  }

  /**
   * Full definition for one canonical key. Served from the shared instrument
   * master, so this is a dictionary hit rather than a broker round trip once
   * the master is warm.
   */
  getInstrument(brokerLinkId: string, instrument: string): Observable<InstrumentDefinition> {
    const params = new HttpParams().set('brokerLinkId', brokerLinkId).set('instrument', instrument);
    return this.http.get<InstrumentDefinition>('/api/market-data/instruments/resolve', { params });
  }

  /**
   * One-shot quote. The live equivalent is the SignalR hub — prefer that for
   * anything that stays on screen, and keep this for a single point-in-time read.
   */
  getQuote(brokerLinkId: string, instrument: string): Observable<Quote> {
    const params = new HttpParams().set('brokerLinkId', brokerLinkId).set('instrument', instrument);
    return this.http.get<Quote>('/api/market-data/quote', { params });
  }

  /**
   * Historical OHLC bars for one instrument — the chart's backfill, before
   * the SignalR stream takes over for everything after `to`.
   *
   * `from`/`to` go on the wire as ISO-8601 with an offset, which is what the
   * backend's `DateTimeOffset` parameters bind from; a bare local datetime
   * would be read in the SERVER's zone and silently shift a session's bars
   * for anyone trading a venue in another one. `Date.toISOString()` is always
   * UTC with a `Z`, so the round trip is unambiguous by construction.
   *
   * Which `timeFrame` values a broker will actually answer is declared per
   * connector in `manifest.marketData.historicalTimeFrames` — ask the
   * manifest, never this method, what to offer a user.
   */
  getHistory(
    brokerLinkId: string,
    instrument: string,
    timeFrame: TimeFrame,
    from: Date,
    to: Date,
  ): Observable<CandleSeries> {
    const params = new HttpParams()
      .set('brokerLinkId', brokerLinkId)
      .set('instrument', instrument)
      .set('timeFrame', timeFrame)
      .set('from', from.toISOString())
      .set('to', to.toISOString());
    return this.http.get<CandleSeries>('/api/market-data/history', { params });
  }

  // ---- Kill switch (per-tenant global trading halt) ---------------------------

  getKillSwitch(): Observable<KillSwitchState> {
    return this.http.get<KillSwitchState>('/api/risk/kill-switch');
  }

  setKillSwitch(engaged: boolean, reason?: string): Observable<KillSwitchState> {
    return this.http.put<KillSwitchState>('/api/risk/kill-switch', { engaged, reason });
  }
}

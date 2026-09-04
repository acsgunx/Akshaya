/**
 * TypeScript mirror of `Akshaya.Connectors.Abstractions.ConnectorManifest` and
 * everything it composes.
 *
 * THIS FILE IS THE ARCHITECTURAL LOAD-BEARING WALL OF THE FRONTEND. The order
 * ticket, the broker-link wizard and the connector catalogue render entirely
 * from a `ConnectorManifest` fetched at runtime from `GET /api/connectors`.
 * If a UI feature needs to know something about a broker that isn't a field
 * on one of these types, the fix is a manifest field on the BACKEND, never a
 * conditional on a connector id in a component — see the module doc comment
 * on `ConnectorManifest.cs` for the same rule stated from the backend side.
 */

import type { AssetClass, CurrencyCode, VenueMic } from './common.model';
import type { OrderType, OrderVariety, PositionEffect, StreamMode, TimeFrame, TimeInForce } from './trading.model';

export type ConnectorHosting = 'inProcess' | 'outOfProcess' | 'gateway';

export interface GatewaySpec {
  readonly id: string;
  readonly image?: string;
  readonly port?: number;
  /** True when every user needs their own gateway process — a real per-user infra cost. */
  readonly perCredential: boolean;
  readonly healthEndpoint?: string;
  readonly setupInstructionsUrl?: string;
}

/** How a broker authenticates. Informational only — the actual flow is driven by AuthStep. */
export type AuthModel =
  | 'oAuth2'
  | 'oAuth1a'
  | 'passwordOtp'
  | 'passwordTotp'
  | 'staticToken'
  | 'rsaSigned'
  | 'gatewaySession';

export type ChallengeKind = 'smsOtp' | 'emailOtp' | 'totp' | 'securityQuestion' | 'deviceApproval';

/**
 * One field the link wizard's form builds itself from. `key` is the wire key
 * used in `BeginLinkRequestDto.credentials` — the frontend never invents its
 * own field names, it echoes the manifest's back verbatim.
 */
export interface CredentialField {
  readonly key: string;
  readonly label: string;
  readonly secret: boolean;
  readonly optional: boolean;
  readonly placeholder?: string;
  readonly help?: string;
  /** Client-side validation only; the server re-validates and this is never a substitute. */
  readonly pattern?: string;
}

export interface AuthSpec {
  readonly model: AuthModel;
  readonly challenges: readonly ChallengeKind[];
  /** ISO-8601 duration string (e.g. "PT8H"), or absent. */
  readonly sessionLifetime?: string;
  /** True for most Indian brokers: session dies at venue midnight regardless of issue time. */
  readonly expiresAtVenueMidnight: boolean;
  readonly venueMidnightTimeZone?: string;
  readonly refreshSupported: boolean;
  /** ISO-8601 duration string; present only for brokers that need an active keep-alive. */
  readonly keepAliveInterval?: string;
  readonly credentialFields: readonly CredentialField[];
}

export interface BasketSpec {
  readonly supported: boolean;
  readonly maxLegs: number;
  /** False = the connector loops single orders; the UI must warn partial execution is possible. */
  readonly atomic: boolean;
}

export interface OrderSpec {
  readonly types: readonly OrderType[];
  readonly timeInForce: readonly TimeInForce[];
  readonly positionEffects: readonly PositionEffect[];
  readonly varieties: readonly OrderVariety[];
  /** Which fields a modify may change; the UI disables every other field on the modify form. */
  readonly modifiable: readonly string[];
  readonly fractionalQuantity: boolean;
  readonly shortSellEquity: boolean;
  readonly basket: BasketSpec;
  readonly bracket: boolean;
  readonly cover: boolean;
  readonly gtt: boolean;
  readonly marginEstimate: boolean;
  readonly chargesEstimate: boolean;
  readonly cancelAll: boolean;
  /**
   * Whether an open position can be moved between margin products (intraday
   * to delivery and back). Not an order type, but the same capability
   * question — the positions screen hides the action when this is false
   * rather than offering a button that always fails.
   */
  readonly positionConversion: boolean;
}

export interface MarketDataSpec {
  readonly streaming: boolean;
  readonly streamModes: readonly StreamMode[];
  readonly depthLevels: number;
  readonly historical: boolean;
  readonly historicalTimeFrames: readonly TimeFrame[];
  readonly optionChain: boolean;
  readonly maxStreamSubscriptions: number;
  readonly historyDays?: number;
}

export interface RateLimitSpec {
  /** "orders" | "data" | "quotes" | "global" */
  readonly scope: string;
  readonly perSecond?: number;
  readonly perMinute?: number;
  readonly perDay?: number;
}

export interface SandboxSpec {
  readonly available: boolean;
  readonly baseUrl?: string;
  readonly notes?: string;
}

export interface ComplianceSpec {
  /** True where automated order flow needs regulatory blessing (SEBI algo approval, e.g.). */
  readonly algoApprovalRequired: boolean;
  readonly regulator?: string;
  readonly algoIdRequired: boolean;
  readonly notes?: string;
}

export interface ConnectorManifest {
  readonly id: string;
  readonly displayName: string;
  readonly vendor: string;
  readonly contractVersion: string;
  readonly connectorVersion: string;
  readonly hosting: ConnectorHosting;
  readonly gateway?: GatewaySpec;
  readonly jurisdictions: readonly string[];
  readonly venues: readonly VenueMic[];
  readonly currencies: readonly CurrencyCode[];
  readonly assetClasses: readonly AssetClass[];
  readonly auth: AuthSpec;
  readonly orders: OrderSpec;
  readonly marketData: MarketDataSpec;
  readonly rateLimits: readonly RateLimitSpec[];
  readonly sandbox?: SandboxSpec;
  readonly compliance: ComplianceSpec;
}

// ---- Small capability-check helpers -----------------------------------------
// Kept here, next to the type, so "does this connector support X" is always
// asked the same way rather than re-implemented ad hoc in each component.

export function manifestSupportsOrderType(manifest: ConnectorManifest, type: OrderType): boolean {
  return manifest.orders.types.includes(type);
}

export function manifestSupportsVenue(manifest: ConnectorManifest, venue: VenueMic): boolean {
  return manifest.venues.some((v) => v.toUpperCase() === venue.toUpperCase());
}

export function connectorHealthBadgeSeverity(manifest: ConnectorManifest): 'info' | 'warning' {
  // Gateway-hosted connectors have an extra failure mode (the local daemon
  // itself) worth flagging distinctly in a catalogue view.
  return manifest.hosting === 'gateway' ? 'warning' : 'info';
}

/**
 * Whether the broker will accept a change to `field` on an amendment.
 *
 * CASE-INSENSITIVE, AND THAT IS THE ENTIRE POINT. `orders.modifiable` is a
 * list of VALUES, not property names, so the API's camelCase policy does not
 * touch it — the manifests ship `"LimitPrice"` and it arrives as
 * `"LimitPrice"`, while every call site here naturally reaches for
 * `'limitPrice'`. The mismatch does not throw and does not warn: the field
 * simply never renders, and a trader quietly loses the ability to amend a
 * price.
 *
 * That is not hypothetical. It is the exact failure `docs/STATUS.md` records
 * being caught once already ("the UI disables fields by that name, so the
 * mismatch would have silently disabled the wrong controls on the order
 * ticket") — and it was still live on the order ticket's disclosed-quantity
 * field until this helper replaced the raw `.includes()` calls.
 *
 * Every read of `modifiable` goes through here. Never call `.includes()` on
 * it directly.
 */
export function canModifyField(manifest: ConnectorManifest, field: string): boolean {
  const wanted = field.toLowerCase();
  return manifest.orders.modifiable.some((name) => name.toLowerCase() === wanted);
}

/**
 * Mirrors `Akshaya.SharedKernel.Error` as it rides an RFC 7807 ProblemDetails
 * response — see `Akshaya.Api.Infrastructure.ProblemDetailsMapper.ToProblem`.
 * The HTTP status is the CLIENT-ACTION signal (401 → prompt reauth, 429 →
 * back off, 422 → show the refusal, 501 → disable the control); `code` is
 * the canonical `ConnectorErrorCodes` value for anything more specific than
 * the status alone conveys.
 */
export interface ApiProblem {
  readonly type?: string;
  readonly title?: string;
  readonly status: number;
  readonly detail?: string;
  /** Canonical `ConnectorErrorCodes.*` value. */
  readonly code?: string;
  /** The broker's own error code, verbatim — never paraphrased. */
  readonly vendorCode?: string;
  /** The broker's own error text, verbatim — always shown alongside our canonical message. */
  readonly vendorMessage?: string;
  readonly [extension: string]: unknown;
}

/** Canonical error codes this build understands — mirrors `ConnectorErrorCodes`. */
export const ConnectorErrorCodes = {
  InvalidCredentials: 'connector.invalid_credentials',
  ChallengeFailed: 'connector.challenge_failed',
  SessionExpired: 'connector.session_expired',
  ReauthRequired: 'connector.reauth_required',
  GatewayUnavailable: 'connector.gateway_unavailable',
  InvalidRequest: 'connector.invalid_request',
  InstrumentNotFound: 'connector.instrument_not_found',
  OrderNotFound: 'connector.order_not_found',
  NotSupported: 'connector.not_supported',
  InsufficientFunds: 'connector.insufficient_funds',
  MarketClosed: 'connector.market_closed',
  RiskRejected: 'connector.risk_rejected',
  OrderRejected: 'connector.order_rejected',
  RateLimited: 'connector.rate_limited',
  Timeout: 'connector.timeout',
  BrokerUnavailable: 'connector.broker_unavailable',
  Unknown: 'connector.unknown',
} as const;

export type ConnectorErrorCode = (typeof ConnectorErrorCodes)[keyof typeof ConnectorErrorCodes];

const RETRYABLE: ReadonlySet<string> = new Set([
  ConnectorErrorCodes.RateLimited,
  ConnectorErrorCodes.Timeout,
  ConnectorErrorCodes.BrokerUnavailable,
  ConnectorErrorCodes.GatewayUnavailable,
]);

/**
 * Mirrors `ConnectorErrorCodes.IsRetryable`. Used ONLY to decide whether to
 * offer a "retry" affordance on a read (quotes, portfolio) — never to
 * auto-retry an order placement, where a blind retry risks a duplicate.
 */
export function isRetryableCode(code: string | undefined): boolean {
  return !!code && RETRYABLE.has(code);
}

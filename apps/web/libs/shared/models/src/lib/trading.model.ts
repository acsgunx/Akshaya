/**
 * Mirrors of Akshaya.SharedKernel.TradingEnums, serialised as camelCase
 * strings by `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` (see
 * `AkshayaJson.Configure`).
 */

export type Side = 'buy' | 'sell';

export type OrderType = 'market' | 'limit' | 'stop' | 'stopLimit' | 'marketIfTouched' | 'trailingStop';

export type TimeInForce = 'day' | 'gtc' | 'ioc' | 'fok' | 'gtd' | 'atTheOpen' | 'atTheClose';

/**
 * `PositionEffect` is a [Flags] enum on the backend, but every place the UI
 * consumes it (`OrderSpec.PositionEffects`, `PlaceOrderRequestDto`) is a list
 * of the SINGLE named values a manifest supports — never a combined bitmask
 * — so we model it as a plain string union and let a manifest's
 * `positionEffects` array be `PositionEffect[]`.
 *
 * IMPORTANT: labels are venue-agnostic on purpose. "Delivery" and "Intraday"
 * are used everywhere, never "CNC"/"MIS" (India-specific broker jargon) or
 * "cash"/"margin" (US-specific) — see `position-effect.label.ts`.
 */
export type PositionEffect = 'intraday' | 'delivery' | 'margin' | 'carryForward' | 'shortSell';

export type OrderVariety = 'regular' | 'afterMarket' | 'cover' | 'bracket' | 'iceberg' | 'goodTillTriggered';

/** Canonical order lifecycle as the broker reports it (Akshaya.SharedKernel.OrderStatus). */
export type OrderStatus =
  | 'pendingSubmit'
  | 'submitted'
  | 'open'
  | 'partiallyFilled'
  | 'filled'
  | 'cancelled'
  | 'rejected'
  | 'expired'
  | 'unknown';

/**
 * Platform lifecycle state (`OrderState`, richer than `OrderStatus` — this is
 * what the order blotter colours rows on). `unknown` is the state reached on
 * a send timeout: the UI MUST render "checking with your broker", never a
 * status, and MUST NOT offer a resubmit action while unresolved — resubmitting
 * an order the platform cannot yet rule out having reached the broker is how
 * duplicate fills happen.
 */
export type OrderState =
  | 'pendingSubmit'
  | 'riskChecked'
  | 'submitted'
  | 'acknowledged'
  | 'partiallyFilled'
  | 'filled'
  | 'cancelled'
  | 'rejected'
  | 'expired'
  | 'unknown';

export function isOrderStateTerminal(state: OrderState): boolean {
  return state === 'filled' || state === 'cancelled' || state === 'rejected' || state === 'expired';
}

export function isOrderStateWorking(state: OrderState): boolean {
  return state === 'submitted' || state === 'acknowledged' || state === 'partiallyFilled';
}

export type StreamMode = 'ltp' | 'quote' | 'full';

export type TimeFrame =
  | 'oneMinute'
  | 'threeMinutes'
  | 'fiveMinutes'
  | 'fifteenMinutes'
  | 'thirtyMinutes'
  | 'oneHour'
  | 'oneDay'
  | 'oneWeek'
  | 'oneMonth';

export type StreamState = 'disconnected' | 'connecting' | 'connected' | 'degraded' | 'reconnecting';

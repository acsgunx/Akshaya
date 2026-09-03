/**
 * Human labels for the enums that ride on a `ConnectorManifest`.
 *
 * DELIBERATELY GENERIC. `PositionEffect.intraday`/`delivery` are labelled
 * "Intraday"/"Delivery" everywhere — never India's "MIS"/"CNC", never the
 * US's "day"/"margin" jargon. A broker whose UI needs its own vocabulary for
 * these belongs to a different design (or a `label` field added to the
 * manifest itself); it must never become a string table keyed by connector
 * id inside a component, which is the exact anti-pattern this whole app
 * exists to avoid.
 */

import type { OrderType, OrderVariety, PositionEffect, Side, TimeFrame, TimeInForce } from './models/trading.model';
import type { ChallengeKind } from './models/manifest.model';

export function positionEffectLabel(effect: PositionEffect): string {
  switch (effect) {
    case 'intraday':
      return 'Intraday';
    case 'delivery':
      return 'Delivery';
    case 'margin':
      return 'Margin';
    case 'carryForward':
      return 'Carry Forward';
    case 'shortSell':
      return 'Short Sell';
  }
}

export function orderTypeLabel(type: OrderType): string {
  switch (type) {
    case 'market':
      return 'Market';
    case 'limit':
      return 'Limit';
    case 'stop':
      return 'Stop';
    case 'stopLimit':
      return 'Stop-Limit';
    case 'marketIfTouched':
      return 'Market-if-Touched';
    case 'trailingStop':
      return 'Trailing Stop';
  }
}

export function timeInForceLabel(tif: TimeInForce): string {
  switch (tif) {
    case 'day':
      return 'Day';
    case 'gtc':
      return 'Good-till-cancelled';
    case 'ioc':
      return 'Immediate-or-cancel';
    case 'fok':
      return 'Fill-or-kill';
    case 'gtd':
      return 'Good-till-date';
    case 'atTheOpen':
      return 'At the open';
    case 'atTheClose':
      return 'At the close';
  }
}

export function orderVarietyLabel(variety: OrderVariety): string {
  switch (variety) {
    case 'regular':
      return 'Regular';
    case 'afterMarket':
      return 'After-market';
    case 'cover':
      return 'Cover';
    case 'bracket':
      return 'Bracket';
    case 'iceberg':
      return 'Iceberg';
    case 'goodTillTriggered':
      return 'Good-till-triggered';
  }
}

export function sideLabel(side: Side): string {
  return side === 'buy' ? 'Buy' : 'Sell';
}

export function challengeKindLabel(kind: ChallengeKind): string {
  switch (kind) {
    case 'smsOtp':
      return 'SMS code';
    case 'emailOtp':
      return 'Email code';
    case 'totp':
      return 'Authenticator code';
    case 'securityQuestion':
      return 'Security question';
    case 'deviceApproval':
      return 'Device approval';
  }
}

/** Which order types need a limit price field shown. */
/**
 * Chart timeframe labels. Short forms ("5m", "1D") because they sit in a
 * dense toggle above a chart, and because they are the notation every
 * charting tool a trader already uses writes them in.
 */
export function timeFrameLabel(frame: TimeFrame): string {
  switch (frame) {
    case 'oneMinute':
      return '1m';
    case 'threeMinutes':
      return '3m';
    case 'fiveMinutes':
      return '5m';
    case 'fifteenMinutes':
      return '15m';
    case 'thirtyMinutes':
      return '30m';
    case 'oneHour':
      return '1H';
    case 'oneDay':
      return '1D';
    case 'oneWeek':
      return '1W';
    case 'oneMonth':
      return '1M';
  }
}

export function orderTypeNeedsLimitPrice(type: OrderType): boolean {
  return type === 'limit' || type === 'stopLimit';
}

/** Which order types need a trigger price field shown. */
export function orderTypeNeedsTriggerPrice(type: OrderType): boolean {
  return type === 'stop' || type === 'stopLimit' || type === 'trailingStop' || type === 'marketIfTouched';
}

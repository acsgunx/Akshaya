/**
 * Mirrors `Akshaya.Api.Contracts.OrderContracts` request/response DTOs and
 * `Akshaya.Connectors.Abstractions.Orders` value types that ride along inside
 * them (`ChargesEstimate`, `MarginEstimate`).
 */

import type { InstrumentKey, Money, QuantityValue } from './common.model';
import type { OrderState, OrderStatus, OrderType, OrderVariety, PositionEffect, Side, TimeInForce } from './trading.model';

/** POST /api/orders body. Mirrors `PlaceOrderRequestDto`. */
export interface PlaceOrderRequest {
  readonly brokerLinkId: string;
  /** Idempotency key: reuse the SAME value on a retry of the same intent, never per HTTP attempt. */
  readonly clientOrderId?: string;
  readonly instrument: InstrumentKey;
  readonly side: Side;
  readonly quantity: QuantityValue;
  readonly orderType: OrderType;
  readonly positionEffect: PositionEffect;
  readonly timeInForce: TimeInForce;
  readonly variety: OrderVariety;
  readonly limitPrice?: Money;
  readonly triggerPrice?: Money;
  readonly disclosedQuantity?: QuantityValue;
  readonly goodTillDate?: string; // yyyy-MM-dd
  readonly algoId?: string;
  readonly tag?: string;
}

/** PUT /api/orders/{id} body. Mirrors `ModifyOrderRequestDto` — every field optional, at least one required. */
export interface ModifyOrderRequest {
  readonly quantity?: QuantityValue;
  readonly limitPrice?: Money;
  readonly triggerPrice?: Money;
  readonly orderType?: OrderType;
  readonly timeInForce?: TimeInForce;
  readonly disclosedQuantity?: QuantityValue;
}

export interface CancelAllRequest {
  /** Omit to cancel across every usable link — the UI must say so explicitly before sending this. */
  readonly brokerLinkId?: string;
}

export interface OrderEvent {
  readonly at: string;
  readonly actor: string;
  readonly state: OrderState;
  readonly status: OrderStatus;
  readonly note?: string;
  /** The broker's own payload, verbatim, for dispute resolution. */
  readonly rawBrokerPayload?: string;
}

/** Mirrors `OrderDto`. */
export interface OrderRecord {
  readonly id: string;
  readonly clientOrderId: string;
  readonly brokerLinkId: string;
  /** Opaque connector id — a label to group and display by, never a branch target. */
  readonly connectorId: string;
  readonly brokerOrderId?: string;
  readonly instrument: InstrumentKey;
  readonly side: Side;
  readonly quantity: QuantityValue;
  readonly filledQuantity: QuantityValue;
  readonly pendingQuantity: QuantityValue;
  readonly orderType: OrderType;
  readonly positionEffect: PositionEffect;
  readonly timeInForce: TimeInForce;
  readonly variety: OrderVariety;
  readonly limitPrice?: Money;
  readonly triggerPrice?: Money;
  readonly averagePrice?: Money;
  readonly state: OrderState;
  readonly status: OrderStatus;
  /** Broker text, verbatim — always render this alongside the canonical status. */
  readonly statusMessage?: string;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly events: readonly OrderEvent[];
}

export function isOrderUnresolved(order: Pick<OrderRecord, 'state'>): boolean {
  return order.state === 'unknown';
}

/** Response of place/modify/cancel. Mirrors `OrderActionResponse`. */
export interface OrderActionResult {
  readonly orderId: string;
  readonly clientOrderId: string;
  readonly brokerOrderId?: string;
  readonly state: OrderState;
  readonly status: OrderStatus;
  readonly message?: string;
}

export interface CancelAllLinkResult {
  readonly brokerLinkId: string;
  readonly requested: number;
  readonly cancelled: number;
  /** False = the connector looped single cancels; a partial sweep is possible. */
  readonly atomic: boolean;
  readonly error?: string;
}

/** Mirrors `CancelAllResponse`. `isPartial` MUST be surfaced prominently — never hide a partial sweep behind a clean count. */
export interface CancelAllResult {
  readonly links: readonly CancelAllLinkResult[];
  readonly totalRequested: number;
  readonly totalCancelled: number;
  readonly isPartial: boolean;
}

export interface ChargeLine {
  readonly name: string;
  readonly amount: Money;
  readonly note?: string;
}

/**
 * Mirrors `OrderEstimateResponse`. Both halves are optional because the
 * MANIFEST says whether the broker offers them (`orders.marginEstimate`,
 * `orders.chargesEstimate`) — a client must render whichever half it gets and
 * never fabricate the other.
 */
export interface OrderEstimate {
  readonly marginRequired?: Money;
  readonly marginAvailable?: Money;
  readonly isMarginSufficient?: boolean;
  readonly charges: readonly ChargeLine[];
  readonly totalCharges?: Money;
  readonly warnings: readonly string[];
}

export interface OrderQuery {
  readonly from?: string;
  readonly to?: string;
  readonly instrument?: InstrumentKey;
  readonly openOnly?: boolean;
}

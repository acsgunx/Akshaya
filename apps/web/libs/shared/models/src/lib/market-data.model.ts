/**
 * Mirrors `Akshaya.Connectors.Abstractions.MarketData` and `Streaming` —
 * quotes, candles, depth, option chains, and the SignalR event envelope.
 */

import type { InstrumentKey, Money, QuantityValue } from './common.model';
import type { StreamState, TimeFrame } from './trading.model';

export interface Quote {
  readonly instrument: InstrumentKey;
  readonly lastPrice: Money;
  readonly open?: Money;
  readonly high?: Money;
  readonly low?: Money;
  readonly previousClose?: Money;
  readonly bidPrice?: Money;
  readonly askPrice?: Money;
  readonly bidQuantity?: QuantityValue;
  readonly askQuantity?: QuantityValue;
  readonly volume?: number;
  readonly openInterest?: number;
  readonly timestamp: string;
  /** Present only when previousClose is; computed client-side otherwise via `computeChange`. */
  readonly change?: Money;
  readonly changePercent?: number;
}

export interface Candle {
  readonly openTime: string;
  readonly open: number;
  readonly high: number;
  readonly low: number;
  readonly close: number;
  readonly volume: number;
  readonly openInterest?: number;
}

export interface CandleSeries {
  readonly instrument: InstrumentKey;
  readonly timeFrame: TimeFrame;
  readonly currency: string;
  readonly candles: readonly Candle[];
}

export interface DepthLevel {
  readonly price: Money;
  readonly quantity: QuantityValue;
  readonly orders?: number;
}

export interface MarketDepth {
  readonly instrument: InstrumentKey;
  readonly bids: readonly DepthLevel[];
  readonly asks: readonly DepthLevel[];
  readonly timestamp: string;
}

export interface OptionChainRow {
  readonly strike: number;
  readonly call?: Quote;
  readonly put?: Quote;
  readonly callOpenInterest?: number;
  readonly putOpenInterest?: number;
}

export interface OptionChain {
  readonly underlying: InstrumentKey;
  readonly expiry: string;
  readonly rows: readonly OptionChainRow[];
  readonly underlyingPrice?: Money;
}

/** Live tick pushed over SignalR. Mirrors `Tick`. */
export interface Tick {
  readonly instrument: InstrumentKey;
  readonly lastPrice: Money;
  readonly lastQuantity?: QuantityValue;
  readonly volume?: number;
  readonly bidPrice?: Money;
  readonly askPrice?: Money;
  readonly open?: Money;
  readonly high?: Money;
  readonly low?: Money;
  readonly previousClose?: Money;
  readonly openInterest?: number;
  readonly timestamp: string;
}

/**
 * Connector-level liveness, mirrors `ConnectorHealth`. Surfaced directly by
 * `connection-status` — a trader must never be left guessing whether a
 * broker link is actually working.
 */
export interface ConnectorHealth {
  readonly isHealthy: boolean;
  readonly streamState: StreamState;
  readonly sessionValid: boolean;
  readonly sessionExpiresAt?: string;
  readonly gatewayRunning: boolean;
  readonly detail?: string;
  readonly latencyMs?: number;
}

export function computeChange(quote: Pick<Quote, 'lastPrice' | 'previousClose'>): number | undefined {
  if (!quote.previousClose) {
    return undefined;
  }
  return Number(quote.lastPrice.amount) - Number(quote.previousClose.amount);
}

export function computeChangePercent(quote: Pick<Quote, 'lastPrice' | 'previousClose'>): number | undefined {
  const prev = quote.previousClose ? Number(quote.previousClose.amount) : undefined;
  if (!prev) {
    return undefined;
  }
  return ((Number(quote.lastPrice.amount) - prev) / prev) * 100;
}

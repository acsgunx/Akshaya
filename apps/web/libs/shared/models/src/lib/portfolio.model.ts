/**
 * Mirrors `Akshaya.Modules.Portfolio.Models.PortfolioModels` — the blended,
 * multi-currency, multi-broker portfolio snapshot the dashboard renders.
 */

import type { CurrencyCode, InstrumentKey, Money, QuantityValue } from './common.model';
import type { PositionEffect } from './trading.model';
import type { ApiProblem } from './error.model';

export type PositionGrouping = 'isin' | 'figi' | 'instrumentKey';

/**
 * The outcome of fetching from ONE broker link. `isComplete` false means we
 * could not fully read that link — the dashboard MUST distinguish "you hold
 * nothing here" from "we could not reach this broker"; collapsing the two
 * teaches a trader not to trust the numbers.
 */
export interface PortfolioSourceStatus {
  readonly brokerLinkId: string;
  readonly connectorId: string;
  readonly displayName: string;
  readonly positionsOk: boolean;
  readonly holdingsOk: boolean;
  readonly balancesOk: boolean;
  readonly error?: ApiProblem;
  readonly fetchedAt: string;
  readonly durationMs?: number;
  readonly isComplete: boolean;
}

export interface BrokerPositionLeg {
  readonly brokerLinkId: string;
  readonly connectorId: string;
  readonly displayName: string;
  readonly netQuantity: QuantityValue;
  readonly averagePrice: Money;
  readonly lastPrice?: Money;
  readonly marketValue?: Money;
  readonly unrealisedPnl?: Money;
  readonly realisedPnl?: Money;
  readonly positionEffect: PositionEffect;
}

/**
 * ONE CURRENCY PER ROW, ALWAYS — mirrors the backend's own rule. The same
 * ISIN held in USD at one broker and SGD at another is two rows here, never
 * merged, because merging would require silently picking an FX rate just to
 * state a quantity's value.
 */
export interface BlendedPosition {
  readonly groupKey: string;
  readonly groupedBy: PositionGrouping;
  readonly instrument: InstrumentKey;
  readonly isin?: string;
  readonly figi?: string;
  readonly currency: CurrencyCode;
  readonly netQuantity: QuantityValue;
  readonly averagePrice: Money;
  readonly lastPrice?: Money;
  readonly marketValue?: Money;
  readonly unrealisedPnl?: Money;
  readonly realisedPnl?: Money;
  readonly legs: readonly BrokerPositionLeg[];
}

export interface BrokerHoldingLeg {
  readonly brokerLinkId: string;
  readonly connectorId: string;
  readonly displayName: string;
  readonly quantity: QuantityValue;
  readonly averagePrice: Money;
  readonly lastPrice?: Money;
  readonly currentValue?: Money;
  readonly unrealisedPnl?: Money;
  readonly pledgedQuantity: QuantityValue;
}

export interface BlendedHolding {
  readonly groupKey: string;
  readonly groupedBy: PositionGrouping;
  readonly instrument: InstrumentKey;
  readonly isin?: string;
  readonly figi?: string;
  readonly currency: CurrencyCode;
  readonly quantity: QuantityValue;
  readonly averagePrice: Money;
  readonly lastPrice?: Money;
  readonly currentValue?: Money;
  readonly unrealisedPnl?: Money;
  readonly pledgedQuantity: QuantityValue;
  readonly legs: readonly BrokerHoldingLeg[];
}

export interface BrokerBalanceLeg {
  readonly brokerLinkId: string;
  readonly connectorId: string;
  readonly displayName: string;
  readonly availableToTrade: Money;
  readonly cashBalance?: Money;
  readonly usedMargin?: Money;
  readonly availableMargin?: Money;
}

/** Every broker's balance in ONE currency, summed. Never collapse the list into a single converted figure at this layer. */
export interface CurrencyBalance {
  readonly currency: CurrencyCode;
  readonly availableToTrade: Money;
  readonly cashBalance?: Money;
  readonly usedMargin?: Money;
  readonly availableMargin?: Money;
  readonly collateral?: Money;
  readonly realisedPnl?: Money;
  readonly unrealisedPnl?: Money;
  readonly legs: readonly BrokerBalanceLeg[];
}

export interface AppliedFxRate {
  readonly from: CurrencyCode;
  readonly to: CurrencyCode;
  readonly rate: number;
  readonly asOf: string;
}

/**
 * `unrealisedConverted`/`realisedConverted` are null when at least one leg
 * could not be converted — a partial total must NEVER be presented as whole,
 * so a null here means "show native figures only, with the warnings below".
 */
export interface PnlSummary {
  readonly displayCurrency: CurrencyCode;
  readonly unrealisedNative: readonly Money[];
  readonly realisedNative: readonly Money[];
  readonly unrealisedConverted?: Money;
  readonly realisedConverted?: Money;
  readonly ratesUsed: readonly AppliedFxRate[];
  readonly conversionWarnings: readonly string[];
  readonly isFullyConverted: boolean;
}

/**
 * The whole portfolio at one instant. `isPartial` is the single most
 * important field here: true means at least one broker link could not be
 * fully read, and the dashboard MUST say "N of M accounts unavailable"
 * rather than a total that quietly excludes part of the user's money.
 */
export interface PortfolioSnapshot {
  readonly asOf: string;
  readonly displayCurrency: CurrencyCode;
  readonly positions: readonly BlendedPosition[];
  readonly holdings: readonly BlendedHolding[];
  readonly balances: readonly CurrencyBalance[];
  readonly pnl: PnlSummary;
  readonly sources: readonly PortfolioSourceStatus[];
  readonly isPartial: boolean;
  readonly failedSources: readonly PortfolioSourceStatus[];
}

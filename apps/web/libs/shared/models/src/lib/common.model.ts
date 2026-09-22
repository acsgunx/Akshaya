/**
 * Wire-format mirrors of Akshaya.SharedKernel's value types.
 *
 * These are hand-written to match the JSON converters in
 * `Akshaya.Api.Contracts.JsonConverters` EXACTLY (see AkshayaJson.Configure):
 *  - Money            → { amount: string; currency: string }   (MoneyJsonConverter)
 *  - Quantity         → string                                  (QuantityJsonConverter)
 *  - InstrumentKey    → string, e.g. "XNSE:INFY:Equity"          (InstrumentKeyJsonConverter)
 *  - Currency         → bare 3-letter ISO 4217 code              (CurrencyJsonConverter)
 *  - Venue            → bare ISO 10383 MIC                       (VenueJsonConverter)
 *
 * Amounts and quantities are STRINGS on the wire, deliberately: a JSON number
 * is an IEEE-754 double in every browser and silently rounds a decimal it
 * cannot represent exactly. We keep them as strings everywhere in the
 * frontend too and only ever parse to `number`/`bigint` at the exact point of
 * arithmetic or display (see `money.pipe.ts`, `quantity.pipe.ts`) — the same
 * discipline the backend applies, so the boundary is symmetric.
 */

/** ISO 4217 currency code, e.g. "INR", "USD", "SGD". */
export type CurrencyCode = string;

/** ISO 10383 Market Identifier Code, e.g. "XNSE", "XNAS". */
export type VenueMic = string;

/**
 * An amount that always carries its own currency. Never coerce `.amount` to
 * a JS `number` for anything but display math — see `money.pipe.ts` for why
 * (locale grouping, INR lakh/crore, no hardcoded symbols).
 */
export interface Money {
  readonly amount: string;
  readonly currency: CurrencyCode;
}

/** Decimal quantity as a string. Fractional (e.g. "0.1") is valid — see AssetClass/manifest.fractionalQuantity. */
export type QuantityValue = string;

/**
 * Canonical instrument identity, opaque on the wire as its round-trippable
 * string form (mirrors `InstrumentKey.ToString()` / `TryParse` in
 * Akshaya.SharedKernel.Instruments):
 *   Equity/ETF/etc: "XNSE:INFY:Equity"
 *   Future:         "XNSE:NIFTY:FUT:2026-01-29"
 *   Option:         "XNSE:NIFTY:OPT:2026-01-29:23000:Call"
 *
 * Used as a URL segment, a SignalR subscription key and a Map key throughout
 * the app — never decompose it by hand outside `parseInstrumentKey` below,
 * or a formatting drift between two call sites will silently stop two keys
 * that should match from matching.
 */
export type InstrumentKey = string;

export type AssetClass =
  | 'equity'
  | 'etf'
  | 'future'
  | 'option'
  | 'index'
  | 'currency'
  | 'commodity'
  | 'bond'
  | 'fund'
  | 'crypto';

export type OptionRight = 'call' | 'put';

/** Parsed, display-friendly view of an `InstrumentKey`. */
export interface ParsedInstrumentKey {
  readonly venue: VenueMic;
  readonly symbol: string;
  readonly assetClass: AssetClass;
  readonly expiry?: string; // ISO date (yyyy-MM-dd)
  readonly strike?: number;
  readonly right?: OptionRight;
}

/**
 * Parses the canonical instrument key string. Mirrors
 * `InstrumentKey.TryParse` on the backend field-for-field; kept as the ONE
 * place that understands the format so every consumer (watchlist, order
 * ticket, positions table) agrees on what "the symbol" means for an option.
 */
export function parseInstrumentKey(key: InstrumentKey): ParsedInstrumentKey | undefined {
  const parts = key.split(':');
  if (parts.length < 3) {
    return undefined;
  }
  const [venue, symbol, kind] = parts;
  if (venue === undefined || symbol === undefined || kind === undefined) {
    return undefined;
  }

  if (kind === 'OPT' && parts.length === 6) {
    const [, , , expiry, strike, right] = parts;
    if (expiry === undefined || strike === undefined || right === undefined) {
      return undefined;
    }
    return {
      venue,
      symbol,
      assetClass: 'option',
      expiry,
      strike: Number(strike),
      right: right.toLowerCase() === 'put' ? 'put' : 'call',
    };
  }

  if (kind === 'FUT' && parts.length === 4) {
    const [, , , expiry] = parts;
    if (expiry === undefined) {
      return undefined;
    }
    return { venue, symbol, assetClass: 'future', expiry };
  }

  return { venue, symbol, assetClass: kind.toLowerCase() as AssetClass };
}

/**
 * Mirrors `Akshaya.SharedKernel.InstrumentDefinition` — everything the
 * platform knows about a tradable instrument, independent of any broker.
 */
export interface InstrumentDefinition {
  readonly key: InstrumentKey;
  readonly name: string;
  readonly currency: CurrencyCode;
  readonly isin?: string;
  readonly figi?: string;
  /** Minimum tradable increment: 1 for most equities, the contract lot for F&O. */
  readonly lotSize: number;
  readonly tickSize: number;
  readonly multiplier: number;
  readonly tradingHoursId: string;
  readonly settlementDays: number;
  readonly isTradable: boolean;
}

/** Short label for a UI chip/breadcrumb, e.g. "NIFTY 23000 CE" or "INFY". */
export function formatInstrumentLabel(key: InstrumentKey): string {
  const parsed = parseInstrumentKey(key);
  if (!parsed) {
    return key;
  }
  if (parsed.assetClass === 'option') {
    return `${parsed.symbol} ${parsed.strike} ${parsed.right === 'call' ? 'CE' : 'PE'}`;
  }
  if (parsed.assetClass === 'future') {
    return `${parsed.symbol} FUT`;
  }
  return parsed.symbol;
}

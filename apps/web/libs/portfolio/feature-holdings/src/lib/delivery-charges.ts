import { parseInstrumentKey } from '@akshaya/shared/models';
import type { BlendedHolding, BrokerHoldingLeg } from '@akshaya/shared/models';
import { calculateTradeCharges, referenceTradeTariff } from '@akshaya/shared/util';

export interface DeliveryTariff {
  readonly brokeragePercent: number | null;
  readonly brokerageCap: number | null;
  readonly dpCharge: number | null;
}

export const DELIVERY_REFERENCE: DeliveryTariff = referenceTradeTariff('delivery');

export interface DeliveryChargeLine {
  readonly label: string;
  readonly basis: string;
  readonly buy: number;
  readonly sell: number;
}

export interface DeliveryEstimate {
  readonly quantity: number;
  readonly invested: number;
  readonly saleValue: number;
  readonly grossPnl: number;
  readonly buyCharges: number;
  readonly sellCharges: number;
  readonly totalCharges: number;
  readonly netProceeds: number;
  readonly netPnl: number;
  readonly returnFraction: number;
  readonly lines: readonly DeliveryChargeLine[];
}

export interface HoldingEstimate {
  readonly value?: DeliveryEstimate;
  readonly unavailable?: string;
}

export type DeliveryTariffs = Readonly<Record<string, DeliveryTariff>>;

const round = (value: number): number => Math.round((value + Number.EPSILON) * 100) / 100;

export function validTariff(tariff: DeliveryTariff): boolean {
  return Object.values(tariff).every(value => value !== null && value >= 0 && Number.isSafeInteger(Math.round(value * 100)))
    && tariff.brokeragePercent! <= 100;
}

function legSalePrice(holding: BlendedHolding, leg: BrokerHoldingLeg): number | undefined {
  const value = leg.currentValue;
  if (value?.currency === holding.currency && Number(value.amount) > 0) {
    return Number(value.amount) / Number(leg.quantity);
  }
  const last = leg.lastPrice ?? holding.lastPrice;
  return last?.currency === holding.currency && Number(last.amount) > 0 ? Number(last.amount) : undefined;
}

export function estimateDelivery(
  holding: BlendedHolding,
  tariffs: DeliveryTariffs,
  includeBuyCharges: boolean,
  sellPrice?: number | null,
): HoldingEstimate {
  const instrument = parseInstrumentKey(holding.instrument);
  const venue = instrument?.venue;
  if (holding.currency !== 'INR' || (venue !== 'XNSE' && venue !== 'XBOM') || instrument?.assetClass !== 'equity') {
    return { unavailable: 'Estimates are available for INR delivery equities on NSE and BSE only.' };
  }
  if (sellPrice !== undefined && (sellPrice === null || !Number.isFinite(sellPrice) || sellPrice < 0.01)) {
    return { unavailable: 'Enter a sell price of at least ₹0.01.' };
  }
  const quantity = Number(holding.quantity);
  if (!holding.legs.length || !Number.isSafeInteger(quantity) || quantity <= 0
    || holding.legs.reduce((sum, leg) => sum + Number(leg.quantity), 0) !== quantity) {
    return { unavailable: 'A complete, positive whole-share quantity and account breakdown are required.' };
  }

  const lines: DeliveryChargeLine[] = [];
  let invested = 0;
  let saleValue = 0;
  const accounts = new Map<string, { buy: number; sell: number }>();
  for (const leg of holding.legs) {
    const tariff = tariffs[leg.brokerLinkId] ?? DELIVERY_REFERENCE;
    if (!validTariff(tariff)) {
      return { unavailable: `Check brokerage and DP assumptions for ${leg.displayName}. Enter non-negative values and a rate no greater than 100%.` };
    }
    const qty = Number(leg.quantity);
    const average = Number(leg.averagePrice.amount);
    const price = sellPrice ?? legSalePrice(holding, leg);
    if (!Number.isSafeInteger(qty) || qty <= 0 || !Number.isFinite(average) || average <= 0
      || leg.averagePrice.currency !== 'INR' || price === undefined || !Number.isFinite(price) || price <= 0) {
      return { unavailable: `Missing or invalid quantity, acquisition cost or valuation for ${leg.displayName}. Refresh holdings or enter a sell price.` };
    }
    const buy = round(qty * average);
    const sell = round(qty * price);
    if (![buy, sell].every(value => value > 0 && Number.isSafeInteger(Math.round(value * 100)))) {
      return { unavailable: 'The holding value is outside the supported calculation range.' };
    }
    invested += buy;
    saleValue += sell;
    const account = accounts.get(leg.brokerLinkId) ?? { buy: 0, sell: 0 };
    accounts.set(leg.brokerLinkId, { buy: round(account.buy + buy), sell: round(account.sell + sell) });
  }
  for (const [id, { buy, sell }] of accounts) {
    const tariff = tariffs[id] ?? DELIVERY_REFERENCE;
    calculateTradeCharges({
      type: 'delivery', exchange: venue, buyValue: buy, sellValue: sell, includeBuyCharges, rounding: 'perSide',
      tariff: { brokeragePercent: tariff.brokeragePercent!, brokerageCap: tariff.brokerageCap!, dpCharge: tariff.dpCharge!, flatBrokerage: 0 },
    }).forEach((line, index) => {
      const previous = lines[index];
      lines[index] = {
        label: line.label, basis: line.basis,
        buy: round((previous?.buy ?? 0) + line.buy), sell: round((previous?.sell ?? 0) + line.sell),
      };
    });
  }
  invested = round(invested);
  saleValue = round(saleValue);
  const buyCharges = round(lines.reduce((sum, line) => sum + line.buy, 0));
  const sellCharges = round(lines.reduce((sum, line) => sum + line.sell, 0));
  const totalCharges = round(buyCharges + sellCharges);
  const grossPnl = round(saleValue - invested);
  const netPnl = round(grossPnl - totalCharges);
  return { value: {
    quantity, invested, saleValue, grossPnl, buyCharges, sellCharges, totalCharges,
    netProceeds: round(saleValue - sellCharges), netPnl,
    returnFraction: netPnl / (invested + buyCharges), lines,
  } };
}

export function deliveryBreakEven(
  holding: BlendedHolding,
  tariffs: DeliveryTariffs,
  includeBuyCharges: boolean,
): number | undefined {
  const base = estimateDelivery(holding, tariffs, includeBuyCharges, 1).value;
  if (!base) return undefined;
  let low = 1;
  let high = Math.ceil((base.invested + base.buyCharges + base.sellCharges) / base.quantity * 200);
  const at = (paise: number) => estimateDelivery(holding, tariffs, includeBuyCharges, paise / 100).value?.netPnl;
  for (let attempt = 0; attempt < 20 && (at(high) ?? -1) < 0; attempt++) high *= 2;
  if (!Number.isSafeInteger(high) || (at(high) ?? -1) < 0) return undefined;
  while (low < high) {
    const mid = Math.floor((low + high) / 2);
    if ((at(mid) ?? -1) >= 0) high = mid;
    else low = mid + 1;
  }
  return high / 100;
}

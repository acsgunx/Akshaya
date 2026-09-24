export type TradeChargeType = 'intraday' | 'delivery' | 'futures' | 'options';
export type TradeExchange = 'XNSE' | 'XBOM';

export const TRADE_CHARGE_TYPES: readonly { value: TradeChargeType; label: string }[] = [
  { value: 'intraday', label: 'Intraday equity' },
  { value: 'delivery', label: 'Delivery equity' },
  { value: 'futures', label: 'F&O · Futures' },
  { value: 'options', label: 'F&O · Options' },
];

export const TRADE_CHARGE_RATES = {
  intraday: { buyStt: 0, sellStt: 0.00025, stamp: 0.00003, nseExchange: 0.0000297, bseExchange: 0.0000375, ipft: 0.000001 },
  delivery: { buyStt: 0.001, sellStt: 0.001, stamp: 0.00015, nseExchange: 0.0000297, bseExchange: 0.0000375, ipft: 0.000001 },
  futures: { buyStt: 0, sellStt: 0.0005, stamp: 0.00002, nseExchange: 0.0000173, bseExchange: 0, ipft: 0.000001 },
  options: { buyStt: 0, sellStt: 0.0015, stamp: 0.00003, nseExchange: 0.0003503, bseExchange: 0.000325, ipft: 0.000005 },
} as const;

export interface TradeTariff {
  readonly brokeragePercent: number;
  readonly brokerageCap: number;
  readonly flatBrokerage: number;
  readonly dpCharge: number;
}

export interface TradeChargeLine {
  readonly label: string;
  readonly basis: string;
  readonly buy: number;
  readonly sell: number;
  readonly total: number;
}

export interface TradeChargeInput {
  readonly type: TradeChargeType;
  readonly exchange: TradeExchange;
  readonly buyValue: number;
  readonly sellValue: number;
  readonly tariff: TradeTariff;
  readonly includeBuyCharges?: boolean;
  readonly rounding?: 'roundTrip' | 'perSide';
}

export interface TradeScenario {
  readonly type: TradeChargeType;
  readonly exchange: TradeExchange;
  readonly buyPrice: number;
  readonly sellPrice: number;
  readonly quantity: number;
  readonly tariff: TradeTariff;
  readonly direction: 'long' | 'short';
}

export interface TradeCalculatorSeed {
  readonly type: TradeChargeType;
  readonly exchange: TradeExchange;
  readonly buyPrice: number | null;
  readonly sellPrice: number | null;
  readonly quantity: number | null;
  readonly direction?: 'long' | 'short';
  readonly label?: string;
}

export const roundCharge = (value: number): number => Math.round((value + Number.EPSILON) * 100) / 100;
const rupees = (value: number): number => Math.round(roundCharge(value));
const percent = (rate: number): string => `${Number((rate * 100).toFixed(5))}%`;

export function referenceTradeTariff(type: TradeChargeType): TradeTariff {
  return { brokeragePercent: type === 'delivery' ? 0 : 0.03, brokerageCap: 20, flatBrokerage: 20, dpCharge: type === 'delivery' ? 13 : 0 };
}

export function calculateTradeCharges(input: TradeChargeInput): readonly TradeChargeLine[] {
  const { type, exchange, tariff } = input;
  const rates = TRADE_CHARGE_RATES[type];
  const buy = input.includeBuyCharges === false ? 0 : input.buyValue;
  const sell = input.sellValue;
  const exchangeRate = exchange === 'XNSE' ? rates.nseExchange : rates.bseExchange;
  const ipftRate = exchange === 'XNSE' ? rates.ipft : 0;
  const split = (label: string, basis: string, buyRaw: number, sellRaw: number, rounding = roundCharge): TradeChargeLine => {
    const buyCharge = rounding(buyRaw);
    const total = input.rounding === 'perSide'
      ? roundCharge(buyCharge + rounding(sellRaw)) : rounding(buyRaw + sellRaw);
    return { label, basis, buy: buyCharge, sell: roundCharge(total - buyCharge), total };
  };
  const brokerage = (value: number): number => value <= 0 ? 0 : roundCharge(type === 'options'
    ? tariff.flatBrokerage : Math.min(value * tariff.brokeragePercent / 100, tariff.brokerageCap));
  const lines = [
    split('Brokerage', type === 'options' ? 'Flat fee per executed order, one order per side' : 'Min(account rate × turnover, cap), one order per side', brokerage(buy), brokerage(sell)),
    split('STT', `${type === 'delivery' ? '0.1% on buy and sell' : `${percent(rates.sellStt)} on sell ${type === 'options' ? 'premium' : 'turnover'}`}; nearest rupee`, buy * rates.buyStt, sell * rates.sellStt, rupees),
    split('Exchange transaction', `${exchange === 'XNSE' ? 'NSE' : 'BSE'} ${percent(exchangeRate)} on both sides, excluding IPFT`, buy * exchangeRate, sell * exchangeRate),
    split('SEBI', '₹10 per crore on both sides', buy * 0.000001, sell * 0.000001),
    split('IPFT', exchange === 'XNSE' ? `NSE ${percent(ipftRate)} on both sides; not charged again in exchange fees` : 'Not applied for BSE', buy * ipftRate, sell * ipftRate),
    split('Stamp duty', `${percent(rates.stamp)} on buy ${type === 'options' ? 'premium' : 'turnover'} only; nearest rupee`, buy * rates.stamp, 0, rupees),
    split('DP charge', type === 'delivery' ? 'One debit per stock/account on delivery sale, before GST' : 'Not applicable to this trade type', 0, type === 'delivery' && sell > 0 ? tariff.dpCharge : 0),
  ];
  const taxable = [lines[0]!, lines[2]!, lines[3]!, lines[4]!, lines[6]!];
  lines.push(split('GST', '18% of brokerage, exchange, SEBI, IPFT and DP',
    taxable.reduce((sum, line) => sum + line.buy, 0) * 0.18,
    taxable.reduce((sum, line) => sum + line.sell, 0) * 0.18));
  return lines;
}

export function estimateTrade(input: TradeScenario) {
  const amounts = [input.buyPrice, input.sellPrice, input.quantity, ...Object.values(input.tariff)];
  if (!TRADE_CHARGE_TYPES.some(type => type.value === input.type)
    || (input.exchange !== 'XNSE' && input.exchange !== 'XBOM')
    || amounts.some(value => typeof value !== 'number' || !Number.isFinite(value) || value < 0)
    || input.buyPrice < 0.01 || input.sellPrice < 0.01
    || !Number.isSafeInteger(input.quantity) || input.quantity <= 0
    || input.tariff.brokeragePercent > 100
    || (input.direction !== 'long' && input.direction !== 'short')
    || (input.type === 'delivery' && input.direction === 'short')) return undefined;
  const buyValue = roundCharge(input.buyPrice * input.quantity);
  const sellValue = roundCharge(input.sellPrice * input.quantity);
  const turnover = roundCharge(buyValue + sellValue);
  if (![turnover, ...Object.values(input.tariff)].every(value => Number.isSafeInteger(Math.round(value * 100)))) return undefined;
  const lines = calculateTradeCharges({ ...input, buyValue, sellValue });
  const buyCharges = roundCharge(lines.reduce((sum, line) => sum + line.buy, 0));
  const sellCharges = roundCharge(lines.reduce((sum, line) => sum + line.sell, 0));
  const totalCharges = roundCharge(buyCharges + sellCharges);
  const grossPnl = roundCharge(sellValue - buyValue);
  if (!Number.isSafeInteger(Math.round((turnover + totalCharges) * 100))) return undefined;
  return {
    buyValue, sellValue, turnover, buyCharges, sellCharges, totalCharges, grossPnl,
    netPnl: roundCharge(grossPnl - totalCharges),
    costPerUnit: totalCharges / input.quantity,
    netSellValue: roundCharge(sellValue - sellCharges), lines,
  };
}

export function tradeBreakEven(input: TradeScenario): number | undefined {
  const estimate = estimateTrade(input);
  if (!estimate) return undefined;
  const short = input.direction === 'short';
  const at = (paise: number) => estimateTrade({ ...input, [short ? 'buyPrice' : 'sellPrice']: paise / 100 })?.netPnl;
  let low = 1;
  let high = Math.ceil((short ? input.sellPrice : input.buyPrice + estimate.costPerUnit + 1) * 100);
  if (short && (at(low) ?? -1) < 0) return undefined;
  if (!short) {
    for (let attempt = 0; attempt < 20 && (at(high) ?? -1) < 0; attempt++) high *= 2;
    if ((at(high) ?? -1) < 0) return undefined;
  }
  if (!Number.isSafeInteger(high)) return undefined;
  while (low < high) {
    const mid = short ? Math.ceil((low + high) / 2) : Math.floor((low + high) / 2);
    if (short) {
      if ((at(mid) ?? -1) >= 0) low = mid;
      else high = mid - 1;
    } else {
      if ((at(mid) ?? -1) >= 0) high = mid;
      else low = mid + 1;
    }
  }
  return low / 100;
}

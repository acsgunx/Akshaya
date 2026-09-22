import type { ChartPriceLevel } from '@akshaya/market/ui-price-chart';
import { sideLabel } from '@akshaya/shared/util';
import type { InstrumentKey, Money, OrderRecord, PortfolioSnapshot } from '@akshaya/shared/models';
import { isOrderStateTerminal, isOrderUnresolved } from '@akshaya/shared/models';

/** One thing the user holds or has resting in this instrument — a sidebar row and, when shown, a line on the chart. */
export interface ExposureRow {
  readonly id: string;
  readonly title: string;
  readonly detail: string;
  readonly price: Money;
  readonly tone: ChartPriceLevel['tone'];
  readonly kind: ChartPriceLevel['kind'];
  /** The screen that owns it, and where its actions live. */
  readonly route: string;
}

const quantity = new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 });

/**
 * Everything the user has in ONE instrument: open positions and holdings at
 * their average price, and working orders at their limit and trigger.
 *
 * Matched on the exact instrument key. An unresolved order is left out — the
 * platform does not know whether the broker has it, and a line on the chart
 * would say it does.
 */
export function exposureRows(
  instrument: InstrumentKey,
  snapshot: PortfolioSnapshot | undefined,
  orders: readonly OrderRecord[],
): readonly ExposureRow[] {
  const rows: ExposureRow[] = [];

  for (const position of snapshot?.positions ?? []) {
    const net = Number(position.netQuantity);
    if (position.instrument !== instrument || !Number.isFinite(net) || net === 0) { continue; }
    rows.push({
      id: `position:${position.groupKey}`,
      title: `${net > 0 ? 'Long' : 'Short'} ${quantity.format(Math.abs(net))}`,
      detail: 'avg price',
      price: position.averagePrice,
      tone: net > 0 ? 'buy' : 'sell',
      kind: 'held',
      route: '/positions',
    });
  }

  for (const holding of snapshot?.holdings ?? []) {
    const held = Number(holding.quantity);
    if (holding.instrument !== instrument || !(held > 0)) { continue; }
    rows.push({
      id: `holding:${holding.groupKey}`,
      title: `Held ${quantity.format(held)}`,
      detail: 'avg cost',
      price: holding.averagePrice,
      tone: 'brand',
      kind: 'held',
      route: '/holdings',
    });
  }

  for (const order of orders) {
    if (order.instrument !== instrument || isOrderStateTerminal(order.state) || isOrderUnresolved(order)) { continue; }
    // What is still resting, not what was first asked for — a half-filled order's line is for the other half.
    const pending = Number(order.pendingQuantity);
    const name = `${sideLabel(order.side)} ${quantity.format(pending > 0 ? pending : Number(order.quantity))}`;
    const tone = order.side === 'buy' ? 'buy' : 'sell';
    const detail = 'working order';
    if (order.limitPrice) {
      rows.push({ id: `limit:${order.id}`, title: `${name} limit`, detail, price: order.limitPrice, tone, kind: 'limit', route: '/orders' });
    }
    if (order.triggerPrice) {
      rows.push({ id: `trigger:${order.id}`, title: `${name} trigger`, detail, price: order.triggerPrice, tone, kind: 'trigger', route: '/orders' });
    }
  }

  return rows;
}

/** The chart's price lines, in the order the sidebar lists them. */
export function exposureLevels(rows: readonly ExposureRow[]): readonly ChartPriceLevel[] {
  return rows.map((row) => ({ price: Number(row.price.amount), title: row.title, tone: row.tone, kind: row.kind }));
}

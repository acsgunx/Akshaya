import { Pipe, PipeTransform } from '@angular/core';

import type { InstrumentKey } from '@akshaya/shared/models';
import { parseInstrumentKey } from '@akshaya/shared/models';

/**
 * Which part of an instrument key to show:
 *
 * - `symbol`  — the ticker a trader recognises: `HDFCBANK`, `NIFTY`.
 * - `detail`  — everything that qualifies it: the venue MIC, plus expiry,
 *               strike and right for a derivative. `XNSE · FUT 29 Sep 2026`.
 */
export type InstrumentPart = 'symbol' | 'detail';

/**
 * Splits the canonical `InstrumentKey` into a headline and a qualifier for
 * narrow layouts.
 *
 * WHY: the key's string form leads with the venue (`XNSE:HDFCBANK:Equity`),
 * so a width-constrained cell truncates it to `XNSE:HD…` — keeping exactly the
 * part every row shares and cutting the part that tells rows apart. On a
 * phone that is every row. Putting the symbol first and the venue in a
 * smaller second line is how every broker's own mobile app reads.
 *
 * Built on `parseInstrumentKey`, never on a hand-rolled `split(':')`, so this
 * and every other consumer agree on what "the symbol" of an option is. An
 * unparseable key falls back to the raw string under `symbol` — showing the
 * whole thing is always better than showing nothing.
 */
@Pipe({ name: 'akInstrument', standalone: true })
export class InstrumentPipe implements PipeTransform {
  /** Only the parts are used — see `formatExpiry`. */
  private static readonly EXPIRY_PARTS = new Intl.DateTimeFormat('en-US', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });

  transform(key: InstrumentKey | null | undefined, part: InstrumentPart = 'symbol'): string {
    if (!key) {
      return '';
    }

    const parsed = parseInstrumentKey(key);
    if (!parsed) {
      return part === 'symbol' ? key : '';
    }

    if (part === 'symbol') {
      return parsed.symbol;
    }

    const contract: string[] = [];
    if (parsed.assetClass === 'future') {
      contract.push('FUT');
    } else if (parsed.assetClass === 'option') {
      if (parsed.strike !== undefined && Number.isFinite(parsed.strike)) {
        contract.push(String(parsed.strike));
      }
      contract.push(parsed.right === 'put' ? 'Put' : 'Call');
    }
    if (parsed.expiry) {
      contract.push(InstrumentPipe.formatExpiry(parsed.expiry));
    }

    return contract.length ? `${parsed.venue} · ${contract.join(' ')}` : parsed.venue;
  }

  /**
   * `2026-09-29` → `29 Sep 2026`, the day-month-year order every Indian and
   * most Asian contract notes use. Assembled from parts because no single
   * locale gives it: `en-GB` has the order but abbreviates September to "Sept"
   * in current ICU, and `en-US` has "Sep" in the wrong order.
   *
   * Formatted in UTC because the expiry is a calendar date, not an instant —
   * reading it in the viewer's zone would show the day before for anyone west
   * of Greenwich.
   */
  private static formatExpiry(isoDate: string): string {
    const date = new Date(`${isoDate}T00:00:00Z`);
    if (Number.isNaN(date.getTime())) {
      return isoDate;
    }
    const parts = InstrumentPipe.EXPIRY_PARTS.formatToParts(date);
    const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? '';
    return `${part('day')} ${part('month')} ${part('year')}`;
  }
}

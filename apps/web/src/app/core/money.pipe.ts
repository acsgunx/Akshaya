import { Pipe, PipeTransform } from '@angular/core';
import type { CurrencyCode, Money } from './models';

/**
 * Formats a `Money` value using ITS OWN currency's conventions, always —
 * never the viewer's browser locale and never a hardcoded symbol table.
 *
 * WHY A LOCALE MAP AT ALL, RATHER THAN JUST `Intl.NumberFormat(undefined,
 * { style: 'currency', currency })`: digit GROUPING is a property of the
 * *locale*, not the currency, in ECMA-402. ₹12,34,567 (lakh/crore grouping)
 * is only what you get from an `en-IN`-family locale; formatting INR with
 * the viewer's own locale (say `en-US`) would silently produce the wrong
 * grouping — ₹1,234,567 — for every Indian trader whose browser isn't set
 * to en-IN. So each currency is paired with the locale whose numbering
 * convention actually belongs to it, and `Intl` supplies the symbol,
 * grouping and decimal precision (JPY's zero minor units, etc.) from that
 * pairing — the pipe itself never spells out "₹" or "$" anywhere.
 *
 * Money.amount arrives as a STRING (see `common.model.ts`) specifically so
 * it survives the wire without float rounding; this is the one place it is
 * finally parsed to a `number`, at the point of display, exactly where the
 * inherent precision loss of a formatted string stops mattering.
 */
@Pipe({ name: 'akMoney', standalone: true })
export class MoneyPipe implements PipeTransform {
  private static readonly LOCALE_BY_CURRENCY: Readonly<Record<string, string>> = {
    INR: 'en-IN', // lakh/crore grouping — see class doc.
    USD: 'en-US',
    SGD: 'en-SG',
    HKD: 'en-HK',
    JPY: 'ja-JP',
    AUD: 'en-AU',
    EUR: 'en-IE',
    GBP: 'en-GB',
    CNY: 'zh-CN',
    CHF: 'de-CH',
    CAD: 'en-CA',
    NZD: 'en-NZ',
    THB: 'th-TH',
    IDR: 'id-ID',
    MYR: 'ms-MY',
  };

  transform(
    value: Money | null | undefined,
    options?: { readonly maximumFractionDigits?: number; readonly signDisplay?: 'auto' | 'always' | 'exceptZero' },
  ): string {
    if (!value) {
      return '—';
    }
    const amount = Number(value.amount);
    if (!Number.isFinite(amount)) {
      return '—';
    }

    const locale = MoneyPipe.localeFor(value.currency);
    try {
      return new Intl.NumberFormat(locale, {
        style: 'currency',
        currency: value.currency,
        maximumFractionDigits: options?.maximumFractionDigits,
        signDisplay: options?.signDisplay ?? 'auto',
      }).format(amount);
    } catch {
      // An unrecognised ISO code (a sandbox/test currency, say) still must
      // render something usable rather than throw inside a live table cell.
      return `${amount.toFixed(options?.maximumFractionDigits ?? 2)} ${value.currency}`;
    }
  }

  private static localeFor(currency: CurrencyCode): string {
    return MoneyPipe.LOCALE_BY_CURRENCY[currency.toUpperCase()] ?? 'en-GB';
  }
}

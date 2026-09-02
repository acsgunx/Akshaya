import { Pipe, PipeTransform } from '@angular/core';
import type { QuantityValue } from './models';

/**
 * Formats a `Quantity` (always a decimal STRING on the wire — see
 * `common.model.ts` for why). Unlike `Money`, a quantity has no currency and
 * therefore no locale-specific grouping convention to honour, so a single
 * neutral locale (`en-US`, grouping only, no currency symbol) is used for
 * everyone; what DOES vary per-instrument is precision, which callers pass
 * explicitly from the instrument's own `fractionalQuantity`/lot-size rules
 * rather than this pipe guessing.
 */
@Pipe({ name: 'akQuantity', standalone: true })
export class QuantityPipe implements PipeTransform {
  transform(value: QuantityValue | null | undefined, options?: { readonly fractional?: boolean }): string {
    if (value === null || value === undefined || value === '') {
      return '—';
    }
    const num = Number(value);
    if (!Number.isFinite(num)) {
      return '—';
    }

    return new Intl.NumberFormat('en-US', {
      maximumFractionDigits: options?.fractional ? 4 : 0,
      minimumFractionDigits: 0,
      useGrouping: true,
    }).format(num);
  }
}

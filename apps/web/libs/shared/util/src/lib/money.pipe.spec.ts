import { MoneyPipe } from './money.pipe';

/**
 * These assertions are about the ONE decision this pipe exists to make:
 * grouping follows the money's own currency, never the viewer's locale.
 * They are written against `Intl` output rather than hardcoded strings
 * wherever the exact glyph (₹ vs ₹, narrow vs regular no-break space) is an
 * ICU detail that legitimately varies between Node/browser versions.
 */
describe('MoneyPipe', () => {
  const pipe = new MoneyPipe();

  it('groups INR in lakhs and crores regardless of the host locale', () => {
    // 1234567 is ₹12,34,567 under en-IN grouping and ₹1,234,567 under en-US.
    expect(pipe.transform({ amount: '1234567', currency: 'INR' })).toContain('12,34,567');
  });

  it('groups USD in thousands', () => {
    expect(pipe.transform({ amount: '1234567', currency: 'USD' })).toContain('1,234,567');
  });

  it('honours the currency’s minor units — JPY has none', () => {
    expect(pipe.transform({ amount: '1500', currency: 'JPY' })).not.toContain('.');
  });

  it('renders an em dash rather than a zero for missing money', () => {
    // A blank P&L cell must never be readable as "0.00" — that is a different fact.
    expect(pipe.transform(undefined)).toBe('—');
    expect(pipe.transform(null)).toBe('—');
    expect(pipe.transform({ amount: 'not-a-number', currency: 'USD' })).toBe('—');
  });

  it('can force a sign so a positive P&L reads as a gain', () => {
    expect(pipe.transform({ amount: '250', currency: 'USD' }, { signDisplay: 'exceptZero' })).toContain('+');
    expect(pipe.transform({ amount: '0', currency: 'USD' }, { signDisplay: 'exceptZero' })).not.toContain('+');
  });

  it('still renders something usable for an unknown currency code', () => {
    // A sandbox/test currency must not throw inside a live table cell.
    expect(pipe.transform({ amount: '10', currency: 'XTS' as never })).toContain('XTS');
  });
});

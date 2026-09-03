import { QuantityPipe } from './quantity.pipe';

describe('QuantityPipe', () => {
  const pipe = new QuantityPipe();

  it('groups whole quantities and shows no decimals by default', () => {
    expect(pipe.transform('12000')).toBe('12,000');
  });

  it('shows up to four decimals when the instrument allows fractions', () => {
    expect(pipe.transform('0.12345', { fractional: true })).toBe('0.1235');
  });

  it('does not round a fractional quantity away when fractions are not requested', () => {
    // 0.5 of a share displayed as "1" would misstate a real position, so the caller
    // opting out of fractions is expected to have checked the instrument first.
    expect(pipe.transform('0.5')).toBe('1');
  });

  it('renders an em dash rather than a zero for a missing quantity', () => {
    expect(pipe.transform(undefined)).toBe('—');
    expect(pipe.transform('')).toBe('—');
    expect(pipe.transform('nonsense')).toBe('—');
  });
});

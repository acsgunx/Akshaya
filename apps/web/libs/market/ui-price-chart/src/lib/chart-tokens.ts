/**
 * Resolves the app's `--ak-*` colour tokens to colours Lightweight Charts can
 * parse, so the chart paints from the stylesheet rather than a second palette.
 *
 * `getComputedStyle(el).getPropertyValue('--ak-buy')` hands back the literal
 * token stream — `light-dark(#1d4ed8, #3b82f6)` — because a custom
 * property's computed value is its substitution value, not a used colour.
 * The library cannot parse that and silently paints the candles near-black.
 * Assigning the var to a REAL colour property on a hidden probe and reading
 * that back forces the resolution; it also means `color-mix()` and any future
 * token syntax resolve for free.
 *
 * One more step is needed: a resolved `color-mix()` comes back as
 * `color(srgb …)`, which the library does not parse either. Anything that is
 * not already `rgb()`/`rgba()` is painted onto a 1px canvas and read back as
 * `rgba()`, and cached, since a theme has only a handful of distinct values.
 */
export class ChartTokens {
  private readonly probe = document.createElement('span');
  private readonly context = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  private readonly cache = new Map<string, string>();

  /** `host` is the chart's own element, so the probe inherits exactly the cascade the chart sits in. */
  constructor(host: HTMLElement) {
    this.probe.style.display = 'none';
    host.appendChild(this.probe);
  }

  /**
   * Current resolved value of one token, or `fallback` when the property is
   * not set at all. The sentinel round trip is how "not set" is told apart
   * from "set to something": an unresolvable `var()` leaves the probe's
   * colour at whatever was assigned before it, so the sentinel going
   * unchanged is the signal that nothing took.
   */
  color(name: string, fallback: string): string {
    const sentinel = 'rgb(1, 2, 3)';
    this.probe.style.color = sentinel;
    this.probe.style.color = `var(${name})`;
    const resolved = getComputedStyle(this.probe).color;
    if (!resolved || resolved === sentinel) { return fallback; }
    if (/^rgba?\(/.test(resolved)) { return resolved; }
    const cached = this.cache.get(resolved);
    if (cached) { return cached; }
    const context = this.context;
    if (!context) { return fallback; }
    context.clearRect(0, 0, 1, 1);
    context.fillStyle = resolved;
    context.fillRect(0, 0, 1, 1);
    const [red = 0, green = 0, blue = 0, alpha = 255] = context.getImageData(0, 0, 1, 1).data;
    const color = `rgba(${red}, ${green}, ${blue}, ${alpha / 255})`;
    this.cache.set(resolved, color);
    return color;
  }
}

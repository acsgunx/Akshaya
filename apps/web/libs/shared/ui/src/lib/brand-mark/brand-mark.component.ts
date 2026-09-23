import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The Akshaya brand mark: an "A" drawn as a candlestick — flat top for the
 * candle body, a wick rising from it, crossbar tilted up like a rising
 * trend — on the violet tile that is also the favicon, the boot splash mark
 * and the install icons (`public/icon.svg` is the same artwork).
 *
 * Inline SVG rather than `<img src="icon.svg">` so the header paints the mark
 * in the first frame instead of after a second request, and so it cannot
 * flash a broken-image icon if the asset fetch races the bundle. Size it
 * from the outside with a `size-*` utility on the host.
 */
@Component({
  selector: 'ak-brand-mark',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-block shrink-0' },
  template: `
    <svg class="block size-full" viewBox="0 0 512 512" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id="akm-bg" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stop-color="#8f35ff" />
          <stop offset="1" stop-color="#4e00ba" />
        </linearGradient>
      </defs>
      <rect width="512" height="512" rx="112" fill="url(#akm-bg)" />
      <path d="M256 96v74" stroke="#fff" stroke-width="30" stroke-linecap="round" fill="none" />
      <path
        d="M150 396L228 196M284 196L362 396M224 196h64"
        stroke="#fff"
        stroke-width="54"
        stroke-linecap="round"
        stroke-linejoin="round"
        fill="none"
      />
      <path d="M202 336l108-24" stroke="#fff" stroke-width="38" stroke-linecap="round" fill="none" />
    </svg>
  `,
})
export class BrandMarkComponent {}

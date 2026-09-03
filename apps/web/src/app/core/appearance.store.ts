import { Injectable, effect, signal } from '@angular/core';

export type ThemePreference = 'dark' | 'light';

const STORAGE_KEY = 'akshaya.appearance';

interface PersistedAppearance {
  readonly theme: ThemePreference;
  readonly cvdSafe: boolean;
}

/**
 * The two viewing preferences the stylesheet has always understood — the
 * `[data-theme]` attribute on `<html>` and the `[data-cvd-safe]` alternate
 * buy/sell pair — held as signals and mirrored onto the document element.
 *
 * DARK IS THE DEFAULT AND `prefers-color-scheme` IS DELIBERATELY IGNORED.
 * A trader's brightness choice for their other apps must not silently flip a
 * trading terminal's chrome underneath them mid-session; this is an explicit
 * preference or it is dark. See DESIGN.md.
 *
 * The colour-blind-safe mode is likewise a setting rather than a media query:
 * there is no platform signal for colour-vision deficiency, so the only
 * honest way to offer the luminance-separated buy/sell pair is to ask.
 */
@Injectable({ providedIn: 'root' })
export class AppearanceStore {
  readonly theme = signal<ThemePreference>('dark');
  readonly cvdSafe = signal(false);

  constructor() {
    this.restore();

    // Zoneless-safe: an effect reading signals and writing to the DOM is
    // exactly the shape the framework schedules for us.
    effect(() => {
      const root = document.documentElement;
      const theme = this.theme();
      const cvdSafe = this.cvdSafe();

      // Dark is the stylesheet's default, so it is the ABSENCE of the
      // attribute rather than a value — one less state to keep in step.
      if (theme === 'light') {
        root.setAttribute('data-theme', 'light');
      } else {
        root.removeAttribute('data-theme');
      }

      if (cvdSafe) {
        root.setAttribute('data-cvd-safe', 'true');
      } else {
        root.removeAttribute('data-cvd-safe');
      }

      this.persist({ theme, cvdSafe });
    });
  }

  toggleTheme(): void {
    this.theme.update((current) => (current === 'dark' ? 'light' : 'dark'));
  }

  setCvdSafe(enabled: boolean): void {
    this.cvdSafe.set(enabled);
  }

  private restore(): void {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) {
        return;
      }
      const parsed = JSON.parse(raw) as Partial<PersistedAppearance>;
      if (parsed.theme === 'light' || parsed.theme === 'dark') {
        this.theme.set(parsed.theme);
      }
      this.cvdSafe.set(parsed.cvdSafe === true);
    } catch {
      // A corrupt or unavailable store (private mode, cleared site data) means
      // the defaults — never a broken shell.
    }
  }

  private persist(value: PersistedAppearance): void {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(value));
    } catch {
      // Preferences that cannot be saved still apply for this session.
    }
  }
}

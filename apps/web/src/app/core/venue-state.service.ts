import { Injectable, Signal, computed, signal } from '@angular/core';
import type { VenueMic } from './models';

export type VenueSessionStatus = 'open' | 'preOpen' | 'closed' | 'afterHours' | 'unknown';

export interface VenueSessionState {
  readonly mic: VenueMic;
  readonly status: VenueSessionStatus;
  /** Wall-clock time AT THE VENUE, formatted in its own timezone — see `venue-clock` for why. */
  readonly localTime: string;
  readonly timeZone: string;
  /** Short human label, e.g. "Market open", "Pre-open", "Closed (weekend)". */
  readonly label: string;
}

interface VenueCalendarEntry {
  readonly timeZone: string;
  /** 24h "HH:mm" in the venue's own timezone. */
  readonly preOpen?: string;
  readonly open: string;
  readonly close: string;
}

/**
 * Trading-calendar lookup for the venues this build ships. NOT exhaustive —
 * new venues are reference data on the backend and the manifest's `venues`
 * list is how a connector declares reach; this table only needs an entry so
 * the UI can render a clock/badge, and falls back to `status: 'unknown'`
 * rather than guessing when a venue isn't in it. Holidays are intentionally
 * out of scope here (belongs in a calendar service call, not a static map)
 * — weekday/session-hours only.
 */
const VENUE_CALENDAR: Readonly<Record<VenueMic, VenueCalendarEntry>> = {
  XNSE: { timeZone: 'Asia/Kolkata', preOpen: '09:00', open: '09:15', close: '15:30' },
  XBOM: { timeZone: 'Asia/Kolkata', preOpen: '09:00', open: '09:15', close: '15:30' },
  XSES: { timeZone: 'Asia/Singapore', open: '09:00', close: '17:00' },
  XNAS: { timeZone: 'America/New_York', preOpen: '04:00', open: '09:30', close: '16:00' },
  XNYS: { timeZone: 'America/New_York', preOpen: '04:00', open: '09:30', close: '16:00' },
  XHKG: { timeZone: 'Asia/Hong_Kong', open: '09:30', close: '16:00' },
  XTKS: { timeZone: 'Asia/Tokyo', open: '09:00', close: '15:00' },
  XASX: { timeZone: 'Australia/Sydney', preOpen: '07:00', open: '10:00', close: '16:00' },
};

/**
 * Per-venue open/closed state, ticking every second. Every trading-critical
 * surface (order ticket header, watchlist, positions) reads this rather than
 * inferring "is the market open" from tick freshness — the two are
 * independent facts (a venue can be open with a degraded feed, or closed
 * with a feed that is technically still connected).
 */
@Injectable({ providedIn: 'root' })
export class VenueStateService {
  private readonly now = signal(new Date());

  constructor() {
    // Zoneless-safe: writing to a signal schedules change detection through
    // Angular's own scheduler regardless of NgZone, so a bare `setInterval`
    // is sufficient here — no `NgZone.runOutsideAngular`/`runInsideAngular`
    // dance required.
    setInterval(() => this.now.set(new Date()), 1000);
  }

  /** A memoised per-venue signal; call once per venue and hold the reference (e.g. in a store). */
  stateFor(mic: VenueMic): Signal<VenueSessionState> {
    const entry = VENUE_CALENDAR[mic.toUpperCase()];
    return computed(() => {
      const at = this.now();
      if (!entry) {
        return { mic, status: 'unknown', localTime: '—', timeZone: '—', label: 'Unknown venue' };
      }

      const parts = new Intl.DateTimeFormat('en-GB', {
        timeZone: entry.timeZone,
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hour12: false,
        weekday: 'short',
      }).formatToParts(at);

      const hh = Number(parts.find((p) => p.type === 'hour')?.value ?? '0');
      const mm = Number(parts.find((p) => p.type === 'minute')?.value ?? '0');
      const weekday = parts.find((p) => p.type === 'weekday')?.value ?? '';
      const localTime = `${pad(hh)}:${pad(mm)}:${parts.find((p) => p.type === 'second')?.value ?? '00'}`;
      const minutesNow = hh * 60 + mm;

      const isWeekend = weekday === 'Sat' || weekday === 'Sun';
      if (isWeekend) {
        return { mic, status: 'closed', localTime, timeZone: entry.timeZone, label: 'Closed (weekend)' };
      }

      const openMin = toMinutes(entry.open);
      const closeMin = toMinutes(entry.close);
      const preOpenMin = entry.preOpen ? toMinutes(entry.preOpen) : undefined;

      if (minutesNow >= openMin && minutesNow < closeMin) {
        return { mic, status: 'open', localTime, timeZone: entry.timeZone, label: 'Market open' };
      }
      if (preOpenMin !== undefined && minutesNow >= preOpenMin && minutesNow < openMin) {
        return { mic, status: 'preOpen', localTime, timeZone: entry.timeZone, label: 'Pre-open' };
      }
      return { mic, status: 'closed', localTime, timeZone: entry.timeZone, label: 'Closed' };
    });
  }
}

function toMinutes(hhmm: string): number {
  const [h, m] = hhmm.split(':').map(Number);
  return (h ?? 0) * 60 + (m ?? 0);
}

function pad(n: number): string {
  return n.toString().padStart(2, '0');
}

import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { debounceTime, distinctUntilChanged, pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import type { InstrumentDefinition } from '../../core/models';

interface State {
  readonly watched: readonly InstrumentDefinition[];
  /**
   * Which linked broker account this watchlist reads through. Required, not
   * cosmetic: instrument search and the tick stream are both answered by one
   * connector's own facets, so with no link there is nothing to search and
   * nothing to price.
   */
  readonly brokerLinkId: string | undefined;
  readonly searchQuery: string;
  readonly searchResults: readonly InstrumentDefinition[];
  readonly searching: boolean;
}

const initialState: State = {
  watched: [],
  brokerLinkId: undefined,
  searchQuery: '',
  searchResults: [],
  searching: false,
};

// v2: v1 persisted the watched list alone, from before the list knew which
// broker link it reads through. An old v1 blob is simply ignored.
const STORAGE_KEY = 'akshaya.watchlist.v2';

interface PersistedWatchlist {
  readonly brokerLinkId?: string;
  readonly watched: readonly InstrumentDefinition[];
}

/** Which instruments a trader has pinned to their watchlist, plus instrument search for adding more. */
export const WatchlistStore = signalStore(
  { providedIn: 'root' },
  withState(() => ({ ...initialState, ...loadPersisted() })),
  withMethods((store, api = inject(ApiService)) => ({
    /** Points the whole list at a linked account. Clears stale results from the previous one. */
    selectBrokerLink(brokerLinkId: string): void {
      if (store.brokerLinkId() === brokerLinkId) {
        return;
      }
      patchState(store, { brokerLinkId, searchResults: [], searching: false });
      persist(brokerLinkId, store.watched());
    },

    search: rxMethod<string>(
      pipe(
        tap((query) => patchState(store, { searchQuery: query })),
        debounceTime(250),
        distinctUntilChanged(),
        switchMap((query) => {
          const brokerLinkId = store.brokerLinkId();
          if (!brokerLinkId || query.trim().length < 1) {
            patchState(store, { searchResults: [], searching: false });
            return [];
          }
          patchState(store, { searching: true });
          return api.searchInstruments(brokerLinkId, query).pipe(
            tapResponse({
              next: (results) => patchState(store, { searchResults: results, searching: false }),
              error: () => patchState(store, { searchResults: [], searching: false }),
            }),
          );
        }),
      ),
    ),

    add(instrument: InstrumentDefinition): void {
      if (store.watched().some((i) => i.key === instrument.key)) {
        return;
      }
      const watched = [...store.watched(), instrument];
      patchState(store, { watched, searchQuery: '', searchResults: [] });
      persist(store.brokerLinkId(), watched);
    },

    remove(key: string): void {
      const watched = store.watched().filter((i) => i.key !== key);
      patchState(store, { watched });
      persist(store.brokerLinkId(), watched);
    },
  })),
);

function loadPersisted(): Partial<State> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return {};
    }
    const parsed = JSON.parse(raw) as PersistedWatchlist;
    return { brokerLinkId: parsed.brokerLinkId, watched: parsed.watched ?? [] };
  } catch {
    // Private browsing / storage disabled — a watchlist that starts empty
    // beats one that throws on load.
    return {};
  }
}

function persist(brokerLinkId: string | undefined, watched: readonly InstrumentDefinition[]): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ brokerLinkId, watched } satisfies PersistedWatchlist));
  } catch {
    // Best-effort only; this is a per-device convenience, not durable state.
  }
}

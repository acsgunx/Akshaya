import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { debounceTime, distinctUntilChanged, pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from '../../core/api.service';
import type { InstrumentDefinition } from '../../core/models';

interface State {
  readonly watched: readonly InstrumentDefinition[];
  readonly searchQuery: string;
  readonly searchResults: readonly InstrumentDefinition[];
  readonly searching: boolean;
}

const initialState: State = {
  watched: [],
  searchQuery: '',
  searchResults: [],
  searching: false,
};

const STORAGE_KEY = 'akshaya.watchlist.v1';

/** Which instruments a trader has pinned to their watchlist, plus instrument search for adding more. */
export const WatchlistStore = signalStore(
  { providedIn: 'root' },
  withState(() => ({ ...initialState, watched: loadPersisted() })),
  withMethods((store, api = inject(ApiService)) => ({
    search: rxMethod<string>(
      pipe(
        tap((query) => patchState(store, { searchQuery: query })),
        debounceTime(250),
        distinctUntilChanged(),
        switchMap((query) => {
          if (query.trim().length < 1) {
            patchState(store, { searchResults: [] });
            return [];
          }
          patchState(store, { searching: true });
          return api.searchInstruments(query).pipe(
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
      persist(watched);
    },

    remove(key: string): void {
      const watched = store.watched().filter((i) => i.key !== key);
      patchState(store, { watched });
      persist(watched);
    },
  })),
);

function loadPersisted(): readonly InstrumentDefinition[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as InstrumentDefinition[]) : [];
  } catch {
    // Private browsing / storage disabled — a watchlist that starts empty
    // beats one that throws on load.
    return [];
  }
}

function persist(watched: readonly InstrumentDefinition[]): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(watched));
  } catch {
    // Best-effort only; this is a per-device convenience, not durable state.
  }
}

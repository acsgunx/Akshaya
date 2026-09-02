import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from './api.service';
import type { ConnectorManifest } from './models';

interface ConnectorState {
  readonly manifests: readonly ConnectorManifest[];
  readonly loading: boolean;
  readonly error: string | undefined;
  readonly loadedAt: number | undefined;
}

const initialState: ConnectorState = {
  manifests: [],
  loading: false,
  error: undefined,
  loadedAt: undefined,
};

/**
 * Holds every `ConnectorManifest` the API knows about. This is the single
 * source of truth the order ticket, the broker-link wizard and the
 * connector catalogue all read from — none of them fetches a manifest on
 * its own, so there is exactly one place a stale/missing manifest could ever
 * come from, and exactly one place a new connector needs the frontend to
 * notice it (nowhere else: it just appears in this store's list).
 */
export const ConnectorStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed(({ manifests }) => ({
    byId: computed(() => new Map(manifests().map((m) => [m.id, m] as const))),
    isEmpty: computed(() => manifests().length === 0),
  })),
  withMethods((store, api = inject(ApiService)) => ({
    load: rxMethod<void>(
      pipe(
        tap(() => patchState(store, { loading: true, error: undefined })),
        switchMap(() =>
          api.getConnectors().pipe(
            tapResponse({
              next: (manifests) => patchState(store, { manifests, loading: false, loadedAt: Date.now() }),
              error: (err: unknown) =>
                patchState(store, {
                  loading: false,
                  error: err instanceof Error ? err.message : 'Could not load connectors.',
                }),
            }),
          ),
        ),
      ),
    ),

    manifestFor(connectorId: string): ConnectorManifest | undefined {
      return store.byId().get(connectorId);
    },
  })),
);

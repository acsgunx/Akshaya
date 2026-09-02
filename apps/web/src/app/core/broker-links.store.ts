import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { pipe, switchMap, tap } from 'rxjs';
import { tapResponse } from '@ngrx/operators';

import { ApiService } from './api.service';
import type { BrokerLink } from './models';

interface State {
  readonly links: readonly BrokerLink[];
  readonly loading: boolean;
  readonly error: string | undefined;
}

const initialState: State = { links: [], loading: false, error: undefined };

/**
 * Every broker account the current user has linked. Deliberately separate
 * from `ConnectorStore`: a `ConnectorManifest` describes a BROKER TYPE
 * ("mStock", capabilities, auth shape); a `BrokerLink` is one specific
 * AUTHENTICATED ACCOUNT for that type, and a user can hold more than one
 * link for the same connector (two mStock accounts, say). Anything that
 * places an order needs a `brokerLinkId` — never a bare `connectorId` — and
 * this store is where that id is resolved back to "which connector's
 * manifest applies", via `manifestConnectorIdFor`.
 */
export const BrokerLinksStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed(({ links }) => ({
    byId: computed(() => new Map(links().map((l) => [l.id, l] as const))),
  })),
  withMethods((store, api = inject(ApiService)) => ({
    load: rxMethod<void>(
      pipe(
        tap(() => patchState(store, { loading: true, error: undefined })),
        switchMap(() =>
          api.getLinks().pipe(
            tapResponse({
              next: (links) => patchState(store, { links, loading: false }),
              error: (err: unknown) =>
                patchState(store, { loading: false, error: err instanceof Error ? err.message : 'Could not load broker links.' }),
            }),
          ),
        ),
      ),
    ),

    linkFor(brokerLinkId: string): BrokerLink | undefined {
      return store.byId().get(brokerLinkId);
    },
  })),
);

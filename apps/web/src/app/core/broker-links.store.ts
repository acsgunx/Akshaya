import { computed, effect, inject } from '@angular/core';
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
  /**
   * Bumped when the set of links CHANGES — never on a load that returns what
   * we already had. Every cached screen (portfolio, blotter, fills) keys its
   * staleness off this number, so a re-load that changed nothing must not
   * make them all refetch.
   */
  readonly revision: number;
  /** Identity of the current set. `undefined` until the first load establishes the baseline. */
  readonly signature: string | undefined;
}

const initialState: State = { links: [], loading: false, error: undefined, revision: 0, signature: undefined };

/**
 * Every broker account the current user has linked. Deliberately separate
 * from `ConnectorStore`: a `ConnectorManifest` describes a BROKER TYPE
 * ("mStock", capabilities, auth shape); a `BrokerLink` is one specific
 * AUTHENTICATED ACCOUNT for that type, and a user can hold more than one
 * link for the same connector (two mStock accounts, say). Anything that
 * places an order needs a `brokerLinkId` — never a bare `connectorId` — and
 * this store is where that id is resolved back to "which connector's
 * manifest applies", via `manifestConnectorIdFor`.
 *
 * It is also the app's ONE announcement point for "the set of linked
 * accounts moved" (see `revision` and `staleOnBrokerLinkChange`): linking an
 * account changes what the portfolio, the blotter and the fills list should
 * be showing, and none of those screens can know that on their own.
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
              next: (links) => {
                const signature = signatureOf(links);
                const known = store.signature();
                patchState(store, {
                  links,
                  loading: false,
                  signature,
                  // The first load is the baseline, not a change: bumping on it
                  // would have every screen discard the data it just fetched at
                  // startup and fetch it again.
                  revision: known !== undefined && known !== signature ? store.revision() + 1 : store.revision(),
                });
              },
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

/**
 * What counts as "the same set of links" for staleness. Identity plus the two
 * flags every screen branches on — a session that expired or an account that
 * was deactivated changes what the portfolio can return just as much as a new
 * account does. Timestamps are excluded on purpose: `lastAuthenticatedAt`
 * moving is not a reason to refetch a portfolio.
 */
function signatureOf(links: readonly BrokerLink[]): string {
  return links.map((l) => `${l.id}:${l.isActive ? 1 : 0}:${l.hasSession ? 1 : 0}`).join('|');
}

/**
 * Calls `markStale` whenever the set of linked accounts changes. Marking, not
 * refetching: the screen that owns the data is usually not the one on screen
 * when a link completes, and firing a portfolio + blotter + fills fetch —
 * each of which fans out to every broker — for screens the user may not open
 * is exactly the cost this app cannot pay. The refetch happens on the next
 * visit, via that store's `ensureFresh()`.
 *
 * Must be called from an injection context (a store's `onInit` hook).
 */
export function staleOnBrokerLinkChange(markStale: () => void): void {
  const links = inject(BrokerLinksStore);
  let seen = links.revision();

  effect(() => {
    const revision = links.revision();
    if (revision !== seen) {
      seen = revision;
      markStale();
    }
  });
}

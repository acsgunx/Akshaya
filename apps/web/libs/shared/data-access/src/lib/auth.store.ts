import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';
import type { HttpErrorResponse } from '@angular/common/http';

import { ApiService } from './api.service';
import type { ApiProblem, RegisterRequest, SavedCredential, SignInRequest, UserProfile } from '@akshaya/shared/models';

interface State {
  readonly user: UserProfile | undefined;
  /** True until the first `/me` settles. The guard waits on it; see `restore()`. */
  readonly restoring: boolean;
  readonly busy: boolean;
  readonly error: ApiProblem | undefined;
  readonly savedCredentials: readonly SavedCredential[];
  /** A `loadSavedCredentials` is in flight; until it answers, an empty list means "not known yet", not "none". */
  readonly savedCredentialsLoading: boolean;
}

const initialState: State = {
  user: undefined,
  restoring: true,
  busy: false,
  error: undefined,
  savedCredentials: [],
  savedCredentialsLoading: false,
};

/**
 * Who is signed in, and which broker logins they have asked us to remember.
 *
 * THE SESSION IS NOT HERE. It is an HttpOnly cookie the browser attaches and
 * this code cannot read, which is the point: nothing in this store, or
 * anywhere else in the bundle, is a credential worth stealing. What lives
 * here is the user's PROFILE — enough to render a name and gate a route —
 * and saved-credential METADATA, which is a list of field keys, never values.
 *
 * `restoring` starts true and the route guard awaits it. Without that, a
 * hard refresh on `/positions` races `/me` and bounces an authenticated user
 * to the sign-in screen for the split second before their cookie is checked.
 */
export const AuthStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed((store) => ({
    isAuthenticated: computed(() => store.user() !== undefined),
    displayName: computed(() => store.user()?.displayName || store.user()?.email || ''),
  })),
  withMethods((store, api = inject(ApiService)) => {
    /**
     * The `/me` probe currently in flight, or undefined when none is.
     *
     * This is what makes `restore()` genuinely safe to call repeatedly, which
     * it always claimed to be and was not. Two callers race it on every boot —
     * `AppComponent.ngOnInit` and `authGuard` — and both read `restoring` as
     * true because neither has answered yet, so both issued their own request.
     * The probe hits the identity database, so that was two connections, two
     * queries and two round trips to learn one thing.
     */
    let inFlight: Promise<void> | undefined;

    /** Resolves once the session is known either way. Safe to call repeatedly. */
    function restore(): Promise<void> {
      return (inFlight ??= probe().finally(() => {
        inFlight = undefined;
      }));
    }

    async function probe(): Promise<void> {
      try {
        const user = await firstValueFrom(api.me());
        patchState(store, { user: user ?? undefined, restoring: false });
      } catch {
        // A failed probe is "not signed in", never a blocking error: the sign-in
        // screen must render even when the API is down, or the user has no way
        // to tell us anything at all.
        patchState(store, { user: undefined, restoring: false });
      }
    }

    return {
      restore,

      async signIn(request: SignInRequest): Promise<boolean> {
        patchState(store, { busy: true, error: undefined });
        try {
          const user = await firstValueFrom(api.signIn(request));
          patchState(store, { user, busy: false });
          return true;
        } catch (err: unknown) {
          patchState(store, { busy: false, error: toProblem(err) });
          return false;
        }
      },

      async register(request: RegisterRequest): Promise<boolean> {
        patchState(store, { busy: true, error: undefined });
        try {
          const user = await firstValueFrom(api.register(request));
          patchState(store, { user, busy: false });
          return true;
        } catch (err: unknown) {
          patchState(store, { busy: false, error: toProblem(err) });
          return false;
        }
      },

      async signOut(): Promise<void> {
        try {
          await firstValueFrom(api.signOut());
        } finally {
          // Clear locally even if the call failed. A user who pressed "sign out"
          // must not be left looking at their own portfolio because the network
          // blipped; the cookie expires server-side regardless.
          patchState(store, { ...initialState, restoring: false });
        }
      },

      async loadSavedCredentials(): Promise<void> {
        patchState(store, { savedCredentialsLoading: true });
        try {
          const savedCredentials = await firstValueFrom(api.getSavedCredentials());
          patchState(store, { savedCredentials, savedCredentialsLoading: false });
        } catch {
          patchState(store, { savedCredentials: [], savedCredentialsLoading: false });
        }
      },

      async deleteSavedCredential(id: string): Promise<void> {
        await firstValueFrom(api.deleteSavedCredential(id));
        patchState(store, { savedCredentials: store.savedCredentials().filter((c) => c.id !== id) });
      },

      /** Saved logins for one connector — what the wizard offers as "use saved login". */
      savedFor(connectorId: string): readonly SavedCredential[] {
        return store.savedCredentials().filter((c) => c.connectorId === connectorId);
      },

      clearError(): void {
        patchState(store, { error: undefined });
      },
    };
  }),
);

function toProblem(err: unknown): ApiProblem {
  const httpErr = err as HttpErrorResponse;
  return (httpErr?.error as ApiProblem) ?? { status: httpErr?.status ?? 0, detail: 'Could not reach the server.' };
}

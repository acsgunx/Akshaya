import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn } from '@angular/router';

import { AuthStore } from './auth.store';

/**
 * Gates every route that shows or touches money.
 *
 * This is CONVENIENCE, NOT SECURITY. The API rejects an unauthenticated call
 * regardless of what the router allows — see the fail-closed fallback policy
 * in the composition root — so the worst a bypassed guard achieves is an
 * empty page full of 401s. Its job is to send a signed-out visitor somewhere
 * useful instead.
 *
 * It awaits `restoring` before deciding. Skipping that wait would bounce an
 * authenticated user to sign-in on every hard refresh, in the moment between
 * the app booting and `/me` answering.
 */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthStore);
  const router = inject(Router);

  if (auth.restoring()) {
    await auth.restore();
  }

  if (auth.isAuthenticated()) {
    return true;
  }

  // Carry where they were going, so signing in lands them there rather than on
  // a generic dashboard — the deep link they clicked is usually the thing they
  // actually wanted.
  return router.createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } });
};

/** Keeps a signed-in user off the sign-in and sign-up screens. */
export const anonymousOnlyGuard: CanActivateFn = async () => {
  const auth = inject(AuthStore);
  const router = inject(Router);

  if (auth.restoring()) {
    await auth.restore();
  }

  return auth.isAuthenticated() ? router.createUrlTree(['/dashboard']) : true;
};

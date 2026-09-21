/**
 * Hand-off from the static splash in `index.html` (`#ak-boot`) to the app.
 *
 * The splash is plain HTML and inline CSS, so it paints before a byte of
 * JavaScript has arrived, and it lives OUTSIDE `<ak-root>` so bootstrapping
 * does not wipe it. It covers three waits, one after the other, as one
 * uninterrupted screen: the bundle downloading, `/me` settling the session
 * (the shell renders nothing useful until then — see `AuthStore.restoring`),
 * and the first route's lazy chunk. Its own markup explains the timed hints.
 */
const SPLASH_ID = 'ak-boot';

/** Fades the splash out and removes it. Safe to call more than once. */
export function dismissBootSplash(): void {
  const splash = document.getElementById(SPLASH_ID);
  if (!splash || splash.dataset['state'] === 'leaving') {
    return;
  }

  splash.dataset['state'] = 'leaving';
  // A timer, not `transitionend`: that never fires when the transition is
  // skipped (reduced motion, a background tab), and a splash stuck over a
  // working app would be far worse than one that leaves a frame early.
  setTimeout(() => splash.remove(), 250);
}

/**
 * Turns the splash into an error with a reload link. Without this, a failed
 * bootstrap is a spinner that never stops — no nav, no kill switch, and no
 * hint that anything is wrong.
 */
export function failBootSplash(): void {
  const splash = document.getElementById(SPLASH_ID);
  if (splash) {
    splash.dataset['state'] = 'failed';
  }
}

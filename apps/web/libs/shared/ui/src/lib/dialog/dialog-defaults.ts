/**
 * The one dialog default the whole app shares, spread into every
 * `MatDialog.open()` config.
 *
 * Dialogs ask for a fixed width (440px for modify/convert, 420px for the
 * confirm) and Material caps that at 80vw — on a 390px phone, a 312px dialog
 * with a type-to-confirm field and two buttons in it. A 16px gutter each side
 * is the most a phone can spare.
 *
 * THIS IS DELIBERATELY NOT `MAT_DIALOG_DEFAULT_OPTIONS`. That token lives in
 * `@angular/material/dialog`, and providing it from the shell's `app.config`
 * means the shell statically imports that entry point — which pins the whole
 * dialog stack, and the ~105kB of CDK overlay under it, into the initial
 * bundle no matter how carefully every other call site lazy-loads it. esbuild
 * places a module in the initial chunk if anything reachable from `main`
 * imports it, so one token import undoes all of it (measured: +28.8kB
 * initial). A plain object costs nothing and is spread at the three call
 * sites instead.
 *
 * Spreading it FIRST also keeps Material's own defaults intact: with no
 * `MAT_DIALOG_DEFAULT_OPTIONS` provided, `MatDialog.open` falls back to
 * `new MatDialogConfig()` and merges the passed config over it, so
 * `closeOnNavigation`, `restoreFocus` and friends still apply — which the
 * old provider had to re-supply by hand.
 */
export const AK_DIALOG_DEFAULTS = { maxWidth: 'calc(100vw - 32px)' } as const;

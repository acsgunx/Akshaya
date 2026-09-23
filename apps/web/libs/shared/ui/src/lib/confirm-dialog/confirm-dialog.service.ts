import { Injectable, Injector, afterNextRender, inject } from '@angular/core';
import { Observable, from, map, switchMap } from 'rxjs';

import { AK_DIALOG_DEFAULTS } from '../dialog/dialog-defaults';
import type { ConfirmDialogComponent, ConfirmDialogData } from './confirm-dialog.component';

/**
 * `MatDialog` plus the CDK overlay, portal, a11y and scrolling code beneath
 * it is ~105kB, and none of it can draw anything at first paint — a
 * confirmation only exists after someone clicks. Loading it here, rather
 * than importing it at the top of this file, is what keeps it out of the
 * initial bundle: the kill switch lives in the app shell, so a static import
 * of `@angular/material/dialog` from this service is a static import from
 * `main`.
 */
function loadDialog() {
  return Promise.all([import('@angular/material/dialog'), import('./confirm-dialog.component')]);
}

/** Thin wrapper so a call site asks one question — "did the user confirm?" — instead of wiring up MatDialog each time. */
@Injectable({ providedIn: 'root' })
export class ConfirmDialogService {
  private readonly injector = inject(Injector);
  private loading?: ReturnType<typeof loadDialog>;

  constructor() {
    // Warmed once the first screen has painted, NOT on the first click. The
    // kill switch is the most consequential button in the app and the one
    // most likely to be reached for when something is already wrong — it must
    // not be the moment the browser discovers it needs a network round-trip.
    // After first render the route's own chunks are already fetched, so this
    // costs nothing anybody is waiting on. Same bargain as the shell's
    // `@defer (on idle)` appearance menu.
    afterNextRender(() => void this.load(), { injector: this.injector });
  }

  confirm(data: ConfirmDialogData): Observable<boolean> {
    return from(this.load()).pipe(
      switchMap(([{ MatDialog }, { ConfirmDialogComponent: dialogComponent }]) =>
        this.injector
          .get(MatDialog)
          .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(dialogComponent, {
            ...AK_DIALOG_DEFAULTS,
            data,
            autoFocus: 'dialog',
            restoreFocus: true,
            width: '420px',
          })
          .afterClosed(),
      ),
      map((result) => result === true),
    );
  }

  /** One in-flight import, shared by the warm-up and every later `confirm()`. */
  private load(): ReturnType<typeof loadDialog> {
    return (this.loading ??= loadDialog());
  }
}

import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Observable, map } from 'rxjs';

import { ConfirmDialogComponent, ConfirmDialogData } from './confirm-dialog.component';

/** Thin wrapper so a call site asks one question — "did the user confirm?" — instead of wiring up MatDialog each time. */
@Injectable({ providedIn: 'root' })
export class ConfirmDialogService {
  private readonly dialog = inject(MatDialog);

  confirm(data: ConfirmDialogData): Observable<boolean> {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data,
      autoFocus: 'dialog',
      restoreFocus: true,
      width: '420px',
    });
    return ref.afterClosed().pipe(map((result) => result === true));
  }
}

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { KillSwitchStore } from '@akshaya/shared/data-access';
import { ConfirmDialogService } from '@akshaya/shared/ui';

/**
 * The global trading halt. Present on every screen (mounted once in
 * `app.component`'s shell, not per-feature) because a trader in the middle
 * of ANY workflow must be able to reach it without navigating away first —
 * that is the entire point of a kill switch.
 *
 * Engaging requires the type-to-confirm dialog (see `confirm-dialog`); a
 * single misclick must not halt every broker link at once. Disengaging is
 * a lighter confirm, matching the backend's own design note that
 * "re-engaging is free" — the asymmetry here is deliberate: it should always
 * be easier to stop trading than to resume it.
 */
@Component({
  selector: 'ak-kill-switch',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.state().isEngaged) {
      <button
        mat-flat-button
        class="ak-btn-danger inline-flex items-center gap-1.5 font-semibold! whitespace-nowrap"
        [disabled]="store.busy()"
        matTooltip="All trading is halted. Click to resume."
        (click)="disengage()"
      >
        <mat-icon aria-hidden="true">block</mat-icon>
        <!--
          Shorter on a phone, where the full label would push the top bar past
          the screen edge — the one place this control must never be.
        -->
        @if (store.busy()) {
          <span>Resuming…</span>
        } @else {
          <span class="lg:hidden">Halted · Resume</span>
          <span class="max-lg:hidden">Trading halted — resume</span>
        }
      </button>
    } @else {
      <button
        mat-stroked-button
        class="inline-flex items-center gap-1.5 font-semibold! whitespace-nowrap"
        [disabled]="store.busy()"
        matTooltip="Immediately stop all new orders across every linked broker."
        (click)="engage()"
      >
        <mat-icon aria-hidden="true">power_settings_new</mat-icon>
        <!--
          Says what it is doing while the halt is in flight. Greyed out alone,
          it reads as "the kill switch is unavailable" — the worst possible
          thing for this button to appear to say.
        -->
        {{ store.busy() ? 'Halting…' : 'Kill switch' }}
      </button>
    }
  `,
  styles: `
    /*
      Material's button gives its leading icon a negative margin sized for its
      own label spacing; the flex gap above already does that job, so the two
      compound into a cramped glyph. Not expressible as a utility: the icon is
      inside Material's own DOM.
    */
    mat-icon {
      flex: none;
      margin: 0;
    }
  `,
})
export class KillSwitchComponent {
  protected readonly store = inject(KillSwitchStore);
  private readonly confirm = inject(ConfirmDialogService);

  engage(): void {
    this.confirm
      .confirm({
        title: 'Halt all trading?',
        message:
          'This immediately blocks every new order across every linked broker, for every user on this tenant, until someone disengages it. Working orders already at a broker are NOT cancelled.',
        confirmLabel: 'Halt trading',
        danger: true,
        typeToConfirm: 'HALT',
      })
      .subscribe((confirmed) => {
        if (confirmed) {
          this.store.engage({ reason: 'Manually engaged from the toolbar.' });
        }
      });
  }

  disengage(): void {
    this.confirm
      .confirm({
        title: 'Resume trading?',
        message: 'New orders will be accepted again across every linked broker.',
        confirmLabel: 'Resume trading',
      })
      .subscribe((confirmed) => {
        if (confirmed) {
          this.store.disengage();
        }
      });
  }
}

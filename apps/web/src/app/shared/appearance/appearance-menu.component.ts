import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { AppearanceStore } from '../../core/appearance.store';

/**
 * The two viewing preferences, in the topbar next to the account link.
 *
 * The colour-blind-safe switch is here rather than buried in a settings page
 * on purpose: it changes what BUY and SELL look like, and someone who needs
 * it needs it before their first order, not after hunting for it.
 */
@Component({
  selector: 'ak-appearance-menu',
  standalone: true,
  imports: [MatButtonModule, MatCheckboxModule, MatIconModule, MatMenuModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      mat-icon-button
      type="button"
      class="ak-focus-halo"
      [matMenuTriggerFor]="menu"
      matTooltip="Appearance"
      aria-label="Appearance settings"
    >
      <mat-icon aria-hidden="true">{{ isDark() ? 'dark_mode' : 'light_mode' }}</mat-icon>
    </button>

    <mat-menu #menu="matMenu" class="ak-appearance-panel">
      <button mat-menu-item type="button" (click)="appearance.toggleTheme()">
        <mat-icon aria-hidden="true">{{ isDark() ? 'light_mode' : 'dark_mode' }}</mat-icon>
        <span>Switch to {{ isDark() ? 'light' : 'dark' }}</span>
      </button>

      <div class="px-3.5 pt-1 pb-2.5">
        <!-- $event.stopPropagation() keeps the menu open: this is a setting the user
             wants to see take effect on the prices behind the menu, not a navigation. -->
        <mat-checkbox
          [checked]="appearance.cvdSafe()"
          (change)="appearance.setCvdSafe($event.checked)"
          (click)="$event.stopPropagation()"
        >
          Colour-blind-safe buy/sell
        </mat-checkbox>
        <p class="mt-1 text-[11px]/[1.4] whitespace-normal text-text-tertiary">
          Swaps the blue/amber pair for cyan and violet, which differ in brightness as well as
          hue.
        </p>
      </div>
    </mat-menu>
  `,
  // NOTE: the menu's contents render in a CDK overlay OUTSIDE this component, so a
  // component-scoped style cannot reach them. Tailwind utilities can, because they
  // are global — which is why the panel's inner spacing is written as classes above.
  // Only the panel's own width still needs a global rule (`.ak-appearance-panel` in
  // styles.scss), since that element is created by Material, not by this template.
  styles: `
    :host {
      display: inline-flex;
    }
  `,
})
export class AppearanceMenuComponent {
  protected readonly appearance = inject(AppearanceStore);

  protected isDark(): boolean {
    return this.appearance.theme() === 'dark';
  }
}

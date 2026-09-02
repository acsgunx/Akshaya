import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { AuthStore } from '../../core/auth.store';
import { ConnectorStore } from '../../core/connector.store';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import type { SavedCredential } from '../../core/models';

/**
 * The account screen: who you are, and which broker logins this platform is
 * holding for you.
 *
 * The saved-login list is deliberately blunt about what is stored. It names
 * the exact fields — "API key, Client code, Password" — because a user
 * deciding whether to keep a saved login needs to know if it includes their
 * password, and a vague "credentials saved" tells them nothing. It cannot
 * show the VALUES: nothing in the browser can, by design.
 */
@Component({
  selector: 'ak-profile',
  standalone: true,
  imports: [DatePipe, RouterLink, MatButtonModule, MatIconModule, EmptyStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './profile.component.html',
  styleUrl: './profile.component.scss',
})
export class ProfileComponent implements OnInit {
  private readonly connectors = inject(ConnectorStore);
  private readonly confirm = inject(ConfirmDialogService);
  protected readonly auth = inject(AuthStore);

  ngOnInit(): void {
    void this.auth.loadSavedCredentials();
  }

  /** The broker's display name, from its manifest. Falls back to the opaque id. */
  protected connectorName(connectorId: string): string {
    return this.connectors.manifestFor(connectorId)?.displayName ?? connectorId;
  }

  /**
   * Turns stored field KEYS into the labels the user saw in the wizard, using
   * the connector's own manifest. A key with no matching manifest field (the
   * broker changed its login since this was saved) falls back to the raw key
   * rather than being hidden — a field we hold and cannot name is exactly the
   * one worth showing.
   */
  protected fieldLabels(credential: SavedCredential): string {
    const fields = this.connectors.manifestFor(credential.connectorId)?.auth.credentialFields ?? [];

    return credential.rememberedKeys
      .map((key) => fields.find((f) => f.key === key)?.label ?? key)
      .join(', ');
  }

  protected async forget(credential: SavedCredential): Promise<void> {
    const name = credential.nickname ?? this.connectorName(credential.connectorId);

    const confirmed = await firstValueFrom(
      this.confirm.confirm({
        title: `Forget the saved login for ${name}?`,
        message:
          'The stored fields are deleted permanently. Any broker account already linked stays linked and keeps '
          + 'working until its session expires — you will just have to type these details again next time.',
        confirmLabel: 'Forget it',
        danger: true,
      }),
    );

    if (confirmed) {
      await this.auth.deleteSavedCredential(credential.id);
    }
  }

  protected async signOut(): Promise<void> {
    await this.auth.signOut();
    window.location.assign('/sign-in');
  }
}

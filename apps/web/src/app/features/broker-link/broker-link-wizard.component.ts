import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router } from '@angular/router';

import { ConnectorStore } from '../../core/connector.store';
import { challengeKindLabel } from '../../core/labels';
import type { AuthCredentials } from '../../core/models';
import { BrokerLinkStore } from './broker-link.store';

/**
 * THE other proof of the architecture: a SINGLE wizard, for every broker,
 * built entirely from `manifest.auth`.
 *
 * Two things make this generic rather than a pile of per-broker `if`s:
 *
 *  1. The credential FORM is built from `manifest.auth.credentialFields` —
 *     whatever fields a broker's manifest declares (a password and an OTP
 *     destination for mStock, a single pasted token for Dhan, nothing at all
 *     for a broker whose first step is an OAuth redirect) are rendered as
 *     however many inputs that list has, with whatever labels/placeholders/
 *     patterns it specifies. Nothing here says "if this is mStock, show a
 *     password field and an OTP field."
 *
 *  2. The FLOW is driven by `AuthStepView`'s four cases — completed /
 *     redirect / challenge / gateway — from `BrokerLinkStore`. A broker
 *     whose login is "type a password, then an OTP" and a broker whose
 *     login is "type a password, then a TOTP" hit the exact same
 *     `challenge` branch below; only the `challengeKind` differs, and that
 *     comes from the API response, not from this component's code.
 */
@Component({
  selector: 'ak-broker-link-wizard',
  standalone: true,
  imports: [DatePipe, ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressSpinnerModule],
  providers: [BrokerLinkStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './broker-link-wizard.component.html',
  styleUrl: './broker-link-wizard.component.scss',
})
export class BrokerLinkWizardComponent {
  private readonly connectorStore = inject(ConnectorStore);
  private readonly router = inject(Router);
  protected readonly store = inject(BrokerLinkStore);
  protected readonly challengeKindLabel = challengeKindLabel;

  readonly connectorId = input.required<string>();

  protected readonly manifest = computed(() => this.connectorStore.manifestFor(this.connectorId()));

  /** Up-to-two-letter monogram for the header avatar, from the broker's display name. */
  protected readonly monogram = computed(() => {
    const name = this.manifest()?.displayName ?? '';
    const words = name.split(/[\s.]+/).filter(Boolean);
    const letters =
      words.length > 1 ? `${words[0]!.charAt(0)}${words[1]!.charAt(0)}` : name.trim().slice(0, 2);
    return letters.toUpperCase() || '?';
  });

  /** Built fresh from the manifest's credential fields — see class doc, point 1. */
  protected credentialForm = new FormGroup<Record<string, FormControl<string>>>({});
  protected readonly nicknameControl = new FormControl<string>('', { nonNullable: true });
  protected readonly challengeResponseControl = new FormControl<string>('', {
    nonNullable: true,
    validators: [Validators.required],
  });
  protected readonly redirectCodeControl = new FormControl<string>('', {
    nonNullable: true,
    validators: [Validators.required],
  });

  private readonly expirySeconds = signal<number | undefined>(undefined);
  protected readonly challengeCountdown = computed(() => this.expirySeconds());

  constructor() {
    effect(() => {
      const manifest = this.manifest();
      if (!manifest) {
        return;
      }
      const group: Record<string, FormControl<string>> = {};
      for (const field of manifest.auth.credentialFields) {
        const validators = [];
        if (!field.optional) {
          validators.push(Validators.required);
        }
        if (field.pattern) {
          validators.push(Validators.pattern(field.pattern));
        }
        group[field.key] = new FormControl<string>('', { nonNullable: true, validators });
      }
      this.credentialForm = new FormGroup(group);
    });

    // Countdown for a challenge's expiry (OTP/TOTP window) — ticks down and
    // is shown so the user knows a code is about to go stale BEFORE they
    // submit it and get a generic "invalid" rejection back.
    effect(() => {
      const step = this.store.step();
      if (step?.type === 'challenge' && step.expiresInSeconds !== undefined) {
        this.expirySeconds.set(step.expiresInSeconds);
      } else {
        this.expirySeconds.set(undefined);
      }
    });

    setInterval(() => {
      const s = this.expirySeconds();
      if (s !== undefined && s > 0) {
        this.expirySeconds.set(s - 1);
      }
    }, 1000);
  }

  /** The single validation message shown under a credential field, or null while it is fine. */
  protected errorFor(key: string, label: string): string | null {
    const control = this.credentialForm.get(key);
    if (!control || control.valid || !(control.touched || control.dirty)) {
      return null;
    }
    if (control.hasError('required')) {
      return `${label} is required.`;
    }
    if (control.hasError('pattern')) {
      return `That does not look like a valid ${label.toLowerCase()}.`;
    }
    return 'Please check this value.';
  }

  protected cancel(): void {
    void this.router.navigate(['/connectors']);
  }

  protected submitCredentials(): void {
    if (this.credentialForm.invalid) {
      this.credentialForm.markAllAsTouched();
      return;
    }
    const credentials = this.credentialForm.getRawValue() as AuthCredentials;
    this.store.begin({
      connectorId: this.connectorId(),
      credentials,
      nickname: this.nicknameControl.value || undefined,
      redirectUri: `${window.location.origin}/connectors/${this.connectorId()}/link`,
    });
  }

  protected submitChallenge(): void {
    const step = this.store.step();
    if (!step || step.type !== 'challenge' || this.challengeResponseControl.invalid) {
      return;
    }
    this.store.continue({ linkId: step.linkId, response: this.challengeResponseControl.value });
  }

  protected openRedirect(): void {
    const step = this.store.step();
    if (step?.type === 'redirect') {
      this.store.rememberFlowState({ state: step.state });
      window.open(step.url, '_blank', 'noopener,noreferrer');
    }
  }

  protected submitRedirectCode(): void {
    const step = this.store.step();
    if (!step || step.type !== 'redirect' || this.redirectCodeControl.invalid) {
      return;
    }
    this.store.continue({ linkId: step.linkId, response: this.redirectCodeControl.value });
  }

  protected recheckGateway(): void {
    const step = this.store.step();
    if (step?.type === 'gateway') {
      // An empty response asks the connector to re-evaluate gateway
      // readiness rather than supplying a challenge answer — the manifest's
      // `gateway` spec (not this component) defines what "ready" means.
      this.store.continue({ linkId: step.linkId, response: '' });
    }
  }

  protected goToConnectors(): void {
    void this.router.navigate(['/connectors']);
  }
}

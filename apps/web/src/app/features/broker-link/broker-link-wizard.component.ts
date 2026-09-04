import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router } from '@angular/router';

import { AuthStore } from '../../core/auth.store';
import { ConnectorStore } from '../../core/connector.store';
import { challengeKindLabel } from '../../core/labels';
import type { AuthCredentials, SavedCredential } from '../../core/models';
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
  imports: [
    DatePipe,
    ReactiveFormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  providers: [BrokerLinkStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './broker-link-wizard.component.html',
  styleUrl: './broker-link-wizard.component.scss',
})
export class BrokerLinkWizardComponent {
  private readonly connectorStore = inject(ConnectorStore);
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthStore);
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

  /** Saved logins this user already has for THIS connector. Drives the "use saved login" panel. */
  protected readonly savedLogins = computed(() =>
    this.auth.savedCredentials().filter((c) => c.connectorId === this.connectorId()),
  );

  /** The saved login in use, or undefined when the user is typing a fresh one. */
  protected readonly selectedSaved = signal<SavedCredential | undefined>(undefined);

  /** Field keys the user ticked "remember" for. */
  private readonly rememberKeys = signal<ReadonlySet<string>>(new Set());

  /**
   * Fields still worth asking for.
   *
   * With a saved login selected, the fields it covers are dropped from the form
   * — the whole point is not retyping them — leaving only what is genuinely
   * missing, which for a daily broker relink is usually just the password.
   */
  protected readonly visibleFields = computed(() => {
    const fields = this.manifest()?.auth.credentialFields ?? [];
    const covered = this.selectedSaved()?.rememberedKeys ?? [];
    return fields.filter((f) => !covered.includes(f.key));
  });

  protected isRemembered(key: string): boolean {
    return this.rememberKeys().has(key);
  }

  protected toggleRemember(key: string, remember: boolean): void {
    const next = new Set(this.rememberKeys());
    if (remember) {
      next.add(key);
    } else {
      next.delete(key);
    }
    this.rememberKeys.set(next);
  }

  protected useSaved(credential: SavedCredential): void {
    this.selectedSaved.set(credential);
    if (credential.nickname) {
      this.nicknameControl.setValue(credential.nickname);
    }
  }

  protected enterManually(): void {
    this.selectedSaved.set(undefined);
  }

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

    // Saved logins for this connector decide whether the form opens as "reuse"
    // or "type it all in", so they have to be loaded before the form is useful.
    void this.auth.loadSavedCredentials();

    // Default every field to "remember" once the manifest arrives. Opt-out rather
    // than opt-in: the user asked for saved logins by coming here, and a wizard
    // where they must tick five boxes to get the feature they wanted is a wizard
    // where they tick none and wonder why nothing was saved. Secrets are still
    // individually untickable, which is the control that matters.
    effect(() => {
      const manifest = this.manifest();
      if (manifest && this.rememberKeys().size === 0 && this.selectedSaved() === undefined) {
        this.rememberKeys.set(new Set(manifest.auth.credentialFields.map((f) => f.key)));
      }
    });
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
    const visible = new Set(this.visibleFields().map((f) => f.key));

    // A field hidden because a saved login already covers it must not be able to
    // block submission with a "required" error for a value the server will fill in.
    const blocking = Object.entries(this.credentialForm.controls)
      .filter(([key, control]) => visible.has(key) && control.invalid);

    if (blocking.length > 0) {
      for (const [, control] of blocking) {
        control.markAsTouched();
      }
      return;
    }

    const raw = this.credentialForm.getRawValue();
    const credentials: Record<string, string> = {};
    for (const key of visible) {
      const value = raw[key];
      if (value) {
        credentials[key] = value;
      }
    }

    const remember = [...this.rememberKeys()].filter((key) => credentials[key]);

    this.store.begin({
      connectorId: this.connectorId(),
      credentials: credentials as AuthCredentials,
      nickname: this.nicknameControl.value || undefined,
      redirectUri: `${window.location.origin}/connectors/${this.connectorId()}/link`,
      savedCredentialId: this.selectedSaved()?.id,
      rememberFields: remember,
    });
  }

  /**
   * Which second factor the user is actually holding.
   *
   * The broker cannot tell us. mStock sends an SMS *or* expects an authenticator code —
   * "if TOTP is enabled, OTP will not be triggered" — and its login response says nothing
   * about which. So the choice is the user's, and it is routed to the matching endpoint by
   * the flow state below.
   */
  protected readonly useAuthenticator = signal(false);

  protected toggleAuthenticator(): void {
    const next = !this.useAuthenticator();
    this.useAuthenticator.set(next);

    // The connector owns this key's meaning (MStockAuth.ChallengeStateKey); the wizard only
    // echoes it back on the continue call. Cleared rather than set to 'sms', because absent
    // means "the connector's default route" and inventing a second vocabulary for the same
    // thing is how these two ends drift apart.
    this.store.rememberFlowState(next ? { challenge: 'totp' } : { challenge: '' });
    this.challengeResponseControl.reset('');
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

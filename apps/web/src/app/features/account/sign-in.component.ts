import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router, RouterLink } from '@angular/router';

import { AuthStore } from '../../core/auth.store';
import { MINIMUM_PASSWORD_LENGTH } from '../../core/models';

/**
 * One component for both sign-in and sign-up.
 *
 * The two forms differ by exactly one field and one button label, and
 * splitting them into two components would duplicate the error handling, the
 * return-url plumbing and the layout for that. `mode` comes from the route.
 */
@Component({
  selector: 'ak-sign-in',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './sign-in.component.html',
  styleUrl: './sign-in.component.scss',
})
export class SignInComponent {
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthStore);

  /** `'sign-in'` or `'register'`, supplied by the route's `data`. */
  readonly mode = input<'sign-in' | 'register'>('sign-in');

  /** Where to land after success. Comes from the guard's `returnUrl` query param. */
  readonly returnUrl = input<string | undefined>(undefined);

  protected readonly isRegister = computed(() => this.mode() === 'register');
  protected readonly minimumPasswordLength = MINIMUM_PASSWORD_LENGTH;
  protected readonly showPassword = signal(false);

  protected readonly form = new FormGroup({
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email],
    }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    displayName: new FormControl('', { nonNullable: true }),
  });

  protected emailError(): string | null {
    const control = this.form.controls.email;
    if (control.valid || !(control.touched || control.dirty)) {
      return null;
    }
    return control.hasError('required') ? 'Enter your email address.' : 'That does not look like an email address.';
  }

  protected passwordError(): string | null {
    const control = this.form.controls.password;
    if (control.valid || !(control.touched || control.dirty)) {
      return null;
    }
    return 'Enter your password.';
  }

  protected async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { email, password, displayName } = this.form.getRawValue();

    // Length is checked here only to save a round trip; the server enforces it
    // regardless and its answer is the one that counts.
    if (this.isRegister() && password.length < MINIMUM_PASSWORD_LENGTH) {
      this.form.controls.password.setErrors({ tooShort: true });
      this.form.controls.password.markAsTouched();
      return;
    }

    const ok = this.isRegister()
      ? await this.auth.register({ email, password, displayName: displayName || undefined })
      : await this.auth.signIn({ email, password });

    if (ok) {
      await this.router.navigateByUrl(this.returnUrl() ?? '/dashboard');
    }
  }
}

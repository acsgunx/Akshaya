/**
 * Mirrors `Akshaya.Api.Contracts.AuthStepDto` and `BrokerLinkDto`.
 *
 * `AuthStepDto` is, per its own doc comment on the backend, "why the wizard
 * is generic": a flat, discriminated-by-`type` payload so the broker-link
 * wizard can `switch` on four cases — completed / redirect / challenge /
 * gateway — and never learn which broker produced them. We model it here as
 * a proper TypeScript discriminated union (narrower than the wire shape) and
 * convert with `toAuthStepView` right where the HTTP response lands, so
 * every component past that boundary gets type-narrowing for free instead of
 * re-checking `.type` and optional-chaining nullable fields everywhere.
 */

import type { ChallengeKind } from './manifest.model';

/** Wire shape exactly as `AuthStepDto` serialises it. */
export interface AuthStepWire {
  readonly type: 'completed' | 'redirect' | 'challenge' | 'gateway';
  readonly linkId: string;
  readonly url?: string;
  readonly state?: string;
  readonly challengeKind?: ChallengeKind;
  readonly prompt?: string;
  readonly maskedDestination?: string;
  readonly expiresInSeconds?: number;
  readonly gatewayId?: string;
  readonly instructions?: string;
  readonly accountId?: string;
  readonly expiresAt?: string; // ISO-8601 instant
}

export interface AuthStepCompleted {
  readonly type: 'completed';
  readonly linkId: string;
  readonly accountId: string;
  readonly expiresAt?: string;
}

export interface AuthStepRedirect {
  readonly type: 'redirect';
  readonly linkId: string;
  readonly url: string;
  readonly state: string;
}

export interface AuthStepChallenge {
  readonly type: 'challenge';
  readonly linkId: string;
  readonly challengeKind: ChallengeKind;
  readonly prompt: string;
  readonly maskedDestination?: string;
  readonly expiresInSeconds?: number;
}

export interface AuthStepGateway {
  readonly type: 'gateway';
  readonly linkId: string;
  readonly gatewayId: string;
  readonly instructions: string;
}

/** The narrowed union the wizard actually switches on. */
export type AuthStepView = AuthStepCompleted | AuthStepRedirect | AuthStepChallenge | AuthStepGateway;

/**
 * The one place that turns the wire's flat, nullable-field shape into a
 * type-narrowed union. Throws on a `type` this build doesn't know, on
 * purpose — an unrecognised auth step reaching the wizard is exactly the
 * "new AuthStep case with no wire representation" scenario the backend's own
 * `AuthStepDto.From` treats as a programmer error, and rendering nothing
 * silently would leave a trader stuck mid-login with no explanation.
 */
export function toAuthStepView(wire: AuthStepWire): AuthStepView {
  switch (wire.type) {
    case 'completed':
      if (!wire.accountId) {
        throw new Error('AuthStepDto: type "completed" without accountId.');
      }
      return { type: 'completed', linkId: wire.linkId, accountId: wire.accountId, expiresAt: wire.expiresAt };
    case 'redirect':
      if (!wire.url || !wire.state) {
        throw new Error('AuthStepDto: type "redirect" without url/state.');
      }
      return { type: 'redirect', linkId: wire.linkId, url: wire.url, state: wire.state };
    case 'challenge':
      if (!wire.challengeKind || !wire.prompt) {
        throw new Error('AuthStepDto: type "challenge" without challengeKind/prompt.');
      }
      return {
        type: 'challenge',
        linkId: wire.linkId,
        challengeKind: wire.challengeKind,
        prompt: wire.prompt,
        maskedDestination: wire.maskedDestination,
        expiresInSeconds: wire.expiresInSeconds,
      };
    case 'gateway':
      if (!wire.gatewayId || !wire.instructions) {
        throw new Error('AuthStepDto: type "gateway" without gatewayId/instructions.');
      }
      return { type: 'gateway', linkId: wire.linkId, gatewayId: wire.gatewayId, instructions: wire.instructions };
  }
}

/** Credentials keyed exactly by the manifest's declared `CredentialField.key`s. */
export type AuthCredentials = Readonly<Record<string, string>>;

export interface BrokerLink {
  readonly id: string;
  readonly connectorId: string;
  readonly nickname?: string;
  readonly isActive: boolean;
  readonly hasSession: boolean;
  readonly sessionExpiresAt?: string;
  readonly createdAt: string;
  readonly lastAuthenticatedAt?: string;
}

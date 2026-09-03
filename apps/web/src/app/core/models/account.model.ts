/**
 * Mirrors `Akshaya.Api.Contracts.UserProfileDto` and `SavedCredentialDto`.
 *
 * NOTE WHAT IS ABSENT. There is no field here for a saved credential's
 * VALUE, and there never will be: the backend has no endpoint that returns
 * one (see `BrokerCredentialVault`'s access rule). The browser learns which
 * field keys are stored — enough to say "we have your API key, you'll still
 * need your password" — and nothing more, so a stolen session cannot be
 * turned into a broker credential dump.
 */

/** The signed-in user. */
export interface UserProfile {
  readonly id: string;
  readonly tenantId: string;
  readonly email: string;
  readonly displayName?: string;
  readonly createdAt: string;
  readonly lastSignedInAt?: string;
}

/** One remembered broker login. Metadata only. */
export interface SavedCredential {
  readonly id: string;
  readonly connectorId: string;
  readonly nickname?: string;
  /** Manifest `credentialFields` keys we hold a value for. Never the values. */
  readonly rememberedKeys: readonly string[];
  readonly updatedAt: string;
  readonly lastUsedAt?: string;
}

export interface RegisterRequest {
  readonly email: string;
  readonly password: string;
  readonly displayName?: string;
}

export interface SignInRequest {
  readonly email: string;
  readonly password: string;
}

/** Mirrors the backend's own minimum; the server re-validates regardless. */
export const MINIMUM_PASSWORD_LENGTH = 10;

namespace Akshaya.Modules.Identity.Domain;

/// <summary>
/// The credential fields a user asked us to remember for one broker account.
///
/// DELIBERATELY OPAQUE. This type knows a connector id and a bag of field keys, and nothing
/// about what any of them mean — the keys come from the connector manifest's
/// <c>credentialFields</c>, the same list the link wizard renders its form from. That is the
/// whole reason "remember my login" works for a broker whose connector does not exist yet: no
/// column here is named after a broker concept, so none has to change when one is added.
///
/// The values never live in this record. <see cref="SealedFields"/> is ciphertext; the
/// plaintext exists only inside <c>BrokerCredentialVault</c>, for the duration of one call.
/// <see cref="RememberedKeys"/> is stored in the clear on purpose, so the UI can say "we have
/// your API key and client code, you'll still need your password" without a decrypt.
/// </summary>
public sealed record SavedBrokerCredential
{
    public required string Id { get; init; }

    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    /// <summary>Which connector these belong to. Opaque — never compared to a literal.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>What the user calls this account. Also the label on the "use saved login" button.</summary>
    public string? Nickname { get; init; }

    /// <summary>
    /// Which manifest field keys are inside <see cref="SealedFields"/>, in the clear.
    ///
    /// Never the values, and never enough to sign in with. Knowing that we hold a value for
    /// "password" is not a secret worth protecting; it is the thing the user most needs to see
    /// before they trust a "Use saved login" button.
    /// </summary>
    public required IReadOnlyList<string> RememberedKeys { get; init; }

    /// <summary>The encrypted field map. Opaque to everything but the cipher.</summary>
    public required SealedSecret SealedFields { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When these credentials were last used to open a broker session.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>True when every field the manifest marks required is remembered.</summary>
    public bool CoversKeys(IEnumerable<string> requiredKeys) =>
        requiredKeys.All(key => RememberedKeys.Contains(key, StringComparer.Ordinal));
}

/// <summary>
/// One envelope-encrypted payload: a data key wrapped by the master key, and the ciphertext
/// that data key protects.
///
/// Envelope rather than encrypting straight under the master key so that rotating the master
/// key rewraps N small data keys instead of re-encrypting every credential blob, and so that a
/// leaked data key compromises exactly one record. <see cref="KeyId"/> names the master key
/// that wrapped it, which is what makes a rotation resumable — records carry the key they were
/// sealed with rather than the system assuming there is only ever one.
/// </summary>
/// <param name="KeyId">Identifier of the master key this record was sealed under.</param>
/// <param name="WrappedDataKey">The per-record data key, encrypted under the master key.</param>
/// <param name="Payload">The ciphertext, encrypted under the data key.</param>
public sealed record SealedSecret(string KeyId, byte[] WrappedDataKey, byte[] Payload);

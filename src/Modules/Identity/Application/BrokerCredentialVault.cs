using System.Text;
using System.Text.Json;
using Akshaya.Modules.Identity.Domain;
using Akshaya.Modules.Identity.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Identity.Application;

/// <summary>What a caller may know about a saved login without being handed the secrets.</summary>
/// <param name="Id">Record id, used to ask for the real thing.</param>
/// <param name="ConnectorId">Opaque connector id.</param>
/// <param name="Nickname">User's label for the account.</param>
/// <param name="RememberedKeys">Which manifest field keys are stored.</param>
/// <param name="UpdatedAt">When the record was last written.</param>
/// <param name="LastUsedAt">When it was last used to open a session.</param>
public sealed record SavedCredentialSummary(
    string Id,
    string ConnectorId,
    string? Nickname,
    IReadOnlyList<string> RememberedKeys,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// The credential vault: encrypt on the way in, decrypt on the way out, and never let the
/// plaintext travel anywhere it does not have to.
///
/// THE ACCESS RULE, and it is the only one that matters here: <see cref="RevealAsync"/> is the
/// single method that returns plaintext, and the API never exposes it. Secrets go from this
/// vault into a connector's login call inside the same request; they never go to the browser,
/// not even to the browser that saved them. A "show me my saved password" endpoint would turn
/// one stolen session cookie into every broker credential the user owns, so there isn't one —
/// the UI shows which FIELDS are saved (<see cref="SavedCredentialSummary.RememberedKeys"/>)
/// and nothing else.
///
/// Field keys are whatever the connector manifest declared. This class never inspects them.
/// </summary>
public sealed class BrokerCredentialVault(
    ISavedCredentialStore store,
    ICredentialCipher cipher,
    IClock clock,
    ILogger<BrokerCredentialVault> logger)
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.General);

    /// <summary>Everything this user has saved, secrets excluded by construction.</summary>
    public async Task<IReadOnlyList<SavedCredentialSummary>> ListAsync(
        string userId,
        CancellationToken ct = default)
    {
        var records = await store.ListForUserAsync(userId, ct);
        return [.. records.Select(Summarise)];
    }

    /// <summary>Saved logins for one connector, secrets excluded.</summary>
    public async Task<IReadOnlyList<SavedCredentialSummary>> ListForConnectorAsync(
        string userId,
        string connectorId,
        CancellationToken ct = default)
    {
        var records = await store.ListForConnectorAsync(userId, connectorId, ct);
        return [.. records.Select(Summarise)];
    }

    /// <summary>
    /// Stores the subset of a login the user asked us to remember.
    ///
    /// <paramref name="fields"/> is already filtered by the caller to the fields whose
    /// "remember this" toggle was on — the vault stores exactly what it is given and does not
    /// second-guess which fields are sensitive, because it cannot know: a field this platform
    /// would consider harmless is a bearer token at some broker.
    ///
    /// Re-saving for the same connector and nickname REPLACES the record rather than adding a
    /// second one, so a user correcting a typo does not end up with two saved logins one of
    /// which no longer works.
    /// </summary>
    public async Task<Result<SavedCredentialSummary>> SaveAsync(
        string tenantId,
        string userId,
        string connectorId,
        string? nickname,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);

        // Blank values are an unchecked box, not a value worth a row.
        var kept = fields
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

        if (kept.Count == 0)
        {
            return Result<SavedCredentialSummary>.Failure(
                IdentityErrors.InvalidRequest("Nothing was selected to remember."));
        }

        var now = clock.UtcNow;
        var trimmedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();

        var existing = (await store.ListForConnectorAsync(userId, connectorId, ct))
            .FirstOrDefault(c => string.Equals(c.Nickname, trimmedNickname, StringComparison.Ordinal));

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(kept, PayloadJson);
        SealedSecret sealedFields;
        try
        {
            sealedFields = cipher.Seal(plaintext);
        }
        finally
        {
            // The serialised secrets are done with; do not leave them in a heap buffer waiting
            // for a GC that may never zero it.
            Array.Clear(plaintext);
        }

        var record = new SavedBrokerCredential
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            UserId = userId,
            ConnectorId = connectorId,
            Nickname = trimmedNickname,
            RememberedKeys = [.. kept.Keys.Order(StringComparer.Ordinal)],
            SealedFields = sealedFields,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            LastUsedAt = existing?.LastUsedAt,
        };

        await store.SaveAsync(record, ct);

        logger.LogInformation(
            "Saved {FieldCount} credential field(s) for connector {ConnectorId} on account {UserId}.",
            record.RememberedKeys.Count,
            connectorId,
            userId);

        return Result<SavedCredentialSummary>.Success(Summarise(record));
    }

    /// <summary>
    /// THE ONLY METHOD THAT RETURNS PLAINTEXT. Callers must use the result to make a broker
    /// login call and must not put it in a response, a log or an audit record.
    ///
    /// Ownership is re-checked here rather than trusted from the caller: this is the one door
    /// to the secrets, so it is the one place the check must not be possible to forget.
    /// </summary>
    public async Task<Result<IReadOnlyDictionary<string, string>>> RevealAsync(
        string userId,
        string credentialId,
        CancellationToken ct = default)
    {
        var record = await store.GetAsync(credentialId, ct);

        // An id belonging to someone else is reported as "not found", never as "forbidden":
        // confirming that a record exists is its own small leak.
        if (record is null || !string.Equals(record.UserId, userId, StringComparison.Ordinal))
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(IdentityErrors.CredentialNotFound());
        }

        if (!cipher.TryUnseal(record.SealedFields, out var plaintext))
        {
            logger.LogError(
                "Saved credential {CredentialId} could not be decrypted (sealed under key {KeyId}). "
                + "The master key is missing, wrong, or was rotated away.",
                record.Id,
                record.SealedFields.KeyId);

            return Result<IReadOnlyDictionary<string, string>>.Failure(IdentityErrors.CredentialUnreadable());
        }

        try
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, PayloadJson);
            if (fields is null)
            {
                return Result<IReadOnlyDictionary<string, string>>.Failure(IdentityErrors.CredentialUnreadable());
            }

            await store.SaveAsync(record with { LastUsedAt = clock.UtcNow }, ct);
            return Result<IReadOnlyDictionary<string, string>>.Success(fields);
        }
        catch (JsonException ex)
        {
            // Authenticated but unreadable: the payload decrypted under the right key and then
            // failed to parse, which means the payload FORMAT changed, not that anyone tampered.
            logger.LogError(ex, "Saved credential {CredentialId} decrypted but did not parse.", record.Id);
            return Result<IReadOnlyDictionary<string, string>>.Failure(IdentityErrors.CredentialUnreadable());
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    public async Task<Result> DeleteAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        var record = await store.GetAsync(credentialId, ct);

        if (record is null || !string.Equals(record.UserId, userId, StringComparison.Ordinal))
        {
            return Result.Failure(IdentityErrors.CredentialNotFound());
        }

        await store.RemoveAsync(credentialId, ct);
        logger.LogInformation("Deleted saved credential {CredentialId} for account {UserId}.", credentialId, userId);

        return Result.Success();
    }

    private static SavedCredentialSummary Summarise(SavedBrokerCredential record) => new(
        record.Id,
        record.ConnectorId,
        record.Nickname,
        record.RememberedKeys,
        record.UpdatedAt,
        record.LastUsedAt);
}

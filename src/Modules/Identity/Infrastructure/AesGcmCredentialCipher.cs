using System.Security.Cryptography;
using Akshaya.Modules.Identity.Domain;
using Akshaya.Modules.Identity.Ports;
using Microsoft.Extensions.Options;

namespace Akshaya.Modules.Identity.Infrastructure;

/// <summary>
/// Master keys, from configuration.
///
/// A DICTIONARY, not a single key, because rotation needs both the new key and the old one at
/// the same time: records sealed yesterday must keep opening while new records are sealed
/// under today's key. <see cref="ActiveKeyId"/> says which one seals; every key in
/// <see cref="Keys"/> can unseal.
/// </summary>
public sealed class CredentialProtectionOptions
{
    public const string SectionName = "CredentialProtection";

    /// <summary>Key id used to seal new records. Must be present in <see cref="Keys"/>.</summary>
    public string ActiveKeyId { get; set; } = string.Empty;

    /// <summary>Key id to base64 of 32 raw bytes. Supply via env vars or a secret store — never appsettings.json.</summary>
    public IDictionary<string, string> Keys { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// AES-256-GCM envelope encryption.
///
/// Per record: a fresh 256-bit data key encrypts the payload; the master key encrypts the data
/// key. Rotating the master key then rewraps N 32-byte data keys instead of re-encrypting every
/// credential blob, and a data key that leaks costs exactly one record.
///
/// GCM, not CBC: these payloads are read back and fed into a broker login, so they must be
/// authenticated. Unauthenticated ciphertext is malleable, and a credential blob an attacker
/// can flip bits in is a credential blob they can steer. A failed tag is a hard "no" here.
///
/// The nonce is 12 random bytes per operation and is stored ahead of the tag and ciphertext in
/// one buffer. Random-per-operation is safe because each data key encrypts exactly ONE payload
/// — the birthday bound that makes random GCM nonces dangerous needs billions of messages
/// under a single key, and no key here ever sees a second message.
///
/// Layout of both <c>WrappedDataKey</c> and <c>Payload</c>: <c>nonce(12) || tag(16) || ciphertext</c>.
/// </summary>
public sealed class AesGcmCredentialCipher : ICredentialCipher
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;
    private readonly byte[] _activeKey;

    public AesGcmCredentialCipher(IOptions<CredentialProtectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        if (string.IsNullOrWhiteSpace(value.ActiveKeyId))
        {
            throw new InvalidOperationException(
                $"{CredentialProtectionOptions.SectionName}:ActiveKeyId is not configured. Saved broker "
                + "credentials cannot be encrypted without a master key.");
        }

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, material) in value.Keys)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(material);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"{CredentialProtectionOptions.SectionName}:Keys:{id} is not valid base64.", ex);
            }

            if (bytes.Length != KeyBytes)
            {
                throw new InvalidOperationException(
                    $"{CredentialProtectionOptions.SectionName}:Keys:{id} must decode to exactly "
                    + $"{KeyBytes} bytes (AES-256); got {bytes.Length}.");
            }

            keys[id] = bytes;
        }

        if (!keys.TryGetValue(value.ActiveKeyId, out var activeKey))
        {
            throw new InvalidOperationException(
                $"{CredentialProtectionOptions.SectionName}:ActiveKeyId is '{value.ActiveKeyId}' but no "
                + "key with that id was configured.");
        }

        _keys = keys;
        _activeKeyId = value.ActiveKeyId;
        _activeKey = activeKey;
    }

    public SealedSecret Seal(ReadOnlySpan<byte> plaintext)
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            var payload = Encrypt(dataKey, plaintext);
            var wrapped = Encrypt(_activeKey, dataKey);
            return new SealedSecret(_activeKeyId, wrapped, payload);
        }
        finally
        {
            // The data key has done its job; do not leave it sitting in a pooled heap buffer.
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public bool TryUnseal(SealedSecret sealedSecret, out byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(sealedSecret);
        plaintext = [];

        if (!_keys.TryGetValue(sealedSecret.KeyId, out var masterKey))
        {
            // Sealed under a key this deployment no longer holds. Not tampering — a rotation
            // that dropped the old key too early.
            return false;
        }

        byte[]? dataKey = null;
        try
        {
            if (!TryDecrypt(masterKey, sealedSecret.WrappedDataKey, out dataKey)
                || dataKey.Length != KeyBytes)
            {
                return false;
            }

            return TryDecrypt(dataKey, sealedSecret.Payload, out plaintext);
        }
        finally
        {
            if (dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
    }

    private static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var buffer = new byte[NonceBytes + TagBytes + plaintext.Length];
        var nonce = buffer.AsSpan(0, NonceBytes);
        var tag = buffer.AsSpan(NonceBytes, TagBytes);
        var ciphertext = buffer.AsSpan(NonceBytes + TagBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return buffer;
    }

    private static bool TryDecrypt(byte[] key, byte[] buffer, out byte[] plaintext)
    {
        plaintext = [];

        if (buffer.Length < NonceBytes + TagBytes)
        {
            return false;
        }

        var nonce = buffer.AsSpan(0, NonceBytes);
        var tag = buffer.AsSpan(NonceBytes, TagBytes);
        var ciphertext = buffer.AsSpan(NonceBytes + TagBytes);
        var output = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, output);
        }
        catch (CryptographicException)
        {
            // Authentication failed: wrong key, or the record was altered. Both are "cannot
            // read this", and neither should be distinguishable to a caller.
            CryptographicOperations.ZeroMemory(output);
            return false;
        }

        plaintext = output;
        return true;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using Akshaya.Modules.Identity.Ports;

namespace Akshaya.Modules.Identity.Infrastructure;

/// <summary>
/// PBKDF2-HMAC-SHA256, with the parameters stored alongside every hash.
///
/// PBKDF2 rather than Argon2id, which is the better algorithm, purely because PBKDF2 is in the
/// framework and Argon2 is not: a password hash is the last place to take a transitive package
/// dependency, and the format below is versioned precisely so swapping the algorithm later is
/// a new prefix plus a re-hash on next sign-in, not a migration.
///
/// The iteration count follows OWASP's 2023 guidance for PBKDF2-HMAC-SHA256 (600,000). It is
/// stored per-hash, so raising it does not invalidate anything: <see cref="Verify"/> reports
/// <c>NeedsRehash</c> and the sign-in path upgrades the record while it still has the
/// plaintext in hand.
///
/// Format: <c>pbkdf2-sha256$iterations$base64(salt)$base64(subkey)</c>.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const char Separator = '$';
    private const int SaltBytes = 16;
    private const int SubkeyBytes = 32;

    /// <summary>Current cost. Anything hashed below this is flagged for rehash on next sign-in.</summary>
    public const int CurrentIterations = 600_000;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher() : this(CurrentIterations)
    {
    }

    /// <param name="iterations">Overridable so tests do not pay 600k iterations per case.</param>
    public Pbkdf2PasswordHasher(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _iterations = iterations;
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var subkey = Derive(password, salt, _iterations);

        return string.Join(
            Separator,
            Prefix,
            _iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(subkey));
    }

    public PasswordVerification Verify(string password, string hash)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (string.IsNullOrEmpty(hash))
        {
            return new PasswordVerification(false, false);
        }

        var parts = hash.Split(Separator);
        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
        {
            // An unrecognised format is a failed verification, never an exception: a corrupt or
            // hand-edited row must not be able to crash the sign-in endpoint for everyone.
            return new PasswordVerification(false, false);
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return new PasswordVerification(false, false);
        }

        var actual = Derive(password, salt, iterations, expected.Length);

        // Fixed-time, so a wrong password cannot be narrowed down byte by byte from timing.
        var isValid = CryptographicOperations.FixedTimeEquals(actual, expected);

        return new PasswordVerification(isValid, isValid && iterations < _iterations);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = SubkeyBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
}

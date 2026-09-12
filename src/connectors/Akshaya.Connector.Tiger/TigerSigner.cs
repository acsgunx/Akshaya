using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Tiger's request signature, and the signature it puts on every answer.
///
/// THE SCHEME IS SHA1withRSA, PKCS#1 v1.5, base64 — Tiger's <c>sign_type: RSA</c>. SHA-1 is weak against collision
/// attacks and would be the wrong choice for anything designed today; it is what Tiger's gateway verifies, so it is
/// what this sends. The signature covers every request parameter, so a forged request would have to be signed with
/// the account's own private key.
///
/// THE CONTENT IS THE PARAMETERS, SORTED BY NAME, joined as <c>k=v</c> with <c>&amp;</c> between them — the whole
/// request including the <c>biz_content</c> string, and nothing else. The same string that was signed is what gets
/// sent, so the gateway rebuilds it from the body it received.
/// </summary>
internal static class TigerSigner
{
    /// <summary>The exact string a request's signature covers.</summary>
    public static string SignContent(IReadOnlyDictionary<string, string> parameters)
    {
        var builder = new StringBuilder();

        foreach (var (key, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(key).Append('=').Append(value);
        }

        return builder.ToString();
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA1withRSA is the signature Tiger's gateway verifies; the algorithm is the vendor's, not a choice.")]
    public static Result<string> Sign(string privateKey, string content, Encoding encoding)
    {
        try
        {
            using var rsa = CreatePrivateKey(privateKey);
            var signature = rsa.SignData(encoding.GetBytes(content), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signature);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "The Tiger private key could not be read. Paste the PEM key whose public half is registered on the "
                + "Tiger console — PKCS#1 or PKCS#8, with or without its BEGIN and END lines.",
                ex.GetType().Name,
                ex.Message));
        }
    }

    /// <summary>
    /// Whether an answer really came from Tiger. The gateway signs the request's own timestamp with Tiger's key, so
    /// verifying it proves the answer came from something holding that key.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA1withRSA is the signature Tiger produces; verifying it is not a choice of algorithm.")]
    public static bool Verify(string publicKey, string content, string signature, Encoding encoding)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Strip(publicKey)), out _);

            return rsa.VerifyData(
                encoding.GetBytes(content),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA1,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Accepts a PKCS#8 or PKCS#1 key, with or without PEM armour.</summary>
    private static RSA CreatePrivateKey(string privateKey)
    {
        var key = privateKey.Trim();
        var rsa = RSA.Create();

        try
        {
            if (key.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                rsa.ImportFromPem(key);
                return rsa;
            }

            var bytes = Convert.FromBase64String(Strip(key));

            try
            {
                rsa.ImportPkcs8PrivateKey(bytes, out _);
            }
            catch (CryptographicException)
            {
                rsa.ImportRSAPrivateKey(bytes, out _);
            }

            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static string Strip(string key) => key
        .Replace("-----BEGIN PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
        .Replace("-----END PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
        .Replace("-----BEGIN RSA PRIVATE KEY-----", string.Empty, StringComparison.Ordinal)
        .Replace("-----END RSA PRIVATE KEY-----", string.Empty, StringComparison.Ordinal)
        .Replace("-----BEGIN PRIVATE KEY-----", string.Empty, StringComparison.Ordinal)
        .Replace("-----END PRIVATE KEY-----", string.Empty, StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Trim();
}

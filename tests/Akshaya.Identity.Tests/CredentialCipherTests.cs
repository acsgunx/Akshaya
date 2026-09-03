using System.Security.Cryptography;
using System.Text;
using Akshaya.Modules.Identity.Domain;
using Akshaya.Modules.Identity.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Akshaya.Identity.Tests;

/// <summary>
/// PREVENTS: the failures that make "encrypted at rest" a claim rather than a fact — a payload
/// that opens under the wrong key, a payload an attacker can edit undetected, and a rotation
/// that silently destroys every record sealed before it.
/// </summary>
public sealed class CredentialCipherTests
{
    private const string KeyA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string KeyB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";

    private static AesGcmCredentialCipher Cipher(string activeKeyId, params (string Id, string Material)[] keys) =>
        new(Options.Create(new CredentialProtectionOptions
        {
            ActiveKeyId = activeKeyId,
            Keys = keys.ToDictionary(k => k.Id, k => k.Material, StringComparer.Ordinal),
        }));

    [Fact]
    public void A_sealed_payload_round_trips()
    {
        var cipher = Cipher("k1", ("k1", KeyA));
        var plaintext = Encoding.UTF8.GetBytes("""{"api_key":"secret","password":"hunter2"}""");

        var sealedSecret = cipher.Seal(plaintext);

        cipher.TryUnseal(sealedSecret, out var opened).Should().BeTrue();
        opened.Should().Equal(plaintext);
    }

    [Fact]
    public void The_ciphertext_does_not_contain_the_plaintext()
    {
        var cipher = Cipher("k1", ("k1", KeyA));

        var sealedSecret = cipher.Seal(Encoding.UTF8.GetBytes("MY-BROKER-API-KEY"));

        Encoding.UTF8.GetString(sealedSecret.Payload).Should().NotContain("MY-BROKER-API-KEY");
    }

    [Fact]
    public void The_same_plaintext_seals_differently_every_time()
    {
        // A fresh data key and nonce per record. Identical ciphertext for identical input
        // would tell an attacker with read access which users share a credential.
        var cipher = Cipher("k1", ("k1", KeyA));
        var plaintext = Encoding.UTF8.GetBytes("the same secret");

        var first = cipher.Seal(plaintext);
        var second = cipher.Seal(plaintext);

        first.Payload.Should().NotEqual(second.Payload);
        first.WrappedDataKey.Should().NotEqual(second.WrappedDataKey);
    }

    [Fact]
    public void A_payload_sealed_under_a_different_key_does_not_open()
    {
        var sealedUnderA = Cipher("k1", ("k1", KeyA)).Seal(Encoding.UTF8.GetBytes("secret"));

        // Same key ID, different key material — the attacker-with-their-own-key case.
        var impostor = Cipher("k1", ("k1", KeyB));

        impostor.TryUnseal(sealedUnderA, out _).Should().BeFalse();
    }

    [Fact]
    public void A_payload_whose_key_is_no_longer_configured_does_not_open()
    {
        var sealedUnderOld = Cipher("old", ("old", KeyA)).Seal(Encoding.UTF8.GetBytes("secret"));

        // A rotation that dropped the old key too early. Must report "cannot read", not throw.
        var rotated = Cipher("new", ("new", KeyB));

        rotated.TryUnseal(sealedUnderOld, out _).Should().BeFalse();
    }

    [Fact]
    public void A_rotation_that_keeps_the_old_key_can_still_open_old_records()
    {
        // THE test that makes rotation safe: seal under the old key, rotate the active key,
        // and old records keep opening because the old key is still in the set.
        var before = Cipher("k1", ("k1", KeyA));
        var sealedSecret = before.Seal(Encoding.UTF8.GetBytes("secret"));

        var after = Cipher("k2", ("k1", KeyA), ("k2", KeyB));

        after.TryUnseal(sealedSecret, out var opened).Should().BeTrue();
        Encoding.UTF8.GetString(opened).Should().Be("secret");

        // And new records are sealed under the NEW key.
        after.Seal(Encoding.UTF8.GetBytes("newer")).KeyId.Should().Be("k2");
    }

    [Fact]
    public void A_tampered_payload_does_not_open()
    {
        // AES-GCM authenticates. Without it the ciphertext is malleable and an attacker with
        // write access to the database could steer what gets sent to a broker.
        var cipher = Cipher("k1", ("k1", KeyA));
        var sealedSecret = cipher.Seal(Encoding.UTF8.GetBytes("""{"password":"hunter2"}"""));

        var tampered = sealedSecret.Payload.ToArray();
        tampered[^1] ^= 0x01; // flip exactly one bit of the ciphertext

        cipher.TryUnseal(sealedSecret with { Payload = tampered }, out _).Should().BeFalse();
    }

    [Fact]
    public void A_tampered_wrapped_data_key_does_not_open()
    {
        var cipher = Cipher("k1", ("k1", KeyA));
        var sealedSecret = cipher.Seal(Encoding.UTF8.GetBytes("secret"));

        var tampered = sealedSecret.WrappedDataKey.ToArray();
        tampered[^1] ^= 0x01;

        cipher.TryUnseal(sealedSecret with { WrappedDataKey = tampered }, out _).Should().BeFalse();
    }

    [Fact]
    public void A_truncated_payload_does_not_open()
    {
        var cipher = Cipher("k1", ("k1", KeyA));
        var sealedSecret = cipher.Seal(Encoding.UTF8.GetBytes("secret"));

        var truncated = sealedSecret.Payload[..4]; // shorter than nonce + tag

        cipher.TryUnseal(sealedSecret with { Payload = truncated }, out _).Should().BeFalse();
    }

    [Fact]
    public void An_active_key_id_with_no_matching_key_fails_loudly_at_startup()
    {
        // Fail at construction, not at the first user who tries to save a credential. An
        // operator must learn about a misconfigured key from a failed deploy.
        var build = () => Cipher("k1", ("other", KeyA));

        build.Should().Throw<InvalidOperationException>().WithMessage("*no key with that id*");
    }

    [Fact]
    public void An_unset_active_key_id_fails_loudly_at_startup()
    {
        var build = () => Cipher(string.Empty);

        build.Should().Throw<InvalidOperationException>().WithMessage("*ActiveKeyId is not configured*");
    }

    [Fact]
    public void A_key_of_the_wrong_length_is_rejected()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var build = () => Cipher("k1", ("k1", shortKey));

        build.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void A_key_that_is_not_base64_is_rejected()
    {
        var build = () => Cipher("k1", ("k1", "!!! not base64 !!!"));

        build.Should().Throw<InvalidOperationException>().WithMessage("*base64*");
    }
}

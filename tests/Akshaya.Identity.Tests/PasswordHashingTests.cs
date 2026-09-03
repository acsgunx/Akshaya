using Akshaya.Modules.Identity.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Akshaya.Identity.Tests;

/// <summary>
/// PREVENTS: the classic password-storage failures — a hash that is really an encoding, a
/// shared salt that makes one rainbow table cover every user, and a cost parameter that can
/// never be raised because raising it would lock everyone out.
/// </summary>
public sealed class PasswordHashingTests
{
    // Deliberately far below production cost. These tests are about correctness, not about
    // spending 600,000 iterations per case; the parameter being settable at all is what lets
    // the cost be raised in production without a migration.
    private static readonly Pbkdf2PasswordHasher Hasher = new(iterations: 1_000);

    [Fact]
    public void A_correct_password_verifies()
    {
        var hash = Hasher.Hash("correct horse battery staple");

        Hasher.Verify("correct horse battery staple", hash).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_wrong_password_does_not_verify()
    {
        var hash = Hasher.Hash("correct horse battery staple");

        Hasher.Verify("Correct horse battery staple", hash).IsValid.Should().BeFalse();
    }

    [Fact]
    public void The_hash_does_not_contain_the_password()
    {
        // Guards against the "hash" being an encoding. Trivially true today; the test exists
        // so it stays true if the format is ever changed.
        var hash = Hasher.Hash("hunter2-is-a-terrible-password");

        hash.Should().NotContain("hunter2");
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // A per-hash random salt. Without it, two users with the same password have the same
        // stored hash, and one cracked password cracks every account that shares it.
        var first = Hasher.Hash("the same password");
        var second = Hasher.Hash("the same password");

        first.Should().NotBe(second);
        Hasher.Verify("the same password", first).IsValid.Should().BeTrue();
        Hasher.Verify("the same password", second).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_hash_made_with_fewer_iterations_verifies_but_asks_to_be_upgraded()
    {
        // The whole point of storing the cost per hash: raising the policy must not invalidate
        // existing passwords, it must flag them for rehash on the next successful sign-in.
        var weak = new Pbkdf2PasswordHasher(iterations: 500).Hash("a fine password");
        var stronger = new Pbkdf2PasswordHasher(iterations: 5_000);

        var result = stronger.Verify("a fine password", weak);

        result.IsValid.Should().BeTrue();
        result.NeedsRehash.Should().BeTrue();
    }

    [Fact]
    public void A_hash_at_current_cost_is_not_flagged_for_rehash()
    {
        var hash = Hasher.Hash("a fine password");

        Hasher.Verify("a fine password", hash).NeedsRehash.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$!!!not-base64!!!$aGFzaA==")]
    [InlineData("bcrypt$12$whatever$else")]
    public void A_corrupt_stored_hash_fails_verification_instead_of_throwing(string stored)
    {
        // A hand-edited or truncated row must fail ONE sign-in, not crash the endpoint for
        // every other user hitting it.
        var verify = () => Hasher.Verify("any password", stored);

        verify.Should().NotThrow();
        verify().IsValid.Should().BeFalse();
    }
}

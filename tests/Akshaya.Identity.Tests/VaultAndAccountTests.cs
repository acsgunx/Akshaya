using System.Text;
using Akshaya.Modules.Identity.Application;
using Akshaya.Modules.Identity.Domain;
using Akshaya.Modules.Identity.Infrastructure;
using Akshaya.Modules.Identity.Infrastructure.Ef;
using Akshaya.SharedKernel;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Akshaya.Identity.Tests;

/// <summary>
/// A real database for the duration of one test class.
///
/// SQLite in-memory rather than EF's InMemory provider, because the InMemory provider does not
/// enforce unique indexes — and the duplicate-registration test below is entirely about the
/// unique index doing its job. A test that passes only because the fake cannot fail is worse
/// than no test.
/// </summary>
public sealed class IdentityFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public IdentityFixture()
    {
        // The connection must stay open: an in-memory SQLite database exists only as long as
        // a connection to it does.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlite(_connection)
            .Options);

        Db.Database.EnsureCreated();

        Clock = new FixedClock(new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero));

        Cipher = new AesGcmCredentialCipher(Options.Create(new CredentialProtectionOptions
        {
            ActiveKeyId = "test",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["test"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            },
        }));

        Accounts = new UserAccountService(
            new EfUserAccountStore(Db),
            new Pbkdf2PasswordHasher(iterations: 1_000),
            Clock,
            NullLogger<UserAccountService>.Instance);

        Vault = new BrokerCredentialVault(
            new EfSavedCredentialStore(Db),
            Cipher,
            Clock,
            NullLogger<BrokerCredentialVault>.Instance);
    }

    public IdentityDbContext Db { get; }

    public FixedClock Clock { get; }

    public AesGcmCredentialCipher Cipher { get; }

    public UserAccountService Accounts { get; }

    public BrokerCredentialVault Vault { get; }

    /// <summary>
    /// Registers a real account and returns it.
    ///
    /// The vault's rows carry a foreign key to <c>users</c>, so tests cannot invent a user id —
    /// and should not want to: a saved credential belonging to nobody is precisely the orphaned
    /// secret the cascade delete exists to prevent.
    /// </summary>
    public async Task<UserAccount> NewUserAsync(string label)
    {
        var result = await Accounts.RegisterAsync($"{label}@example.com", "a good long password", null);
        result.IsSuccess.Should().BeTrue($"the fixture could not create the '{label}' account");
        return result.Value;
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>Test clock. Nothing in the platform may read the ambient time; see SharedKernel/Clock.cs.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>
/// PREVENTS: an account system that tells an attacker which email addresses are registered,
/// and duplicate accounts created by two sign-ups landing at once.
/// </summary>
public sealed class UserAccountServiceTests : IClassFixture<IdentityFixture>
{
    private readonly IdentityFixture _fixture;

    public UserAccountServiceTests(IdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Registering_creates_an_account_that_can_sign_in()
    {
        var registered = await _fixture.Accounts.RegisterAsync("first@example.com", "a good long password", "First");

        registered.IsSuccess.Should().BeTrue();
        registered.Value.Email.Should().Be("first@example.com");
        registered.Value.TenantId.Should().NotBeNullOrEmpty();

        var signedIn = await _fixture.Accounts.SignInAsync("first@example.com", "a good long password");
        signedIn.IsSuccess.Should().BeTrue();
        signedIn.Value.Id.Should().Be(registered.Value.Id);
    }

    [Fact]
    public async Task The_same_address_cannot_be_registered_twice_whatever_its_casing()
    {
        await _fixture.Accounts.RegisterAsync("dupe@example.com", "a good long password", null);

        var second = await _fixture.Accounts.RegisterAsync("  DUPE@Example.COM  ", "another password", null);

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be(IdentityErrorCodes.EmailAlreadyRegistered);
    }

    [Fact]
    public async Task An_unknown_address_and_a_wrong_password_are_indistinguishable()
    {
        // THE anti-enumeration test. If these two ever diverge, the sign-in form becomes a
        // tool for discovering which addresses hold broker credentials on this deployment.
        await _fixture.Accounts.RegisterAsync("known@example.com", "the real password", null);

        var wrongPassword = await _fixture.Accounts.SignInAsync("known@example.com", "not the password");
        var unknownAddress = await _fixture.Accounts.SignInAsync("nobody@example.com", "not the password");

        wrongPassword.IsFailure.Should().BeTrue();
        unknownAddress.IsFailure.Should().BeTrue();
        unknownAddress.Error.Code.Should().Be(wrongPassword.Error.Code);
        unknownAddress.Error.Message.Should().Be(wrongPassword.Error.Message);
    }

    [Fact]
    public async Task A_password_below_the_minimum_length_is_rejected()
    {
        var result = await _fixture.Accounts.RegisterAsync("short@example.com", "tiny", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.InvalidRequest);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("someone@")]
    [InlineData("two@at@example.com")]
    [InlineData("has space@example.com")]
    public async Task An_implausible_address_is_rejected(string email)
    {
        var result = await _fixture.Accounts.RegisterAsync(email, "a good long password", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Signing_in_records_the_time_and_leaves_the_account_usable()
    {
        await _fixture.Accounts.RegisterAsync("stamp@example.com", "a good long password", null);

        var signedIn = await _fixture.Accounts.SignInAsync("stamp@example.com", "a good long password");

        signedIn.Value.LastSignedInAt.Should().Be(_fixture.Clock.UtcNow);

        // And the recorded sign-in did not corrupt the stored hash.
        var again = await _fixture.Accounts.SignInAsync("stamp@example.com", "a good long password");
        again.IsSuccess.Should().BeTrue();
    }
}

/// <summary>
/// PREVENTS: the two failures that would make the saved-login feature a liability — one user
/// reading another's vault, and the vault storing more than the user agreed to.
/// </summary>
public sealed class BrokerCredentialVaultTests : IClassFixture<IdentityFixture>
{
    private readonly IdentityFixture _fixture;

    public BrokerCredentialVaultTests(IdentityFixture fixture) => _fixture = fixture;

    private static Dictionary<string, string> Fields() => new(StringComparer.Ordinal)
    {
        ["api_key"] = "AK-12345",
        ["username"] = "USER-1",
        ["password"] = "hunter2",
    };

    private async Task<(string TenantId, string UserId)> UserAsync(string label)
    {
        var account = await _fixture.NewUserAsync(label);
        return (account.TenantId, account.Id);
    }

    [Fact]
    public async Task Saved_fields_come_back_exactly_as_they_went_in()
    {
        var (tenant, user) = await UserAsync("roundtrip");

        var saved = await _fixture.Vault.SaveAsync(tenant, user, "broker-x", "Main", Fields());
        saved.IsSuccess.Should().BeTrue();

        var revealed = await _fixture.Vault.RevealAsync(user, saved.Value.Id);

        revealed.IsSuccess.Should().BeTrue();
        revealed.Value.Should().BeEquivalentTo(Fields());
    }

    [Fact]
    public async Task Only_the_field_keys_are_listed_never_the_values()
    {
        var (tenant, user) = await UserAsync("keys");

        await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, Fields());

        var listed = await _fixture.Vault.ListAsync(user);

        listed.Should().ContainSingle();
        listed[0].RememberedKeys.Should().BeEquivalentTo(["api_key", "username", "password"]);

        // The summary type has no member for a value at all — this asserts the shape stays
        // that way, so a future "just add the values, it's convenient" change fails a test.
        typeof(SavedCredentialSummary).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(["Values", "Fields", "Secrets"]);
    }

    [Fact]
    public async Task Blank_values_are_not_stored()
    {
        // An unticked or empty field is not a secret worth a row, and storing "" would make
        // the UI claim a field is remembered when nothing usable is.
        var (tenant, user) = await UserAsync("blank");

        var saved = await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = "AK-1",
            ["password"] = "",
        });

        saved.Value.RememberedKeys.Should().Equal("api_key");
    }

    [Fact]
    public async Task Saving_nothing_is_a_failure_rather_than_an_empty_record()
    {
        var (tenant, user) = await UserAsync("empty");

        var saved = await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, new Dictionary<string, string>(StringComparer.Ordinal));

        saved.IsFailure.Should().BeTrue();
        saved.Error.Code.Should().Be(IdentityErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Re_saving_the_same_broker_and_nickname_replaces_rather_than_duplicates()
    {
        var (tenant, user) = await UserAsync("replace");

        await _fixture.Vault.SaveAsync(tenant, user, "broker-x", "Main", Fields());
        await _fixture.Vault.SaveAsync(tenant, user, "broker-x", "Main", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = "AK-CORRECTED",
        });

        var listed = await _fixture.Vault.ListAsync(user);

        listed.Should().ContainSingle("a corrected typo must not leave a stale second login behind");
        listed[0].RememberedKeys.Should().Equal("api_key");
    }

    [Fact]
    public async Task A_different_nickname_for_the_same_broker_is_a_separate_login()
    {
        // Two accounts at one broker is a legitimate case — it is why the nickname exists.
        var (tenant, user) = await UserAsync("twoaccounts");

        await _fixture.Vault.SaveAsync(tenant, user, "broker-x", "Personal", Fields());
        await _fixture.Vault.SaveAsync(tenant, user, "broker-x", "Family trust", Fields());

        (await _fixture.Vault.ListAsync(user)).Should().HaveCount(2);
    }

    [Fact]
    public async Task One_user_cannot_reveal_another_users_credential()
    {
        // THE isolation test. The id is a bare GUID, so guessing is not the threat — leaking
        // it in a log or a URL is, and the owner check is what makes that survivable.
        var (tenant, owner) = await UserAsync("owner");
        var (_, attacker) = await UserAsync("attacker");

        var saved = await _fixture.Vault.SaveAsync(tenant, owner, "broker-x", null, Fields());

        var stolen = await _fixture.Vault.RevealAsync(attacker, saved.Value.Id);

        stolen.IsFailure.Should().BeTrue();
        stolen.Error.Code.Should().Be(IdentityErrorCodes.CredentialNotFound);
    }

    [Fact]
    public async Task One_user_cannot_delete_another_users_credential()
    {
        var (tenant, owner) = await UserAsync("owner2");
        var (_, attacker) = await UserAsync("attacker2");

        var saved = await _fixture.Vault.SaveAsync(tenant, owner, "broker-x", null, Fields());

        var deleted = await _fixture.Vault.DeleteAsync(attacker, saved.Value.Id);

        deleted.IsFailure.Should().BeTrue();
        (await _fixture.Vault.ListAsync(owner)).Should().ContainSingle();
    }

    [Fact]
    public async Task Listing_is_scoped_to_the_user()
    {
        var (aliceTenant, alice) = await UserAsync("alice");
        var (bobTenant, bob) = await UserAsync("bob");
        var (_, carol) = await UserAsync("carol");

        await _fixture.Vault.SaveAsync(aliceTenant, alice, "broker-x", null, Fields());
        await _fixture.Vault.SaveAsync(bobTenant, bob, "broker-x", null, Fields());

        (await _fixture.Vault.ListAsync(alice)).Should().ContainSingle();
        (await _fixture.Vault.ListAsync(bob)).Should().ContainSingle();
        (await _fixture.Vault.ListAsync(carol)).Should().BeEmpty();
    }

    [Fact]
    public async Task Revealing_records_that_the_login_was_used()
    {
        var (tenant, user) = await UserAsync("used");

        var saved = await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, Fields());
        saved.Value.LastUsedAt.Should().BeNull();

        await _fixture.Vault.RevealAsync(user, saved.Value.Id);

        var listed = await _fixture.Vault.ListAsync(user);
        listed[0].LastUsedAt.Should().Be(_fixture.Clock.UtcNow);
    }

    [Fact]
    public async Task Deleting_removes_it()
    {
        var (tenant, user) = await UserAsync("deleting");

        var saved = await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, Fields());

        (await _fixture.Vault.DeleteAsync(user, saved.Value.Id)).IsSuccess.Should().BeTrue();

        (await _fixture.Vault.ListAsync(user)).Should().BeEmpty();
        (await _fixture.Vault.RevealAsync(user, saved.Value.Id)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_record_whose_key_is_gone_reports_unreadable_rather_than_throwing()
    {
        // The rotation-gone-wrong case. The user must be told to re-enter it; the page listing
        // their other, working logins must still render.
        var (tenant, user) = await UserAsync("lostkey");

        var saved = await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, Fields());

        var vaultWithDifferentKey = new BrokerCredentialVault(
            new EfSavedCredentialStore(_fixture.Db),
            new AesGcmCredentialCipher(Options.Create(new CredentialProtectionOptions
            {
                ActiveKeyId = "other",
                Keys = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["other"] = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=",
                },
            })),
            _fixture.Clock,
            NullLogger<BrokerCredentialVault>.Instance);

        var revealed = await vaultWithDifferentKey.RevealAsync(user, saved.Value.Id);

        revealed.IsFailure.Should().BeTrue();
        revealed.Error.Code.Should().Be(IdentityErrorCodes.CredentialUnreadable);
    }

    [Fact]
    public async Task The_stored_row_holds_no_plaintext()
    {
        // End-to-end proof of the claim the UI makes to the user. Reads the actual row rather
        // than trusting the cipher's own unit tests.
        var (tenant, user) = await UserAsync("atrest");

        var saved = await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, Fields());

        var row = await _fixture.Db.SavedCredentials.AsNoTracking()
            .FirstAsync(c => c.Id == saved.Value.Id);

        var blob = Encoding.UTF8.GetString(row.Payload) + Encoding.UTF8.GetString(row.WrappedDataKey);

        blob.Should().NotContain("AK-12345");
        blob.Should().NotContain("hunter2");
        blob.Should().NotContain("USER-1");
    }

    [Fact]
    public async Task Deleting_the_account_takes_its_saved_credentials_with_it()
    {
        // The cascade. An encrypted credential belonging to a user who no longer exists is
        // exactly the orphaned secret nobody remembers to clean up.
        var (tenant, user) = await UserAsync("cascade");
        await _fixture.Vault.SaveAsync(tenant, user, "broker-x", null, Fields());

        await _fixture.Db.Users.Where(u => u.Id == user).ExecuteDeleteAsync();

        (await _fixture.Vault.ListAsync(user)).Should().BeEmpty();
    }
}

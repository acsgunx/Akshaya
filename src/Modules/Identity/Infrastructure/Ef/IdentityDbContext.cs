using Akshaya.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Akshaya.Modules.Identity.Infrastructure.Ef;

/// <summary>
/// Row shape for <see cref="UserAccount"/>.
///
/// A separate persistence type rather than mapping the domain record directly: the domain
/// record is immutable with <c>required init</c> members, which EF can materialise but cannot
/// change-track comfortably, and keeping them apart means a storage concern (a shadow column,
/// a rowversion) never forces itself into the domain model.
/// </summary>
public sealed class UserAccountRow
{
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string NormalisedEmail { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignedInAt { get; set; }

    public bool IsActive { get; set; }

    public static UserAccountRow From(UserAccount account) => new()
    {
        Id = account.Id,
        TenantId = account.TenantId,
        Email = account.Email,
        NormalisedEmail = account.NormalisedEmail,
        PasswordHash = account.PasswordHash,
        DisplayName = account.DisplayName,
        CreatedAt = account.CreatedAt,
        LastSignedInAt = account.LastSignedInAt,
        IsActive = account.IsActive,
    };

    public UserAccount ToDomain() => new()
    {
        Id = Id,
        TenantId = TenantId,
        Email = Email,
        NormalisedEmail = NormalisedEmail,
        PasswordHash = PasswordHash,
        DisplayName = DisplayName,
        CreatedAt = CreatedAt,
        LastSignedInAt = LastSignedInAt,
        IsActive = IsActive,
    };
}

/// <summary>
/// Row shape for <see cref="SavedBrokerCredential"/>.
///
/// The remembered field keys are stored as a newline-delimited string rather than a JSON
/// column or a child table. They are a short list of opaque identifiers that is only ever read
/// and written whole — a child table would buy a join and a migration for nothing, and a
/// provider-specific JSON column would tie this module to Postgres, which the SQLite-backed
/// tests would then not exercise.
/// </summary>
public sealed class SavedCredentialRow
{
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string ConnectorId { get; set; } = string.Empty;

    public string? Nickname { get; set; }

    /// <summary>Newline-delimited manifest field keys. Never values.</summary>
    public string RememberedKeys { get; set; } = string.Empty;

    /// <summary>Which master key sealed <see cref="WrappedDataKey"/>. Drives rotation.</summary>
    public string KeyId { get; set; } = string.Empty;

    public byte[] WrappedDataKey { get; set; } = [];

    public byte[] Payload { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public static SavedCredentialRow From(SavedBrokerCredential credential) => new()
    {
        Id = credential.Id,
        TenantId = credential.TenantId,
        UserId = credential.UserId,
        ConnectorId = credential.ConnectorId,
        Nickname = credential.Nickname,
        RememberedKeys = string.Join('\n', credential.RememberedKeys),
        KeyId = credential.SealedFields.KeyId,
        WrappedDataKey = credential.SealedFields.WrappedDataKey,
        Payload = credential.SealedFields.Payload,
        CreatedAt = credential.CreatedAt,
        UpdatedAt = credential.UpdatedAt,
        LastUsedAt = credential.LastUsedAt,
    };

    public SavedBrokerCredential ToDomain() => new()
    {
        Id = Id,
        TenantId = TenantId,
        UserId = UserId,
        ConnectorId = ConnectorId,
        Nickname = Nickname,
        RememberedKeys = RememberedKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        SealedFields = new SealedSecret(KeyId, WrappedDataKey, Payload),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        LastUsedAt = LastUsedAt,
    };
}

/// <summary>
/// The identity module's own schema, in its own <c>identity</c> namespace/schema.
///
/// One DbContext per module rather than one for the application: modules that own separate
/// tables should own separate migration histories, so adding a column to the trading schema
/// cannot block a deploy of this one.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string SchemaName = "identity";

    public DbSet<UserAccountRow> Users => Set<UserAccountRow>();

    public DbSet<SavedCredentialRow> SavedCredentials => Set<SavedCredentialRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<UserAccountRow>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(320).IsRequired();
            entity.Property(e => e.NormalisedEmail).HasMaxLength(320).IsRequired();
            entity.Property(e => e.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(120);

            // THE constraint that makes registration's race safe. UserAccountService relies on
            // the insert failing rather than reading first; without this index it silently
            // creates duplicate accounts under concurrency.
            entity.HasIndex(e => e.NormalisedEmail).IsUnique();
        });

        modelBuilder.Entity<SavedCredentialRow>(entity =>
        {
            entity.ToTable("saved_broker_credentials");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ConnectorId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Nickname).HasMaxLength(120);
            entity.Property(e => e.RememberedKeys).IsRequired();
            entity.Property(e => e.KeyId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.WrappedDataKey).IsRequired();
            entity.Property(e => e.Payload).IsRequired();

            entity.HasIndex(e => new { e.UserId, e.ConnectorId });

            // Deleting an account takes its saved credentials with it. Leaving encrypted
            // credentials behind for a user who no longer exists is exactly the kind of
            // orphaned secret nobody remembers to clean up.
            entity.HasOne<UserAccountRow>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

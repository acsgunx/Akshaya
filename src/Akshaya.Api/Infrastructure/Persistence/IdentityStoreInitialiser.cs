using System.Security.Cryptography;
using Akshaya.Modules.Identity.Application;
using Akshaya.Modules.Identity.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Akshaya.Api.Infrastructure.Persistence;

/// <summary>
/// Brings the identity store up to a usable state at startup: schema first, then the seed
/// account if one is wanted and the store is empty.
///
/// Doing this in-process rather than as a separate `dotnet ef database update` step is what
/// makes a one-container deployment possible at all — there is no migration job to schedule and
/// no shell to run it from on the hosts this is aimed at.
/// </summary>
public static class IdentityStoreInitialiser
{
    /// <summary>
    /// Creates or migrates the schema and seeds the first account when appropriate.
    /// </summary>
    public static async Task InitialiseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var options = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentityStoreInitialiser));
        var db = sp.GetRequiredService<IdentityDbContext>();

        if (options.Mode == PersistenceMode.Postgres)
        {
            // Versioned DDL, reviewed in a pull request like any other change. This is the only
            // mode where the schema history is a thing anyone can inspect.
            logger.LogInformation("Applying identity migrations (Postgres).");
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            // SQLite gets its schema straight from the model. The migrations in this assembly
            // are Postgres DDL and would not run here; and for a store whose whole point is that
            // it costs nothing to recreate, a migration history buys nothing worth the second
            // set of migrations it would cost to maintain.
            logger.LogInformation("Ensuring identity schema exists ({Mode}).", options.Mode);
            await db.Database.EnsureCreatedAsync(ct);
        }

        await SeedAsync(sp, db, options, logger, ct);
    }

    private static async Task SeedAsync(
        IServiceProvider sp,
        IdentityDbContext db,
        PersistenceOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var seed = options.SeedUser;

        // Null means "decide from the mode": convenience where the store is disposable, silence
        // where an account nobody created appearing in a shared database would be an incident.
        var enabled = seed.Enabled ?? options.Mode != PersistenceMode.Postgres;
        if (!enabled)
        {
            return;
        }

        // ONLY into an empty store. Not "if this address is missing" — that would recreate the
        // seed account every time an operator deliberately deleted it, and would keep handing
        // out a known address on a database that has real users in it.
        if (await db.Users.AnyAsync(ct))
        {
            return;
        }

        var generated = string.IsNullOrWhiteSpace(seed.Password);
        var password = generated ? GeneratePassword() : seed.Password;

        var accounts = sp.GetRequiredService<UserAccountService>();
        var result = await accounts.RegisterAsync(seed.Email, password, seed.DisplayName, ct);

        if (result.IsFailure)
        {
            // Not fatal: a bad seed configuration must not stop an otherwise working deployment
            // from starting, because sign-up still works without it.
            logger.LogError(
                "Seed account {Email} was not created: {Error}", seed.Email, result.Error.Message);
            return;
        }

        if (generated)
        {
            // Written once, at Warning so it survives a production log level, and only ever for
            // a password this process just invented. A configured password is never logged.
            // NOT "change it after signing in" — there is no change-password endpoint yet, so this
            // value is the one that account keeps. Say so, and point at the setting that lets an
            // operator choose it instead of inheriting whatever this process invented.
            logger.LogWarning(
                "Seeded the first account because the identity store was empty. "
                + "Sign in as {Email} with the generated password: {Password} "
                + "— it will not be shown again, and cannot be changed in the app. "
                + "Set Persistence:SeedUser:Password to choose it yourself instead.",
                seed.Email,
                password);
        }
        else
        {
            logger.LogInformation(
                "Seeded the first account {Email} using the configured password.", seed.Email);
        }
    }

    /// <summary>
    /// A password that satisfies <see cref="UserAccountService.MinimumPasswordLength"/> with room
    /// to spare, from a cryptographic RNG.
    ///
    /// The alphabet omits characters that are ambiguous when read off a log — no O/0, no I/l/1 —
    /// because the entire purpose of this value is that a human retypes it once.
    /// </summary>
    private static string GeneratePassword()
    {
        const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        const int Length = 20;

        return RandomNumberGenerator.GetString(Alphabet, Length);
    }
}

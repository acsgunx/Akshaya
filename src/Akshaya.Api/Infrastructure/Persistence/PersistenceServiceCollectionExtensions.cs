using Akshaya.Modules.Identity.Infrastructure.Ef;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Akshaya.Api.Infrastructure.Persistence;

/// <summary>
/// Wires the identity module's <see cref="IdentityDbContext"/> to whichever store the
/// <c>Persistence</c> section selected.
///
/// THE PROVIDER CHOICE LIVES HERE, not in the module. The identity module references only
/// EF Core's provider-agnostic packages, which is exactly what lets one model serve SQLite on a
/// $0 deployment and Postgres on an enterprise one without a line of it changing.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// The shared-cache database name used by <see cref="PersistenceMode.InMemory"/>. Every
    /// connection naming it reaches the same in-process database.
    /// </summary>
    private const string InMemoryDatabaseName = "akshaya-identity";

    /// <summary>
    /// Registers <see cref="IdentityDbContext"/>, its options, and a readiness health check for
    /// the store behind it.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">Configuration to bind the <c>Persistence</c> section from.</param>
    /// <param name="contentRootPath">Content root, used to resolve a relative SQLite path.</param>
    /// <returns>The resolved options, so the caller can log what it ended up with.</returns>
    public static PersistenceOptions AddAkshayaPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contentRootPath);

        var section = configuration.GetSection(PersistenceOptions.SectionName);
        services.Configure<PersistenceOptions>(section);

        var options = new PersistenceOptions();
        section.Bind(options);

        switch (options.Mode)
        {
            case PersistenceMode.Postgres:
                AddPostgres(services, configuration);
                break;

            case PersistenceMode.InMemory:
                AddSqliteInMemory(services);
                break;

            case PersistenceMode.Sqlite:
            default:
                AddSqliteFile(services, ResolveSqlitePath(options.SqlitePath, contentRootPath));
                break;
        }

        services.AddHealthChecks().AddCheck<IdentityStoreHealthCheck>("identity-store");

        return options;
    }

    /// <summary>
    /// Turns a possibly-relative configured path into an absolute one and makes sure the
    /// directory exists.
    ///
    /// Creating the directory here rather than letting SQLite fail is deliberate: "unable to
    /// open database file" is the least informative error in the library, and on a container
    /// host the cause is nearly always a volume mounted one directory up from where the
    /// configured path expects to write.
    /// </summary>
    public static string ResolveSqlitePath(string configuredPath, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(contentRootPath);

        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "App_Data/akshaya-identity.db"
            : configuredPath;

        var absolute = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(contentRootPath, path));

        var directory = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return absolute;
    }

    private static void AddSqliteFile(IServiceCollection services, string absolutePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            // The database is created on first run by EnsureCreated; there is no separate
            // provisioning step to fail before it.
            Mode = SqliteOpenMode.ReadWriteCreate,
            // One process, many concurrent requests: shared cache lets them share the page
            // cache instead of each opening an independent view of the same file.
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        services.AddDbContext<IdentityDbContext>(builder => ConfigureSqlite(builder, connectionString));
    }

    private static void AddSqliteInMemory(IServiceCollection services)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = InMemoryDatabaseName,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        // A shared in-memory SQLite database exists only while at least one connection to it is
        // open. Request-scoped DbContexts open and close theirs constantly, so without this
        // keep-alive the schema — and every account in it — is destroyed between two requests.
        services.AddSingleton(_ => new SqliteKeepAlive(connectionString));

        services.AddDbContext<IdentityDbContext>((sp, builder) =>
        {
            // Resolving the keep-alive here, rather than only at initialisation, guarantees it
            // is constructed before the first context opens a connection.
            _ = sp.GetRequiredService<SqliteKeepAlive>();
            ConfigureSqlite(builder, connectionString);
        });
    }

    private static void ConfigureSqlite(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString, sqlite => sqlite
            // The migrations in this assembly are Postgres DDL. SQLite gets its schema from
            // EnsureCreated instead — see IdentityStoreInitialiser — so no migrations assembly
            // is nominated here on purpose.
            .CommandTimeout(30));

    private static void AddPostgres(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Identity")
            ?? "Host=localhost;Port=5432;Database=akshaya;Username=akshaya;Password=akshaya";

        // The MIGRATIONS live in this assembly, not in the module. The module owns the model and
        // stays provider-agnostic — which is what lets it run on SQLite above — while the
        // composition root owns the choice of Postgres and the Postgres-specific DDL it implies.
        services.AddDbContext<IdentityDbContext>(builder => builder.UseNpgsql(
            connectionString,
            npgsql => npgsql
                .MigrationsAssembly(typeof(Program).Assembly.FullName)
                .MigrationsHistoryTable("__ef_migrations", IdentityDbContext.SchemaName)));
    }
}

/// <summary>
/// Holds one SQLite connection open for the lifetime of the application, so that a shared
/// in-memory database outlives the request-scoped contexts that use it.
/// </summary>
public sealed class SqliteKeepAlive : IDisposable
{
    private readonly SqliteConnection connection;

    /// <summary>Opens and holds the connection named by <paramref name="connectionString"/>.</summary>
    public SqliteKeepAlive(string connectionString)
    {
        connection = new SqliteConnection(connectionString);
        connection.Open();
    }

    /// <inheritdoc />
    public void Dispose() => connection.Dispose();
}

/// <summary>
/// Readiness probe for the identity store: can we actually reach it?
///
/// It matters more in the local-database modes than it looks. A SQLite path pointing at a
/// directory the container cannot write to fails here, at the probe, instead of at the first
/// user who tries to sign up.
/// </summary>
internal sealed class IdentityStoreHealthCheck(
    IdentityDbContext db,
    IOptions<PersistenceOptions> options)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var mode = options.Value.Mode.ToString();
        var data = new Dictionary<string, object> { ["mode"] = mode };

        try
        {
            var reachable = await db.Database.CanConnectAsync(cancellationToken);

            return reachable
                ? HealthCheckResult.Healthy(
                    $"Identity store reachable ({mode}).", data)
                : HealthCheckResult.Unhealthy(
                    $"Identity store unreachable ({mode}).", data: data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Identity store threw on connect ({mode}).", ex, data);
        }
    }
}

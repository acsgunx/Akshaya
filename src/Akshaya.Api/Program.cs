// ============================================================================================
// AKSHAYA API — composition root.
//
// This file deliberately contains ZERO broker names: connectors are discovered through the
// generic IConnectorFactory / ConnectorCatalog machinery in Akshaya.Connectors.Host, never by
// naming a concrete connector type. The one exception is "Paper" — the platform's own built-in
// simulator, not a third-party broker — which is wired in-process below exactly the way a real
// operator would wire any first-party connector that ships with the platform.
//
// tests/Akshaya.Architecture.Tests scans this project for vendor names; keep it that way.
// ============================================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Akshaya.Api.Contracts;
using Akshaya.Api.Endpoints;
using Akshaya.Api.Hubs;
using Akshaya.Api.Infrastructure;
using Akshaya.Connector.Paper;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Host;
using Akshaya.Connectors.Sdk;
using Akshaya.Modules.Identity;
using Akshaya.Modules.Identity.Infrastructure;
using Akshaya.Modules.Identity.Infrastructure.Ef;
using Akshaya.Modules.MarketData;
using Akshaya.Modules.Portfolio;
using Akshaya.Modules.Portfolio.Ports;
using Akshaya.Modules.Trading;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;

// ── Serilog bootstrap: a logger exists before the host does, so startup failures are logged
// rather than lost to a crashed console. ──────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Akshaya API");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // ── Time. Nothing in this composition root or below it may call DateTime.Now — see
    // SharedKernel/Clock.cs; tests/Akshaya.Architecture.Tests enforces it. ─────────────────────
    builder.Services.AddSingleton<IClock>(SystemClock.Instance);

    // ── Trading calendar. Dev-only, approximate hours for the venues the built-in connectors
    // and the paper simulator claim; a real deployment seeds this from reference data instead. ─
    builder.Services.AddSingleton<ITradingCalendar>(_ => new TradingCalendar(BuildDevTradingCalendars()));

    // ── Connector host. ──────────────────────────────────────────────────────────────────────
    //
    // MISMATCH WORTH FLAGGING: this project's csproj comment says connectors are "discovered by
    // scanning the output folder for IConnectorPlugin implementations", but Akshaya.Connectors.Host
    // ships no IServiceCollection.AddAkshayaConnectors(...) extension method to do that scanning
    // — only the raw pieces (ConnectorCatalog, ConnectorFactory, ConnectorHostOptions,
    // GatewaySupervisor). This composition root therefore wires them by hand, exactly the way
    // such an extension method would. If Connectors.Host later ships one, this block is what
    // gets deleted in its favour.
    var paperConnectors = new ConcurrentDictionary<string, PaperConnector>(StringComparer.Ordinal);
    var paperManifest = LoadEmbeddedManifest(typeof(PaperConnector).Assembly, "paper");

    builder.Services.Configure<ConnectorHostOptions>(options =>
    {
        // Null (the default) disables disk scanning entirely — the right setting until a real
        // broker connector ships an IConnectorPlugin entry point and is dropped in this folder.
        // None of this solution's compiled-in connector projects currently exposes one, so
        // setting this by default to a directory would only produce load failures the operator
        // did not ask for.
        options.PluginDirectory = builder.Configuration["Connectors:PluginDirectory"];
        options.FailFastOnPluginError = false;

        // The Paper simulator ships with the platform, so it is registered the same way a host
        // would register any other first-party, compiled-in connector — see AddInProcess's own
        // doc comment. This is the ONLY connector this file may name.
        options.AddInProcess(paperManifest, context => CreatePaperConnector(context, paperConnectors));

        // Every other compiled-in connector is discovered by its IConnectorPlugin entry point —
        // the same contract a dropped-in plugin implements — so this file never names one. Paper
        // is the exception above only because its lifetime is special (one instance per account).
        foreach (var plugin in DiscoverInProcessPlugins())
        {
            var alreadyRegistered = options.InProcess.Any(
                r => string.Equals(r.Manifest.Id, plugin.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            if (!alreadyRegistered)
            {
                options.AddInProcess(plugin.Manifest, plugin.Create);
            }
        }
    });

    builder.Services.AddSingleton<ConnectorCatalog>();
    builder.Services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
    builder.Services.AddSingleton<IConnectorAuditSink, LoggingConnectorAuditSink>();
    builder.Services.AddSingleton<IGatewayRuntime, NullGatewayRuntime>();
    builder.Services.AddSingleton<IGatewaySupervisor, GatewaySupervisor>();
    builder.Services.AddSingleton<IConnectorFactory, ConnectorFactory>();

    // ── Trading core + Portfolio module. ─────────────────────────────────────────────────────
    builder.Services.AddTradingCore();
    builder.Services.AddDevelopmentTradingStores(
        Currency.Inr,
        fx => fx
            .Set(Currency.Usd, Currency.Inr, 84.00m)
            .Set(Currency.Sgd, Currency.Inr, 63.00m)
            .Set(Currency.Hkd, Currency.Inr, 10.80m));

    builder.Services.AddBlendedPortfolio();
    builder.Services.AddDevelopmentFxRates(fx => fx
        .Set(Currency.Usd, Currency.Inr, 84.00m)
        .Set(Currency.Sgd, Currency.Inr, 63.00m)
        .Set(Currency.Hkd, Currency.Inr, 10.80m));

    // The Portfolio module deliberately does not know about Trading's link store (see
    // IPortfolioLinkProvider's remarks) — this is the dev-only bridge between them.
    builder.Services.AddSingleton<IPortfolioLinkProvider>(sp =>
        new BrokerLinkPortfolioProvider(sp.GetRequiredService<IBrokerLinkStore>()));

    builder.Services.Configure<PortfolioOptions>(builder.Configuration.GetSection("Portfolio"));

    // ── Instrument master. ───────────────────────────────────────────────────────────────────
    //
    // One shared, searchable copy of each connector's instrument list. Registered here rather
    // than inside a connector because the whole point is that it OUTLIVES the request-scoped
    // connectors that fill it: a broker whose master is a single large CSV would otherwise
    // re-download it on every keystroke of a search box.
    builder.Services.AddInstrumentMaster();
    builder.Services.Configure<InstrumentMasterOptions>(
        builder.Configuration.GetSection(InstrumentMasterOptions.SectionName));

    // ── Identity: accounts, sessions, and the saved-broker-credential vault. ──────────────────
    //
    // Postgres is the only persisted store in the application so far, and that is deliberate:
    // orders and risk policies can be rebuilt from the broker on restart, whereas a user's
    // account and the credentials they asked us to remember cannot be rebuilt from anything.
    var identityConnection = builder.Configuration.GetConnectionString("Identity")
        ?? "Host=localhost;Port=5432;Database=akshaya;Username=akshaya;Password=akshaya";

    // The MIGRATIONS live here, not in the module. The module owns the model and stays
    // provider-agnostic — which is what lets its tests run against SQLite or the in-memory
    // provider — while the composition root owns the choice of Postgres and the
    // Postgres-specific DDL that choice generates.
    builder.Services.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(
        identityConnection,
        npgsql => npgsql
            .MigrationsAssembly(typeof(Program).Assembly.FullName)
            .MigrationsHistoryTable("__ef_migrations", IdentityDbContext.SchemaName)));

    builder.Services.Configure<CredentialProtectionOptions>(
        builder.Configuration.GetSection(CredentialProtectionOptions.SectionName));

    builder.Services.AddIdentityModule();

    // ── Authentication: an HTTP-only session cookie. ──────────────────────────────────────────
    //
    // A cookie rather than a bearer token in localStorage, because behind this session sit
    // saved broker credentials: a token JavaScript can read is a token an XSS bug can exfiltrate.
    // SameSite=Lax is enough here — every state-changing call is a fetch() from our own origin,
    // and Lax already blocks the cross-site form POST that CSRF needs.
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "akshaya.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;

            // Always over HTTPS in a deployment; relaxed for the plain-HTTP dev server, which
            // is the only place a Secure cookie would silently never be set.
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;

            // This is an API: an unauthenticated XHR must get a status the Angular app can act
            // on, not a 302 to a login page that does not exist server-side.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

    // FAIL CLOSED. Every endpoint requires an authenticated user unless it explicitly opts out
    // with AllowAnonymous. The alternative — remembering RequireAuthorization on each new
    // endpoint — fails open, and the endpoint someone forgets is the one that places orders.
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserAccessor, ClaimsCurrentUserAccessor>();

    // ── Broker-link auth-flow state, and FluentValidation validators for this project's own
    // Contracts. Not registered via AddValidatorsFromAssemblyContaining: this csproj references
    // FluentValidation and FluentValidation.AspNetCore but not
    // FluentValidation.DependencyInjectionExtensions (unlike the Trading module, which does), so
    // that assembly-scanning extension method is not available here — each validator is
    // registered individually instead. ────────────────────────────────────────────────────────
    builder.Services.AddSingleton<PendingLinkAuthStore>();
    builder.Services.AddScoped<IValidator<RegisterRequestDto>, RegisterRequestDtoValidator>();
    builder.Services.AddScoped<IValidator<SignInRequestDto>, SignInRequestDtoValidator>();
    builder.Services.AddScoped<IValidator<BeginLinkRequestDto>, BeginLinkRequestDtoValidator>();
    builder.Services.AddScoped<IValidator<ContinueLinkRequestDto>, ContinueLinkRequestDtoValidator>();
    builder.Services.AddScoped<IValidator<PlaceOrderRequestDto>, PlaceOrderRequestDtoValidator>();
    builder.Services.AddScoped<IValidator<ModifyOrderRequestDto>, ModifyOrderRequestDtoValidator>();
    builder.Services.AddScoped<IValidator<RiskPolicyDto>, RiskPolicyDtoValidator>();
    builder.Services.AddScoped<IValidator<KillSwitchRequestDto>, KillSwitchRequestDtoValidator>();

    // ── CORS: the Angular dev server only. AllowCredentials is required for SignalR's
    // negotiate handshake, which is why this cannot be AllowAnyOrigin. ──────────────────────────
    var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:4200";
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

    // ── SignalR. ──────────────────────────────────────────────────────────────────────────────
    // MISMATCH WORTH FLAGGING: the brief asks for SignalR with MessagePack, but
    // Akshaya.Api.csproj references neither Microsoft.AspNetCore.SignalR.Protocols.MessagePack
    // nor MessagePack itself, and this file may not edit the csproj. AddMessagePackProtocol()
    // would not compile without that package reference. Registered here with the JSON protocol
    // only, sharing the exact same converters as the HTTP surface (AkshayaJson.Configure) so a
    // Tick over the socket serialises identically to a Quote over HTTP.
    // TODO: add the MessagePack protocol package reference and call .AddMessagePackProtocol()
    // once it is available; no other change is required — clients that speak MessagePack and
    // clients that speak JSON negotiate independently.
    builder.Services
        .AddSignalR()
        .AddJsonProtocol(options => AkshayaJson.Configure(options.PayloadSerializerOptions));

    builder.Services.AddSingleton<SubscriptionRegistry>();

    // ── HTTP JSON: the same converters as the SignalR hub protocol above, for the reason given
    // on AkshayaJson.Configure's own doc comment. ────────────────────────────────────────────────
    builder.Services.ConfigureHttpJsonOptions(options => AkshayaJson.Configure(options.SerializerOptions));

    // ── Health checks. ────────────────────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddCheck<ConnectorCatalogHealthCheck>("connectors");

    // ── OpenTelemetry: tracing and metrics across the API, the connector decorators and any
    // outbound HTTP a connector makes — the only way to answer "where did those 900ms go" after
    // the fact. The OTLP exporter fails silently (with a logged warning) if the collector is
    // unreachable, which is the right default for a local dev box with nothing listening on
    // 4317; it is never fatal to startup. ────────────────────────────────────────────────────
    var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: "Akshaya.Api",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
        .WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddHttpClientInstrumentation();
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
            }
        })
        .WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddHttpClientInstrumentation();
            metrics.AddRuntimeInstrumentation();
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                metrics.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
            }
        });

    // ── ProblemDetails + OpenAPI + Scalar. ────────────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Force the credential cipher to be constructed NOW, so a missing or malformed master key
    // fails the deploy rather than the first user who ticks "remember this". The cipher
    // validates its whole key set in its constructor; resolving it is the whole check.
    _ = app.Services.GetRequiredService<Akshaya.Modules.Identity.Ports.ICredentialCipher>();

    // Load the connector catalog once, at startup, before any request can ask for a manifest.
    // FailFastOnPluginError is off by default (see above), so a broken third-party plugin is
    // recorded and surfaced via the "connectors" health check rather than taking the host down.
    var catalog = app.Services.GetRequiredService<ConnectorCatalog>();
    var catalogLoad = await catalog.LoadAsync();
    if (catalogLoad.IsFailure)
    {
        Log.Fatal("Connector catalog failed to load: {Error}", catalogLoad.Error);
        throw new InvalidOperationException(catalogLoad.Error.Message);
    }

    app.UseSerilogRequestLogging();

    // A last-resort net for anything that escapes the Result-based error handling every endpoint
    // is written to use. Expected failures never reach this — they return Results.Problem via
    // ProblemDetailsMapper directly — so reaching here is itself a bug worth a 500 that still
    // comes back as RFC 7807 rather than a raw stack trace.
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        var problem = ProblemDetailsMapper.ToProblem(new Error(
            ConnectorErrorCodes.Unknown,
            "An unexpected error occurred. It has been logged."));

        await problem.ExecuteAsync(context);
    }));

    app.UseCors();

    // Order matters and is not arbitrary: CORS first (so a rejected pre-flight never reaches
    // auth), then authentication to populate HttpContext.User, then authorization to enforce
    // RequireAuthorization() using it.
    app.UseAuthentication();
    app.UseAuthorization();

    // Probes are for the orchestrator, which has no session. They expose no tenant data.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true }).AllowAnonymous();

    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference();

    // Everything below is covered by the fallback policy above and needs a signed-in user;
    // sign-up and sign-in opt back out individually inside MapAccountEndpoints.
    app.MapAccountEndpoints();
    app.MapConnectorEndpoints();
    app.MapBrokerLinkEndpoints();
    app.MapOrderEndpoints();
    app.MapPortfolioEndpoints();
    app.MapMarketDataEndpoints();
    app.MapRiskEndpoints();

    app.MapHub<MarketDataHub>("/hubs/market-data");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Akshaya API terminated unexpectedly during startup");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// ================================================================================================
// Composition-root helpers below. Program.cs uses top-level statements, so — per the ordinary
// C# rule for such files — everything below lives in the global namespace rather than a
// file-scoped one; every other file in this project uses a file-scoped namespace as required.
// ================================================================================================

/// <summary>Reads and validates an embedded <c>connector.manifest.json</c> straight out of a connector assembly.</summary>
static ConnectorManifest LoadEmbeddedManifest(Assembly assembly, string label)
{
    using var stream = assembly.GetManifestResourceStream(ManifestLoader.FileName)
        ?? throw new InvalidOperationException(
            $"The {label} connector's embedded {ManifestLoader.FileName} was not found in {assembly.FullName}.");

    using var reader = new StreamReader(stream);
    var json = reader.ReadToEnd();

    var result = ManifestLoader.Parse(json, $"embedded:{label}");
    if (result.IsFailure)
    {
        throw new InvalidOperationException($"The {label} connector's manifest failed validation: {result.Error}");
    }

    return result.Value;
}

/// <summary>
/// Finds every <see cref="IConnectorPlugin"/> compiled into this deployment by loading the
/// connector assemblies that sit next to the API and scanning them for the entry-point
/// interface. This is the same shape the on-disk plugin loader uses; it names no broker.
/// </summary>
static IReadOnlyList<IConnectorPlugin> DiscoverInProcessPlugins()
{
    var plugins = new List<IConnectorPlugin>();
    var baseDir = AppContext.BaseDirectory;

    foreach (var dll in Directory.EnumerateFiles(baseDir, "Akshaya.Connector.*.dll"))
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(dll);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            continue;
        }

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!typeof(IConnectorPlugin).IsAssignableFrom(type)
                || type is { IsAbstract: true } or { IsInterface: true }
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            plugins.Add((IConnectorPlugin)Activator.CreateInstance(type)!);
        }
    }

    return plugins;
}

/// <summary>
/// Activates (or reuses) the Paper connector for one account.
///
/// THE REUSE IS LOAD-BEARING, NOT AN OPTIMISATION. <see cref="Akshaya.Modules.Trading.Application.BrokerLinkResolver"/>
/// creates a fresh connector on every call and requires the caller to dispose it — correct for
/// every real broker, where the state lives at the venue and the connector is just a client. The
/// Paper connector is the one exception: its <c>MatchingEngine</c> holds the whole simulated book
/// (positions, working orders, fills) in process memory, so a fresh instance per HTTP request
/// would reset a paper account back to zero on every call. The fix is to activate exactly one
/// <see cref="PaperConnector"/> per account and hand out a non-disposing proxy over it — see
/// <see cref="NonDisposingConnectorProxy"/> — so BrokerLinkResolver's own disposal contract is
/// honoured (the caller's `await using` still runs) without tearing down the shared state.
/// </summary>
static Result<IBrokerConnector> CreatePaperConnector(
    ConnectorActivationContext context,
    ConcurrentDictionary<string, PaperConnector> cache)
{
    if (context.Session is null)
    {
        // The login handshake needs no persistent state — PaperAuth completes on its first
        // call — so the unauthenticated instance is never cached or shared.
        var anonymousLogger = context.LoggerFactory.CreateLogger<PaperConnector>();
        var anonymousSource = new DevPaperMarketDataSource(context.Clock);
        return Result<IBrokerConnector>.Success(PaperConnector.CreateUnauthenticated(
            context.Manifest, anonymousSource, new PaperOptions(), anonymousLogger, context.Clock));
    }

    var accountId = context.Session.AccountId;
    var connector = cache.GetOrAdd(accountId, _key =>
    {
        var logger = context.LoggerFactory.CreateLogger<PaperConnector>();
        var source = new DevPaperMarketDataSource(context.Clock);
        var created = new PaperConnector(context.Manifest, context.Session, source, new PaperOptions(), logger, context.Clock);

        // Drives the simulated tape for as long as the process lives, exactly once per account
        // — never once per activation, which is the whole point of the cache above.
        _ = RunPaperTapeAsync(created, accountId, logger);
        return created;
    });

    return Result<IBrokerConnector>.Success(new NonDisposingConnectorProxy(connector));
}

static async Task RunPaperTapeAsync(
    PaperConnector connector,
    string accountId,
    Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        var result = await connector.Engine.RunAsync(CancellationToken.None);
        if (result.IsFailure)
        {
            logger.LogWarning(
                "The paper market-data tape for account {AccountId} ended: {Error}",
                accountId,
                result.Error);
        }
    }
    catch (Exception ex)
    {
        // This background pump must never take the process down with it; a dead tape means
        // that account's resting orders stop filling, not that the API stops answering.
        logger.LogError(ex, "The paper connector's background tape consumption crashed for account {AccountId}.", accountId);
    }
}

/// <summary>Dev-only venue calendars covering the venues the built-in connectors and the paper simulator claim.</summary>
static IReadOnlyDictionary<Venue, VenueCalendar> BuildDevTradingCalendars()
{
    var indiaSession = new TradingSession(new TimeOnly(9, 15), new TimeOnly(15, 30));
    var singaporeSession = new TradingSession(new TimeOnly(9, 0), new TimeOnly(17, 0));
    var usSession = new TradingSession(new TimeOnly(9, 30), new TimeOnly(16, 0));

    return new Dictionary<Venue, VenueCalendar>
    {
        [Venue.Nse] = new VenueCalendar { Venue = Venue.Nse, TimeZoneId = "Asia/Kolkata", Sessions = [indiaSession] },
        [Venue.Bse] = new VenueCalendar { Venue = Venue.Bse, TimeZoneId = "Asia/Kolkata", Sessions = [indiaSession] },
        [Venue.Sgx] = new VenueCalendar { Venue = Venue.Sgx, TimeZoneId = "Asia/Singapore", Sessions = [singaporeSession] },
        [Venue.Nasdaq] = new VenueCalendar { Venue = Venue.Nasdaq, TimeZoneId = "America/New_York", Sessions = [usSession] },
        [Venue.Nyse] = new VenueCalendar { Venue = Venue.Nyse, TimeZoneId = "America/New_York", Sessions = [usSession] },
    };
}

/// <summary>Marker type <c>WebApplicationFactory&lt;Program&gt;</c>-style integration tests can target.</summary>
public sealed partial class Program;

/// <summary>
/// Wraps a cached, long-lived connector so a caller's <c>await using</c> — the pattern every
/// other consumer of <c>IConnectorFactory</c> correctly follows — does not tear it down. See
/// <see cref="CreatePaperConnector"/> for why exactly one connector, Paper, needs this.
/// </summary>
internal sealed class NonDisposingConnectorProxy(IBrokerConnector inner) : IBrokerConnector
{
    public ConnectorManifest Manifest => inner.Manifest;

    public IConnectorAuth Auth => inner.Auth;

    public IConnectorOrders Orders => inner.Orders;

    public IConnectorPortfolio Portfolio => inner.Portfolio;

    public IConnectorMarketData MarketData => inner.MarketData;

    public IConnectorReference Reference => inner.Reference;

    public IConnectorStream? Stream => inner.Stream;

    public Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);

    /// <summary>Deliberately a no-op. The wrapped connector is torn down only when it is evicted from the process-lifetime cache.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// DEV-ONLY synthetic tick source for the built-in Paper connector: a handful of NSE equities
/// walking on a fixed seed. A real deployment injects a live feed (for paper trading proper) or
/// a recorded tape (for the backtester) — see <see cref="IMarketDataSource"/>'s own remarks. This
/// exists so the API is exercisable end to end with no external market-data subscription.
/// </summary>
internal sealed class DevPaperMarketDataSource : IMarketDataSource
{
    private static readonly (string Symbol, decimal Price)[] Seed =
    [
        ("RELIANCE", 2900.00m),
        ("TCS", 3850.00m),
        ("INFY", 1550.00m),
        ("HDFCBANK", 1650.00m),
    ];

    private readonly IClock _clock;
    private readonly Random _random = new(20260901); // fixed seed: reproducible dev prices across runs
    private readonly Dictionary<InstrumentKey, InstrumentDefinition> _definitions;
    private readonly ConcurrentDictionary<InstrumentKey, decimal> _prices;

    public DevPaperMarketDataSource(IClock clock)
    {
        _clock = clock;

        _definitions = Seed.ToDictionary(
            s => Key(s.Symbol),
            s => new InstrumentDefinition
            {
                Key = Key(s.Symbol),
                Name = s.Symbol,
                Currency = Currency.Inr,
                LotSize = 1m,
                TickSize = 0.05m,
                Multiplier = 1m,
            });

        _prices = new ConcurrentDictionary<InstrumentKey, decimal>(
            Seed.ToDictionary(s => Key(s.Symbol), s => s.Price));
    }

    public IReadOnlyList<InstrumentDefinition> Instruments => [.. _definitions.Values];

    public Result<InstrumentDefinition> Resolve(InstrumentKey key) =>
        _definitions.TryGetValue(key, out var definition)
            ? definition
            : Result<InstrumentDefinition>.Failure(ConnectorErrors.InstrumentNotFound(key));

    public Result<Money> LastPrice(InstrumentKey key) =>
        _prices.TryGetValue(key, out var price)
            ? new Money(price, Currency.Inr)
            : Result<Money>.Failure(ConnectorErrors.InstrumentNotFound(key));

    public Result<CandleSeries> History(HistoryRequest request)
    {
        if (!_prices.TryGetValue(request.Instrument, out var basePrice))
        {
            return Result<CandleSeries>.Failure(ConnectorErrors.InstrumentNotFound(request.Instrument));
        }

        var step = request.TimeFrame.ToTimeSpan();
        var candles = new List<Candle>();
        var price = basePrice;

        // Seeded from the instrument and timeframe rather than shared mutable state, so calling
        // History twice for the same series returns the same synthetic candles — a backtest or
        // a chart re-render must not see the tape rewritten under it.
        var rng = new Random(HashCode.Combine(request.Instrument.ToString(), request.TimeFrame));

        for (var open = request.From; open < request.To; open += step)
        {
            var changePercent = ((decimal)rng.NextDouble() - 0.5m) * 0.02m;
            var close = Math.Max(0.05m, price * (1 + changePercent));
            var high = Math.Max(price, close) * (1 + (decimal)rng.NextDouble() * 0.002m);
            var low = Math.Min(price, close) * (1 - (decimal)rng.NextDouble() * 0.002m);

            candles.Add(new Candle
            {
                OpenTime = open,
                Open = price,
                High = high,
                Low = low,
                Close = close,
                Volume = rng.Next(1_000, 50_000),
            });

            price = close;
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = Currency.Inr,
            Candles = candles,
        };
    }

    public async IAsyncEnumerable<Tick> Ticks([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var key in _definitions.Keys)
            {
                var previous = _prices[key];
                var changePercent = ((decimal)_random.NextDouble() - 0.5m) * 0.004m;
                var next = Math.Max(0.05m, previous * (1 + changePercent));
                _prices[key] = next;

                yield return new Tick
                {
                    Instrument = key,
                    LastPrice = new Money(next, Currency.Inr),
                    LastQuantity = new Quantity(_random.Next(1, 100)),
                    Volume = _random.Next(1_000, 100_000),
                    BidPrice = new Money(Math.Round(next * 0.999m, 2), Currency.Inr),
                    AskPrice = new Money(Math.Round(next * 1.001m, 2), Currency.Inr),
                    PreviousClose = new Money(previous, Currency.Inr),
                    Timestamp = _clock.UtcNow,
                };
            }

            var delay = Task.Delay(TimeSpan.FromSeconds(1), ct);
            try
            {
                await delay;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private static InstrumentKey Key(string symbol) => new(Venue.Nse, symbol, AssetClass.Equity);
}

/// <summary>
/// DEV-ONLY adapter from the Trading module's broker-link store to the Portfolio module's own,
/// deliberately thin, link port. The Portfolio module must not depend on Trading's link
/// lifecycle (see <see cref="IPortfolioLinkProvider"/>'s remarks) — this is the one place that
/// bridges them, and it is exactly what a future BrokerLink module replaces both stores with.
/// </summary>
internal sealed class BrokerLinkPortfolioProvider(IBrokerLinkStore links) : IPortfolioLinkProvider
{
    public async Task<IReadOnlyList<PortfolioLink>> GetLinksAsync(
        string tenantId,
        string userId,
        CancellationToken ct = default)
    {
        var all = await links.ListAsync(tenantId, userId, ct);

        return [.. all
            .Where(l => l.IsUsable)
            .Select(l => new PortfolioLink(l.Id, l.TenantId, l.UserId, l.ConnectorId, l.Nickname ?? l.ConnectorId, l.Session!))];
    }
}

/// <summary>Dev-configurable defaults for the portfolio endpoints.</summary>
public sealed class PortfolioOptions
{
    /// <summary>Currency the blended snapshot displays in when the caller does not ask for a specific one.</summary>
    public string DefaultDisplayCurrency { get; set; } = "INR";
}

/// <summary>
/// The authenticated caller's tenant and user id.
///
/// Every endpoint depends on this abstraction rather than on <c>HttpContext.User</c> directly,
/// which is what made swapping the old header-trusting dev stub for
/// <see cref="ClaimsCurrentUserAccessor"/> a one-line change in the composition root.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>The tenant the caller is acting for.</summary>
    string TenantId { get; }

    /// <summary>The caller's own user id within that tenant.</summary>
    string UserId { get; }

    /// <summary>
    /// False for an anonymous caller, where <see cref="UserId"/> and <see cref="TenantId"/>
    /// are empty. Endpoints behind <c>RequireAuthorization()</c> never see false; the handful
    /// that are deliberately anonymous (<c>/api/account/me</c>) check it.
    /// </summary>
    bool IsAuthenticated { get; }
}

/// <summary>
/// Reads the tenant and user id from the authenticated principal's claims.
///
/// The claims come from the signed session cookie and from nowhere else. The tenant in
/// particular is never taken from a header or a request body: every store below is
/// tenant-scoped, so a tenant a caller can name is a tenant a caller can read.
/// </summary>
internal sealed class ClaimsCurrentUserAccessor : ICurrentUserAccessor
{
    public ClaimsCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        (TenantId, UserId, IsAuthenticated) = AkshayaIdentity.Resolve(httpContextAccessor.HttpContext?.User);
    }

    public string TenantId { get; }

    public string UserId { get; }

    public bool IsAuthenticated { get; }
}

/// <summary>
/// Shared by <see cref="ClaimsCurrentUserAccessor"/> and <see cref="Akshaya.Api.Hubs.MarketDataHub"/>.
///
/// The hub cannot use <see cref="IHttpContextAccessor"/> reliably: SignalR hub method
/// invocations do not run inside the original HTTP request's ambient context once the connection
/// is established, so the ambient <c>HttpContext</c> it exposes is typically null there. The hub
/// reads the principal off <c>HubCallerContext.User</c> instead. This type is the one place that
/// decides what a principal's identity is, so both call sites agree.
/// </summary>
internal static class AkshayaIdentity
{
    public static (string TenantId, string UserId, bool IsAuthenticated) Resolve(ClaimsPrincipal? principal)
    {
        var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantId = principal?.FindFirstValue(AkshayaClaims.TenantId);

        // Both claims or neither. A principal carrying a user id but no tenant would otherwise
        // fall through to an empty tenant string and quietly read another tenant's empty set
        // instead of failing.
        return principal?.Identity?.IsAuthenticated == true
               && !string.IsNullOrWhiteSpace(userId)
               && !string.IsNullOrWhiteSpace(tenantId)
            ? (tenantId, userId, true)
            : (string.Empty, string.Empty, false);
    }
}

/// <summary>Readiness signal for the connector layer: degraded the moment any connector — built-in or plugin — failed to load.</summary>
internal sealed class ConnectorCatalogHealthCheck(ConnectorCatalog catalog) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failures = catalog.Failures;

        var result = failures.Count == 0
            ? HealthCheckResult.Healthy($"{catalog.ConnectorIds.Count} connector(s) loaded.")
            : HealthCheckResult.Degraded(
                $"{failures.Count} connector(s) failed to load: "
                + string.Join(", ", failures.Select(f => f.ConnectorId ?? f.Location)));

        return Task.FromResult(result);
    }
}

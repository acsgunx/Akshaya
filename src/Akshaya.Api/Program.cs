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
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Akshaya.Api.Contracts;
using Akshaya.Api.Endpoints;
using Akshaya.Api.Hubs;
using Akshaya.Api.Infrastructure;
using Akshaya.Api.Infrastructure.Persistence;
using Akshaya.Connector.Paper;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Host;
using Akshaya.Connectors.Sdk;
using Akshaya.Modules.Identity;
using Akshaya.Modules.Identity.Infrastructure;
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
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
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

        // Where operator-run gateway daemons listen, keyed by the gateway id a manifest declares.
        // Empty is a working default for local development: each gateway is then looked for on
        // loopback at its manifest's port. See ADR 0008.
        builder.Configuration.GetSection("Connectors:Gateways").Bind(options.Gateways);

        // Per-connector settings, keyed by connector id then setting name — a region, a timeout, a
        // quota. Never credentials: those arrive through the link flow. See ADR 0008.
        foreach (var connector in builder.Configuration.GetSection("Connectors:Settings").GetChildren())
        {
            options.Settings[connector.Key] = connector.GetChildren()
                .Where(setting => setting.Value is not null)
                .ToDictionary(setting => setting.Key, setting => setting.Value!, StringComparer.OrdinalIgnoreCase);
        }

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

    // ONE OUTBOUND CONNECTION POOL PER CONNECTOR, FOR THE LIFE OF THE PROCESS.
    //
    // Connectors are request-scoped by design, and before this each activation also built and
    // then disposed its own HttpClient — so every portfolio refresh, every quote and every
    // order paid a fresh DNS lookup, TCP handshake and TLS handshake to the broker before it
    // could send anything. That handshake is the single largest avoidable cost on the whole
    // read path. The pool is handed to connectors through ConnectorActivationContext, which
    // has always had the hook for it; nothing was filling it in.
    builder.Services.AddSingleton<ConnectorHttpClientPool>();
    builder.Services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
    builder.Services.AddSingleton<IConnectorAuditSink, LoggingConnectorAuditSink>();
    // Gateways are run by the operator and probed here, not launched by this process. A runtime
    // that starts a container per credential would replace this one line. See ADR 0008.
    builder.Services.AddSingleton<IGatewayRuntime, ConfiguredGatewayRuntime>();
    builder.Services.AddSingleton<IGatewaySupervisor, GatewaySupervisor>();
    builder.Services.AddSingleton<IConnectorFactory, ConnectorFactory>();

    // Calls Auth.KeepAliveAsync on the interval each manifest declares. Reads only the manifest,
    // so it names no broker; a connector without keepAliveInterval is never touched.
    builder.Services.AddHostedService<ConnectorKeepAliveService>();

    // ── Trading core + Portfolio module. ─────────────────────────────────────────────────────
    builder.Services.AddTradingCore();

    // Polls every linked broker's order book: corrects what the broker disagrees with, and
    // adopts orders placed outside Akshaya (the broker's own app or website), which otherwise
    // never reach the blotter.
    builder.Services.AddHostedService<ReconciliationHostedService>();
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
    // The ONLY persisted store in the application, and that is deliberate: orders, positions and
    // risk policies can all be rebuilt from the broker on restart, whereas a user's account and
    // the credentials they asked us to remember cannot be rebuilt from anything.
    //
    // Because it is the only one, it is also the only reason a deployment would need a database
    // SERVER — so which store backs it is a configuration choice rather than a compile-time one.
    // Persistence:Mode selects a SQLite file (the default: no infrastructure to run and nothing
    // to pay for), SQLite held in memory, or Postgres for an enterprise deployment. See
    // Infrastructure/Persistence/PersistenceOptions.cs for what each one costs you.
    var persistence = builder.Services.AddAkshayaPersistence(
        builder.Configuration,
        builder.Environment.ContentRootPath);

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
    builder.Services.AddScoped<IValidator<ConvertPositionRequestDto>, ConvertPositionRequestDtoValidator>();
    builder.Services.AddScoped<IValidator<RiskPolicyDto>, RiskPolicyDtoValidator>();
    builder.Services.AddScoped<IValidator<KillSwitchRequestDto>, KillSwitchRequestDtoValidator>();

    // ── CORS: the Angular dev server only. AllowCredentials is required for SignalR's
    // negotiate handshake, which is why this cannot be AllowAnyOrigin. ──────────────────────────
    //
    // Set Cors:AllowedOrigin to "" or "none" to switch the whole thing off. That is what the
    // single-container deployments do: when this process also serves the Angular bundle (see
    // the SPA block further down), every request the browser makes is same-origin and a CORS
    // policy is not a loosened restriction so much as a header nobody reads.
    var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:4200";
    var corsEnabled = !string.IsNullOrWhiteSpace(allowedOrigin)
        && !string.Equals(allowedOrigin, "none", StringComparison.OrdinalIgnoreCase);

    if (corsEnabled)
    {
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(allowedOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));
    }

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

    // ── Rate limiting, for the two endpoints that accept a password from anyone. ──────────────
    //
    // Sign-in and sign-up are anonymous by necessity, and behind the accounts they guard sit
    // saved broker credentials. Without a limiter, guessing a password is bounded only by how
    // fast requests can be sent, which is the whole attack.
    //
    // PARTITIONED BY THE CLIENT ADDRESS, and getting that address right is the load-bearing part.
    // Behind App Service the connection comes from the platform's front end, so
    // RemoteIpAddress is the same value for every caller and a limiter keyed on it throttles
    // everyone together the moment one attacker starts guessing. The client address is in
    // X-Forwarded-For, and the LAST entry is the one the platform observed — anything to its
    // left was supplied by the caller and can say whatever it likes.
    //
    // A caller who can vary that last entry defeats this, which is why it is a speed bump for
    // guessing rather than an authentication control. Raising MinimumPasswordLength is what
    // makes the space too large to walk; this makes walking it slow.
    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        limiter.AddPolicy(AccountEndpoints.AuthRateLimitPolicy, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    // Ten attempts per five minutes. A person who has mistyped their password
                    // three times is not helped by a fourth attempt arriving faster; a script
                    // working through a keyspace is stopped dead.
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                }));

        // Say when to come back rather than only that the door is shut, and log it: a burst of
        // these on sign-in is the signal that someone is being guessed at.
        limiter.OnRejected = (context, ct) =>
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)TimeSpan.FromMinutes(5).TotalSeconds).ToString(CultureInfo.InvariantCulture);

            Log.Warning(
                "Rate limit rejected {Method} {Path} from {Client}.",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                ClientAddress(context.HttpContext));

            return ValueTask.CompletedTask;
        };
    });

    // ── Response compression. ─────────────────────────────────────────────────────────────────
    //
    // The portfolio snapshot is the biggest thing this API returns and the most repetitive: one
    // row per blended position, each carrying a leg per broker, each leg carrying several Money
    // objects that serialise as {"amount":…,"currency":…}. That shape compresses by roughly an
    // order of magnitude, and on a phone on mobile data the transfer is a visible part of how
    // long the dashboard spends showing a spinner. The single-container deployments serve the
    // Angular bundle from this same pipeline, so they get it on the bundle too.
    //
    // EnableForHttps is on, and that is a deliberate BREACH trade-off rather than an oversight.
    // Leaving it off would mean compression never applies in any real deployment, since they
    // are all HTTPS. BREACH needs a secret in the RESPONSE BODY next to attacker-controlled
    // content; this API keeps its session in an HttpOnly cookie, issues no CSRF token in a
    // body, and never echoes one user's secret into another's response. If a body-borne token
    // is ever added, this flag is the line to revisit.
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();

        // The defaults omit JSON's +json suffixes and the SPA's own asset types.
        options.MimeTypes =
        [
            .. ResponseCompressionDefaults.MimeTypes,
            "application/json",
            "application/problem+json",
            "application/javascript",
            "text/javascript",
            "image/svg+xml",
        ];
    });

    // Fastest, not Optimal. Brotli's default quality is 11, which spends more CPU per response
    // than the bytes it saves are worth on an API whose payloads are tens of kilobytes — and
    // that CPU is on the request's own critical path.
    builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

    // ── ProblemDetails + OpenAPI + Scalar. ────────────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Force the credential cipher to be constructed NOW, so a missing or malformed master key
    // fails the deploy rather than the first user who ticks "remember this". The cipher
    // validates its whole key set in its constructor; resolving it is the whole check.
    _ = app.Services.GetRequiredService<Akshaya.Modules.Identity.Ports.ICredentialCipher>();

    // Create or migrate the identity schema, and seed the first account if the store is empty.
    // Done in-process because the deployments this supports have no shell to run a migration
    // job from — the container starting IS the deployment.
    Log.Information("Identity persistence mode: {Mode}", persistence.Mode);
    await IdentityStoreInitialiser.InitialiseAsync(app.Services);

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

    if (corsEnabled)
    {
        app.UseCors();
    }

    // Before the static-file and endpoint middleware, so it covers both the API's JSON and the
    // Angular bundle in the single-container deployments.
    app.UseResponseCompression();

    // ── The Angular app, served from this origin when it has been published into wwwroot. ─────
    //
    // Present only in a container build, where the Dockerfile drops `ng build` output into
    // wwwroot; a developer running `ng serve` on :4200 has no wwwroot and this whole block stays
    // off, so the dev loop is untouched. Co-hosting is what makes a one-service deployment
    // possible: one process, one TLS certificate, one bill, and no CORS.
    var webRoot = app.Environment.WebRootPath
        ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var spaEnabled = File.Exists(Path.Combine(webRoot, "index.html"));

    if (spaEnabled)
    {
        // Static files BEFORE authentication. The bundle is public by definition — it is what
        // renders the sign-in form — and running it through the fallback authorization policy
        // would 401 the very page a signed-out user needs.
        app.UseDefaultFiles();
        app.UseStaticFiles();
    }

    // Order matters and is not arbitrary: CORS first (so a rejected pre-flight never reaches
    // auth), then authentication to populate HttpContext.User, then authorization to enforce
    // RequireAuthorization() using it.
    app.UseAuthentication();
    app.UseAuthorization();

    // After authentication so a rejected request is still attributable in the log, and before the
    // endpoints it protects.
    app.UseRateLimiter();

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

    if (spaEnabled)
    {
        // An unmatched /api or /hubs path is a bug or a probe, and must answer 404. Without
        // these two the SPA fallback below would hand it index.html with a 200, which turns
        // every typo'd endpoint into a client-side JSON parse error instead of a status code.
        app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
        app.MapFallback("/hubs/{**rest}", () => Results.NotFound()).AllowAnonymous();

        // Everything else is an Angular route: deep links and refreshes must return index.html
        // and let the client router resolve them. AllowAnonymous because the fallback
        // authorization policy applies to endpoints, and this one has to serve signed-out users.
        app.MapFallbackToFile("index.html").AllowAnonymous();
    }

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

// Reads and validates an embedded connector.manifest.json straight out of a connector assembly.
// The caller's address, as the hosting platform saw it.
//
// X-Forwarded-For is a list that grows left to right, and every entry except the last was put
// there by someone upstream of us — including, potentially, the caller. The LAST entry is the one
// added by the proxy that actually accepted the connection, so that is the only one worth keying
// a limit on. With no header at all (a direct connection, or a local run) the socket's own remote
// address is both available and honest.
static string ClientAddress(HttpContext http)
{
    var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwarded))
    {
        var last = forwarded.Split(',')[^1].Trim();
        if (last.Length > 0)
        {
            // App Service writes "<ip>:<port>"; the port is per-connection and would make every
            // request its own partition, which is the same as having no limit at all.
            var colon = last.LastIndexOf(':');
            return colon > 0 && !last.Contains("::", StringComparison.Ordinal)
                ? last[..colon]
                : last;
        }
    }

    return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

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

//
// Finds every IConnectorPlugin compiled into this deployment by loading the
// connector assemblies that sit next to the API and scanning them for the entry-point
// interface. This is the same shape the on-disk plugin loader uses; it names no broker.
//
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

//
// Activates (or reuses) the Paper connector for one account.
//
// THE REUSE IS LOAD-BEARING, NOT AN OPTIMISATION. Akshaya.Modules.Trading.Application.BrokerLinkResolver
// creates a fresh connector on every call and requires the caller to dispose it — correct for
// every real broker, where the state lives at the venue and the connector is just a client. The
// Paper connector is the one exception: its MatchingEngine holds the whole simulated book
// (positions, working orders, fills) in process memory, so a fresh instance per HTTP request
// would reset a paper account back to zero on every call. The fix is to activate exactly one
// PaperConnector per account and hand out a non-disposing proxy over it — see
// NonDisposingConnectorProxy — so BrokerLinkResolver's own disposal contract is
// honoured (the caller's `await using` still runs) without tearing down the shared state.
//
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

// Dev-only venue calendars covering the venues the built-in connectors and the paper simulator claim.
static IReadOnlyDictionary<Venue, VenueCalendar> BuildDevTradingCalendars()
{
    var indiaSession = new TradingSession(new TimeOnly(9, 15), new TimeOnly(15, 30));
    var singaporeSession = new TradingSession(new TimeOnly(9, 0), new TimeOnly(17, 0));
    var usSession = new TradingSession(new TimeOnly(9, 30), new TimeOnly(16, 0));

    // HKEX breaks for lunch, so its day is two regular sessions around a break. One 09:30–16:00
    // session would call the market open at 12:30, when an order is rejected at the venue.
    TradingSession[] hongKongSessions =
    [
        new(new TimeOnly(9, 30), new TimeOnly(12, 0)),
        new(new TimeOnly(12, 0), new TimeOnly(13, 0), SessionKind.Break),
        new(new TimeOnly(13, 0), new TimeOnly(16, 0)),
    ];

    // The listing venues of most US ETFs. They trade the same core hours as NYSE and Nasdaq, and
    // without a row here the risk gate treats SPY (NYSE Arca) as a closed market.
    var nyseArca = new Venue("ARCX");
    var cboeBzx = new Venue("BATS");
    var nyseAmerican = new Venue("XASE");

    return new Dictionary<Venue, VenueCalendar>
    {
        [Venue.Nse] = new VenueCalendar { Venue = Venue.Nse, TimeZoneId = "Asia/Kolkata", Sessions = [indiaSession] },
        [Venue.Bse] = new VenueCalendar { Venue = Venue.Bse, TimeZoneId = "Asia/Kolkata", Sessions = [indiaSession] },
        [Venue.Sgx] = new VenueCalendar { Venue = Venue.Sgx, TimeZoneId = "Asia/Singapore", Sessions = [singaporeSession] },
        [Venue.Nasdaq] = new VenueCalendar { Venue = Venue.Nasdaq, TimeZoneId = "America/New_York", Sessions = [usSession] },
        [Venue.Nyse] = new VenueCalendar { Venue = Venue.Nyse, TimeZoneId = "America/New_York", Sessions = [usSession] },
        [nyseArca] = new VenueCalendar { Venue = nyseArca, TimeZoneId = "America/New_York", Sessions = [usSession] },
        [cboeBzx] = new VenueCalendar { Venue = cboeBzx, TimeZoneId = "America/New_York", Sessions = [usSession] },
        [nyseAmerican] = new VenueCalendar { Venue = nyseAmerican, TimeZoneId = "America/New_York", Sessions = [usSession] },
        [Venue.Hkex] = new VenueCalendar { Venue = Venue.Hkex, TimeZoneId = "Asia/Hong_Kong", Sessions = hongKongSessions },
    };
}

/// <summary>Marker type <c>WebApplicationFactory&lt;Program&gt;</c>-style integration tests can target.</summary>
public sealed partial class Program;

/// <summary>
/// Wraps a cached, long-lived connector so a caller's <c>await using</c> — the pattern every
/// other consumer of <c>IConnectorFactory</c> correctly follows — does not tear it down. See
/// CreatePaperConnector for why exactly one connector, Paper, needs this.
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
/// a recorded tape (for the backtester) — see IMarketDataSource's own remarks. This
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
/// lifecycle (see IPortfolioLinkProvider's remarks) — this is the one place that
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
/// ClaimsCurrentUserAccessor a one-line change in the composition root.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>The tenant the caller is acting for.</summary>
    string TenantId { get; }

    /// <summary>The caller's own user id within that tenant.</summary>
    string UserId { get; }

    /// <summary>
    /// False for an anonymous caller, where UserId and TenantId
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
/// Shared by ClaimsCurrentUserAccessor and Akshaya.Api.Hubs.MarketDataHub.
///
/// The hub cannot use IHttpContextAccessor reliably: SignalR hub method
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

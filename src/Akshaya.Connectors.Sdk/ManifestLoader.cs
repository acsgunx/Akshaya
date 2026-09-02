using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// The version of the connector CONTRACT — the shape of
/// <see cref="Akshaya.Connectors.Abstractions.IBrokerConnector"/> and everything it touches.
///
/// Distinct from any assembly version. It changes only when the contract changes in a way a
/// connector author must react to, which is exactly when a third-party connector could break
/// on one of our deploys. The host accepts the current major and one behind, so a connector
/// built last quarter keeps working while its author catches up.
/// </summary>
public static class ConnectorContract
{
    /// <summary>Current contract version, "major.minor".</summary>
    public const string CurrentVersion = "1.0";

    /// <summary>
    /// Compatibility rule: same major, or exactly one major behind. Minor versions are
    /// additive-only by policy, so a connector built against 1.0 runs on host 1.7, and one
    /// built against 1.9 runs on host 1.0 as long as it does not use anything 1.0 lacks —
    /// which is the connector author's problem, not something we can check from a version
    /// string.
    /// </summary>
    public static bool IsCompatible(string manifestVersion, string hostVersion = CurrentVersion)
    {
        if (!TryParseMajorMinor(manifestVersion, out var manifestMajor, out _)
            || !TryParseMajorMinor(hostVersion, out var hostMajor, out _))
        {
            return false;
        }

        return manifestMajor == hostMajor || manifestMajor == hostMajor - 1;
    }

    /// <summary>Parses "1", "1.2" or "1.2.3"; anything else fails rather than being guessed at.</summary>
    public static bool TryParseMajorMinor(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major))
        {
            return false;
        }

        return parts.Length < 2
               || int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }
}

/// <summary>
/// Loads and validates <c>connector.manifest.json</c>.
///
/// Validation is strict and happens at LOAD time, before any connector is activated, because a
/// manifest is a promise the rest of the platform relies on: the order ticket renders from it,
/// the risk gate validates against it, the rate limiter is built from it, and the session
/// monitor computes expiry from it. A manifest that declares a venue as "NSE" instead of the
/// MIC "XNSE" does not fail loudly at load; it silently makes every NSE instrument
/// untradable through that broker, and someone spends a day finding out why.
///
/// Every failure names the offending field and what was expected. A connector author reading
/// the error should not have to open this file.
/// </summary>
public static partial class ManifestLoader
{
    /// <summary>The fixed file name. The catalog scans for exactly this.</summary>
    public const string FileName = "connector.manifest.json";

    /// <summary>Error context key listing every validation problem, newline separated.</summary>
    public const string ValidationDetailKey = "validationErrors";

    // Lower-case, dot/dash/underscore separated. Used in URLs, log fields, metric tags and
    // Redis keys, so anything exotic causes trouble somewhere downstream.
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectorIdPattern();

    // ISO 10383 MICs are exactly four uppercase alphanumerics. This is the check that stops
    // "NSE" and "NASDAQ" ever reaching the venue router.
    [GeneratedRegex("^[A-Z0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex MicPattern();

    // ISO 4217 alphabetic codes are three uppercase letters.
    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    // ISO 3166-1 alpha-2.
    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex JurisdictionPattern();

    /// <summary>Reads and validates a manifest file.</summary>
    public static Result<ConnectorManifest> LoadFromFile(
        string path,
        string hostContractVersion = ConnectorContract.CurrentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not read '{path}': {ex.Message}");
        }

        return Parse(json, path, hostContractVersion);
    }

    /// <summary>Async form, for the catalog scanning a plugin directory.</summary>
    public static async Task<Result<ConnectorManifest>> LoadFromFileAsync(
        string path,
        string hostContractVersion = ConnectorContract.CurrentVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not read '{path}': {ex.Message}");
        }

        return Parse(json, path, hostContractVersion);
    }

    /// <summary>Parses and validates manifest JSON. <paramref name="source"/> appears in errors.</summary>
    public static Result<ConnectorManifest> Parse(
        string json,
        string? source = null,
        string hostContractVersion = ConnectorContract.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(json);

        ConnectorManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ConnectorManifest>(json, ConnectorJson.Default);
        }
        catch (JsonException ex)
        {
            // JsonException carries the line and position; keeping its message verbatim is the
            // single most useful thing for whoever hand-edited the file.
            return Fail($"{Where(source)} is not valid manifest JSON: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            return Fail($"{Where(source)} could not be deserialised: {ex.Message}");
        }

        if (manifest is null)
        {
            return Fail($"{Where(source)} deserialised to null.");
        }

        var validation = Validate(manifest, hostContractVersion, source);
        return validation.IsSuccess ? Result<ConnectorManifest>.Success(manifest) : validation.Error;
    }

    /// <summary>
    /// Validates an already-materialised manifest. Public so an in-process connector that
    /// builds its manifest in C# gets the same checks as one that ships JSON.
    /// </summary>
    public static Result Validate(
        ConnectorManifest manifest,
        string hostContractVersion = ConnectorContract.CurrentVersion,
        string? source = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var errors = new List<string>();

        ValidateIdentity(manifest, hostContractVersion, errors);
        ValidateMarkets(manifest, errors);
        ValidateAuth(manifest.Auth, errors);
        ValidateOrders(manifest.Orders, errors);
        ValidateMarketData(manifest, errors);
        ValidateRateLimits(manifest, errors);
        ValidateHosting(manifest, errors);
        ValidateSandbox(manifest.Sandbox, errors);

        if (errors.Count == 0)
        {
            return Result.Success();
        }

        var detail = string.Join(Environment.NewLine, errors.Select(e => "  • " + e));
        return new Error(
            ConnectorErrorCodes.InvalidRequest,
            $"{Where(source)} is not a valid connector manifest ({errors.Count} problem"
            + $"{(errors.Count == 1 ? string.Empty : "s")}):{Environment.NewLine}{detail}",
            VendorCode: null,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ValidationDetailKey] = string.Join(Environment.NewLine, errors),
                ["connectorId"] = string.IsNullOrWhiteSpace(manifest.Id) ? "(missing)" : manifest.Id,
            });
    }

    private static void ValidateIdentity(ConnectorManifest manifest, string hostContractVersion, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add("'id' is required.");
        }
        else if (!ConnectorIdPattern().IsMatch(manifest.Id))
        {
            errors.Add(
                $"'id' must be 2–64 lower-case characters from [a-z0-9._-] and start with a letter or "
                + $"digit; got '{manifest.Id}'. It is used in URLs, metric tags and cache keys.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            errors.Add("'displayName' is required — the link wizard shows it to the user.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Vendor))
        {
            errors.Add("'vendor' is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ContractVersion))
        {
            errors.Add("'contractVersion' is required.");
        }
        else if (!ConnectorContract.TryParseMajorMinor(manifest.ContractVersion, out _, out _))
        {
            errors.Add(
                $"'contractVersion' must look like '1.0'; got '{manifest.ContractVersion}'.");
        }
        else if (!ConnectorContract.IsCompatible(manifest.ContractVersion, hostContractVersion))
        {
            errors.Add(
                $"'contractVersion' {manifest.ContractVersion} is not compatible with this host "
                + $"(contract {hostContractVersion}). The host accepts the current major version and one behind.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ConnectorVersion))
        {
            errors.Add("'connectorVersion' is required — support needs to know which build is deployed.");
        }
    }

    private static void ValidateMarkets(ConnectorManifest manifest, List<string> errors)
    {
        if (manifest.Jurisdictions is null || manifest.Jurisdictions.Count == 0)
        {
            errors.Add("'jurisdictions' must list at least one ISO 3166-1 alpha-2 country code.");
        }
        else
        {
            foreach (var jurisdiction in manifest.Jurisdictions)
            {
                if (!JurisdictionPattern().IsMatch(jurisdiction ?? string.Empty))
                {
                    errors.Add(
                        $"'jurisdictions' entry '{jurisdiction}' is not a 2-letter ISO 3166-1 alpha-2 code "
                        + "(for example 'IN', 'SG', 'US').");
                }
            }
        }

        if (manifest.Venues is null || manifest.Venues.Count == 0)
        {
            errors.Add("'venues' must list at least one ISO 10383 MIC.");
        }
        else
        {
            foreach (var venue in manifest.Venues)
            {
                if (!MicPattern().IsMatch(venue ?? string.Empty))
                {
                    errors.Add(
                        $"'venues' entry '{venue}' is not a 4-character ISO 10383 MIC. "
                        + "Use 'XNSE' not 'NSE', 'XNAS' not 'NASDAQ', 'XSES' not 'SGX'.");
                }
            }

            var duplicateVenues = manifest.Venues
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var duplicate in duplicateVenues)
            {
                errors.Add($"'venues' lists '{duplicate}' more than once.");
            }
        }

        if (manifest.Currencies is null || manifest.Currencies.Count == 0)
        {
            errors.Add("'currencies' must list at least one ISO 4217 code.");
        }
        else
        {
            foreach (var currency in manifest.Currencies)
            {
                if (!CurrencyPattern().IsMatch(currency ?? string.Empty))
                {
                    errors.Add(
                        $"'currencies' entry '{currency}' is not a 3-letter uppercase ISO 4217 code "
                        + "(for example 'INR', 'SGD', 'USD').");
                }
            }
        }

        if (manifest.AssetClasses is null || manifest.AssetClasses.Count == 0)
        {
            errors.Add("'assetClasses' must list at least one asset class.");
        }
    }

    private static void ValidateAuth(AuthSpec? auth, List<string> errors)
    {
        if (auth is null)
        {
            errors.Add("'auth' is required.");
            return;
        }

        if (auth.CredentialFields is null || auth.CredentialFields.Count == 0)
        {
            // Even a static-token broker needs one field: the token. A manifest with none
            // renders an empty link wizard the user cannot complete.
            errors.Add("'auth.credentialFields' must declare at least one field; the link wizard renders from it.");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in auth.CredentialFields)
            {
                if (string.IsNullOrWhiteSpace(field.Key))
                {
                    errors.Add("'auth.credentialFields' contains a field with no 'key'.");
                    continue;
                }

                if (!seen.Add(field.Key))
                {
                    errors.Add($"'auth.credentialFields' declares the key '{field.Key}' more than once.");
                }

                if (string.IsNullOrWhiteSpace(field.Label))
                {
                    errors.Add($"'auth.credentialFields[{field.Key}].label' is required.");
                }

                if (!string.IsNullOrWhiteSpace(field.Pattern) && !IsValidRegex(field.Pattern))
                {
                    errors.Add(
                        $"'auth.credentialFields[{field.Key}].pattern' is not a valid regular expression.");
                }
            }
        }

        if (auth.SessionLifetime is { } lifetime && lifetime <= TimeSpan.Zero)
        {
            errors.Add("'auth.sessionLifetime' must be positive when present.");
        }

        if (auth.KeepAliveInterval is { } keepAlive && keepAlive <= TimeSpan.Zero)
        {
            errors.Add("'auth.keepAliveInterval' must be positive when present.");
        }

        if (auth.ExpiresAtVenueMidnight)
        {
            // This is the check that protects SessionMonitor's most dangerous branch: without a
            // timezone it would have to guess, and guessing UTC for an IST broker means
            // believing a dead session is alive for five and a half hours.
            if (string.IsNullOrWhiteSpace(auth.VenueMidnightTimeZone))
            {
                errors.Add(
                    "'auth.venueMidnightTimeZone' is required when 'auth.expiresAtVenueMidnight' is true — "
                    + "session expiry cannot be computed without it.");
            }
            else if (!IsKnownTimeZone(auth.VenueMidnightTimeZone))
            {
                errors.Add(
                    $"'auth.venueMidnightTimeZone' value '{auth.VenueMidnightTimeZone}' is not a known "
                    + "time zone id on this host (expected an IANA id such as 'Asia/Kolkata').");
            }
        }

        if (auth.Model == AuthModel.GatewaySession && auth.RefreshSupported)
        {
            errors.Add(
                "'auth.refreshSupported' cannot be true for the GatewaySession model — the gateway daemon "
                + "owns the session and there is nothing for the platform to refresh.");
        }

        // A challenge-based model with no declared challenge leaves the UI with nothing to
        // prompt for, and the flow stalls at ChallengeRequired.
        var needsChallenge = auth.Model is AuthModel.PasswordOtp or AuthModel.PasswordTotp;
        if (needsChallenge && (auth.Challenges is null || auth.Challenges.Count == 0))
        {
            errors.Add($"'auth.challenges' must list at least one challenge for the {auth.Model} model.");
        }
    }

    private static void ValidateOrders(OrderSpec? orders, List<string> errors)
    {
        if (orders is null)
        {
            errors.Add("'orders' is required.");
            return;
        }

        if (orders.Types is null || orders.Types.Count == 0)
        {
            errors.Add("'orders.types' must list at least one order type.");
        }

        if (orders.TimeInForce is null || orders.TimeInForce.Count == 0)
        {
            errors.Add("'orders.timeInForce' must list at least one time-in-force.");
        }

        if (orders.PositionEffects is null || orders.PositionEffects.Count == 0)
        {
            errors.Add("'orders.positionEffects' must list at least one product type.");
        }
        else if (orders.PositionEffects.Contains(PositionEffect.None))
        {
            errors.Add("'orders.positionEffects' must not contain 'None' — it is the absence of a product type.");
        }

        if (orders.Varieties is null || orders.Varieties.Count == 0)
        {
            errors.Add("'orders.varieties' must list at least 'Regular'.");
        }

        // Self-consistency: the flags and the variety list are two views of the same fact, and
        // the UI reads one while the risk gate reads the other.
        if (orders.Bracket && orders.Varieties?.Contains(OrderVariety.Bracket) != true)
        {
            errors.Add("'orders.bracket' is true but 'orders.varieties' does not include 'Bracket'.");
        }

        if (orders.Cover && orders.Varieties?.Contains(OrderVariety.Cover) != true)
        {
            errors.Add("'orders.cover' is true but 'orders.varieties' does not include 'Cover'.");
        }

        if (orders.Basket is { Supported: true, MaxLegs: <= 0 })
        {
            errors.Add("'orders.basket.maxLegs' must be greater than zero when baskets are supported.");
        }

        if (orders.Basket is { Supported: false, MaxLegs: > 0 })
        {
            errors.Add("'orders.basket.maxLegs' is set but 'orders.basket.supported' is false.");
        }

        if (orders.ShortSellEquity && orders.PositionEffects?.Contains(PositionEffect.ShortSell) != true)
        {
            errors.Add(
                "'orders.shortSellEquity' is true but 'orders.positionEffects' does not include 'ShortSell'.");
        }

        if (orders.Modifiable is { Count: > 0 })
        {
            foreach (var field in orders.Modifiable)
            {
                if (!ModifiableFields.Contains(field))
                {
                    errors.Add(
                        $"'orders.modifiable' entry '{field}' is not a field of ModifyOrderRequest. "
                        + $"Expected one of: {string.Join(", ", ModifiableFields)}.");
                }
            }
        }
    }

    /// <summary>
    /// The only values <c>orders.modifiable</c> may contain: the settable properties of
    /// <see cref="ModifyOrderRequest"/>. Anything else would disable a UI control that does
    /// not exist, or fail to disable one that does.
    /// </summary>
    private static readonly IReadOnlySet<string> ModifiableFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(ModifyOrderRequest.Quantity),
            nameof(ModifyOrderRequest.LimitPrice),
            nameof(ModifyOrderRequest.TriggerPrice),
            nameof(ModifyOrderRequest.OrderType),
            nameof(ModifyOrderRequest.TimeInForce),
            nameof(ModifyOrderRequest.DisclosedQuantity),
        };

    private static void ValidateMarketData(ConnectorManifest manifest, List<string> errors)
    {
        var md = manifest.MarketData;
        if (md is null)
        {
            errors.Add("'marketData' is required.");
            return;
        }

        if (md.Streaming && (md.StreamModes is null || md.StreamModes.Count == 0))
        {
            errors.Add(
                "'marketData.streamModes' must list at least one mode when 'marketData.streaming' is true — "
                + "the fan-out layer has no way to subscribe otherwise.");
        }

        if (!md.Streaming && md.StreamModes is { Count: > 0 })
        {
            errors.Add("'marketData.streamModes' is set but 'marketData.streaming' is false.");
        }

        if (md.Historical && (md.HistoricalTimeFrames is null || md.HistoricalTimeFrames.Count == 0))
        {
            errors.Add(
                "'marketData.historicalTimeFrames' must list at least one time frame when "
                + "'marketData.historical' is true.");
        }

        if (!md.Historical && md.HistoricalTimeFrames is { Count: > 0 })
        {
            errors.Add("'marketData.historicalTimeFrames' is set but 'marketData.historical' is false.");
        }

        if (md.DepthLevels < 0)
        {
            errors.Add("'marketData.depthLevels' cannot be negative.");
        }

        if (md.MaxStreamSubscriptions <= 0)
        {
            errors.Add(
                "'marketData.maxStreamSubscriptions' must be positive. Omit it for 'no declared cap'; "
                + "zero would mean the connector can never subscribe to anything.");
        }

        if (md.HistoryDays is { } days && days <= 0)
        {
            errors.Add("'marketData.historyDays' must be positive when present.");
        }

        if (md.OptionChain && manifest.AssetClasses?.Contains(AssetClass.Option) != true)
        {
            errors.Add(
                "'marketData.optionChain' is true but 'assetClasses' does not include 'Option'.");
        }
    }

    private static void ValidateRateLimits(ConnectorManifest manifest, List<string> errors)
    {
        if (manifest.RateLimits is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in manifest.RateLimits)
        {
            if (string.IsNullOrWhiteSpace(spec.Scope))
            {
                errors.Add("'rateLimits' contains an entry with no 'scope'.");
                continue;
            }

            if (!RateLimitScopes.IsKnown(spec.Scope))
            {
                errors.Add(
                    $"'rateLimits' scope '{spec.Scope}' is unknown. Expected one of: "
                    + $"{string.Join(", ", RateLimitScopes.Known.Order(StringComparer.Ordinal))}. "
                    + "An unknown scope declares a limit nothing enforces.");
            }

            if (!seen.Add(spec.Scope))
            {
                errors.Add($"'rateLimits' declares the scope '{spec.Scope}' more than once.");
            }

            if (spec.PerSecond is null && spec.PerMinute is null && spec.PerDay is null)
            {
                errors.Add($"'rateLimits[{spec.Scope}]' sets no limit; remove the entry or give it one.");
            }

            if (spec.PerSecond is { } perSecond && perSecond <= 0)
            {
                errors.Add($"'rateLimits[{spec.Scope}].perSecond' must be positive.");
            }

            if (spec.PerMinute is { } perMinute && perMinute <= 0)
            {
                errors.Add($"'rateLimits[{spec.Scope}].perMinute' must be positive.");
            }

            if (spec.PerDay is { } perDay && perDay <= 0)
            {
                errors.Add($"'rateLimits[{spec.Scope}].perDay' must be positive.");
            }

            // A per-minute cap below the per-second cap is almost always a transcription error
            // from the vendor's documentation, and it silently throttles the connector to the
            // wrong rate rather than failing.
            if (spec.PerSecond is { } second && spec.PerMinute is { } minute && minute < second)
            {
                errors.Add(
                    $"'rateLimits[{spec.Scope}]' declares perMinute ({minute}) below perSecond ({second}).");
            }

            if (spec.PerMinute is { } perMin && spec.PerDay is { } perDayValue && perDayValue < perMin)
            {
                errors.Add(
                    $"'rateLimits[{spec.Scope}]' declares perDay ({perDayValue}) below perMinute ({perMin}).");
            }
        }
    }

    private static void ValidateHosting(ConnectorManifest manifest, List<string> errors)
    {
        if (manifest.Hosting == ConnectorHosting.Gateway)
        {
            if (manifest.Gateway is null)
            {
                errors.Add(
                    "'gateway' is required when 'hosting' is 'Gateway' — the supervisor has nothing to "
                    + "start or probe without it.");
            }
            else if (string.IsNullOrWhiteSpace(manifest.Gateway.Id))
            {
                errors.Add("'gateway.id' is required; it is how a running gateway instance is addressed.");
            }
            else if (manifest.Gateway.Port is { } port && port is < 1 or > 65535)
            {
                errors.Add($"'gateway.port' {port} is outside the valid range 1–65535.");
            }
        }
        else if (manifest.Gateway is not null)
        {
            errors.Add($"'gateway' is set but 'hosting' is '{manifest.Hosting}'.");
        }

        if (manifest.Auth?.Model == AuthModel.GatewaySession && manifest.Hosting != ConnectorHosting.Gateway)
        {
            errors.Add(
                "'auth.model' is 'GatewaySession' but 'hosting' is not 'Gateway'. A gateway-held session "
                + "requires a supervised gateway.");
        }
    }

    private static void ValidateSandbox(SandboxSpec? sandbox, List<string> errors)
    {
        if (sandbox is null)
        {
            return;
        }

        if (sandbox.Available && string.IsNullOrWhiteSpace(sandbox.BaseUrl))
        {
            errors.Add("'sandbox.baseUrl' is required when 'sandbox.available' is true.");
        }

        if (!string.IsNullOrWhiteSpace(sandbox.BaseUrl)
            && !Uri.TryCreate(sandbox.BaseUrl, UriKind.Absolute, out var uri))
        {
            errors.Add($"'sandbox.baseUrl' value '{sandbox.BaseUrl}' is not an absolute URL.");
        }
        else if (!string.IsNullOrWhiteSpace(sandbox.BaseUrl)
                 && Uri.TryCreate(sandbox.BaseUrl, UriKind.Absolute, out uri)
                 && uri.Scheme != Uri.UriSchemeHttps)
        {
            // Credentials travel to sandboxes too, and a plaintext sandbox trains people to
            // paste real credentials into a plaintext endpoint.
            errors.Add($"'sandbox.baseUrl' must use https; got '{uri.Scheme}'.");
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = Regex.Match(string.Empty, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            // It compiled; it is merely slow on an empty string, which is itself a red flag,
            // but the pattern is structurally valid.
            return true;
        }
    }

    private static bool IsKnownTimeZone(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static string Where(string? source) =>
        string.IsNullOrWhiteSpace(source) ? "The connector manifest" : $"'{source}'";

    private static Result<ConnectorManifest> Fail(string message) =>
        Result<ConnectorManifest>.Failure(new Error(ConnectorErrorCodes.InvalidRequest, message));
}

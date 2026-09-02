using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Sdk;

/// <summary>Knobs for <see cref="HttpConnectorClient"/>. All optional except the connector id.</summary>
public sealed class ConnectorHttpOptions
{
    /// <summary>Used in error messages and log scopes. Must match the manifest id.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>
    /// Produces the per-request headers — bearer token, API key, signature, session cookie.
    ///
    /// A delegate rather than a fixed dictionary because the values change underneath us: a
    /// session refresh replaces the token, and RSA/OAuth1a connectors must SIGN each request,
    /// which cannot be precomputed. It takes the request so signing schemes can hash the
    /// method and path.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>>?
        HeaderProvider
    { get; init; }

    public JsonSerializerOptions Json { get; init; } = ConnectorJson.Default;

    /// <summary>
    /// Property names to probe, in order, for the vendor's error code. The defaults cover the
    /// shapes seen across Indian, US and Asian broker APIs; override for anything exotic.
    /// </summary>
    public IReadOnlyList<string> VendorCodeFields { get; init; } =
        ["code", "errorCode", "error_code", "errorcode", "status_code", "statusCode", "error_type", "error"];

    /// <summary>Property names to probe, in order, for the vendor's human message.</summary>
    public IReadOnlyList<string> VendorMessageFields { get; init; } =
        ["message", "error_description", "errorMessage", "error_message", "msg", "description", "detail", "error"];

    /// <summary>
    /// Some brokers return HTTP 200 for failures and signal the failure in the body. When set,
    /// a 2xx response whose <c>status</c>-style field equals one of these values is treated as
    /// a failure. Empty means "trust the status code".
    /// </summary>
    public IReadOnlyList<string> BodyFailureStatusValues { get; init; } = [];

    /// <summary>Property name holding the status value checked against <see cref="BodyFailureStatusValues"/>.</summary>
    public string BodyStatusField { get; init; } = "status";

    /// <summary>How much of a failing body to keep for diagnostics. Bodies can be megabytes.</summary>
    public int MaxErrorBodyChars { get; init; } = 2_048;

    /// <summary>
    /// Cap on how long we honour a <c>Retry-After</c>. Brokers occasionally return an hour,
    /// and blocking a trading request for an hour is worse than failing it.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// A thin typed wrapper over <see cref="HttpClient"/> that returns <see cref="Result{T}"/> and
/// never throws for anything a broker can plausibly do.
///
/// It exists so that no connector author writes the status-to-canonical-code mapping again,
/// and so that mapping is identical everywhere: the risk engine and the retry policy both
/// switch on <see cref="ConnectorErrorCodes"/>, and one connector deciding 429 means
/// <c>Unknown</c> would silently disable retry shaping for that broker.
///
/// Transport mapping, applied only when the injected <see cref="IVendorErrorMapper"/> has no
/// opinion (the vendor knows better than the status code, and several brokers return 200 on
/// failures or 500 on a bad symbol):
///
/// <list type="bullet">
///   <item>401 → SessionExpired</item>
///   <item>403 → SessionExpired (in broker APIs a 403 is nearly always a dead or under-scoped token)</item>
///   <item>404 → InvalidRequest (a missing ORDER is OrderNotFound, but only the vendor mapper can know that)</item>
///   <item>408 / client timeout → Timeout</item>
///   <item>429 → RateLimited, carrying Retry-After</item>
///   <item>5xx, and connection failures → BrokerUnavailable</item>
///   <item>anything else → InvalidRequest for 4xx, Unknown otherwise</item>
/// </list>
///
/// Cancellation is NOT a broker failure: when the caller's token is cancelled the
/// <see cref="OperationCanceledException"/> propagates. Only a timeout with a live token is
/// turned into <see cref="ConnectorErrorCodes.Timeout"/>.
/// </summary>
public sealed class HttpConnectorClient
{
    /// <summary>Error context key carrying the HTTP status of the failing call.</summary>
    public const string HttpStatusKey = "httpStatus";

    /// <summary>Error context key carrying honoured Retry-After seconds, for the resilience decorator.</summary>
    public const string RetryAfterSecondsKey = "retryAfterSeconds";

    /// <summary>Error context key carrying the request path, for support triage.</summary>
    public const string PathKey = "path";

    /// <summary>Property names brokers habitually nest the real error object under.</summary>
    private static readonly string[] NestedErrorContainers = ["error", "data", "errors", "result"];

    private readonly HttpClient _http;
    private readonly IVendorErrorMapper _errorMapper;
    private readonly ConnectorHttpOptions _options;
    private readonly ILogger _logger;

    public HttpConnectorClient(
        HttpClient http,
        IVendorErrorMapper errorMapper,
        ConnectorHttpOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(errorMapper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _errorMapper = errorMapper;
        _options = options;
        _logger = logger;
    }

    /// <summary>The underlying client, for the rare call that needs raw access (file downloads).</summary>
    public HttpClient Inner => _http;

    public Task<Result<T>> GetAsync<T>(string path, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, path), ct);

    public Task<Result<T>> PostJsonAsync<T>(string path, object body, CancellationToken ct = default) =>
        SendAsync<T>(
            () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body, body.GetType(), options: _options.Json),
            },
            ct);

    public Task<Result<T>> PutJsonAsync<T>(string path, object body, CancellationToken ct = default) =>
        SendAsync<T>(
            () => new HttpRequestMessage(HttpMethod.Put, path)
            {
                Content = JsonContent.Create(body, body.GetType(), options: _options.Json),
            },
            ct);

    public Task<Result<T>> DeleteAsync<T>(string path, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Delete, path), ct);

    /// <summary>
    /// Form-encoded POST. Not a legacy nicety: OAuth2 token endpoints and most Indian broker
    /// login endpoints only accept <c>application/x-www-form-urlencoded</c>.
    /// </summary>
    public Task<Result<T>> PostFormAsync<T>(
        string path,
        IEnumerable<KeyValuePair<string, string>> form,
        CancellationToken ct = default)
    {
        var materialised = form as IList<KeyValuePair<string, string>> ?? [.. form];
        return SendAsync<T>(
            () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(materialised),
            },
            ct);
    }

    /// <summary>
    /// The general form. Takes a FACTORY rather than a message because an
    /// <see cref="HttpRequestMessage"/> cannot be sent twice, and the host's resilience
    /// decorator may call the enclosing operation again.
    /// </summary>
    public async Task<Result<T>> SendAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        var raw = await SendRawAsync(requestFactory, ct);
        if (raw.IsFailure)
        {
            return Result<T>.Failure(raw.Error);
        }

        var (body, path) = raw.Value;

        // A 204 or an empty body deserialising to a reference type is a contract mismatch the
        // caller should hear about, not a null slipped through as success.
        if (string.IsNullOrWhiteSpace(body))
        {
            return typeof(T) == typeof(string)
                ? Result<T>.Success((T)(object)string.Empty)
                : Result<T>.Failure(BuildError(
                    ConnectorErrorCodes.Unknown,
                    "The broker returned an empty body where data was expected.",
                    null,
                    null,
                    null,
                    path,
                    null));
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, _options.Json);
            return value is null
                ? Result<T>.Failure(BuildError(
                    ConnectorErrorCodes.Unknown,
                    "The broker returned a null payload where data was expected.",
                    null,
                    null,
                    null,
                    path,
                    null))
                : Result<T>.Success(value);
        }
        catch (JsonException ex)
        {
            // A shape change at the vendor is an operational event, not a crash. Log the body
            // so support can diff it against what we expected.
            _logger.LogWarning(
                ex,
                "{ConnectorId}: could not deserialise the response from {Path} into {Type}.",
                _options.ConnectorId,
                path,
                typeof(T).Name);

            return Result<T>.Failure(BuildError(
                ConnectorErrorCodes.Unknown,
                "The broker's response could not be understood.",
                null,
                Truncate(body),
                null,
                path,
                null));
        }
    }

    /// <summary>For endpoints that return no useful body (a cancel, a logout).</summary>
    public async Task<Result> SendNoContentAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct = default)
    {
        var raw = await SendRawAsync(requestFactory, ct);
        return raw.IsSuccess ? Result.Success() : Result.Failure(raw.Error);
    }

    /// <summary>
    /// Sends, applies headers, and classifies the outcome — returning the body text and the
    /// path on success. Everything above funnels through here, so the status mapping exists
    /// exactly once.
    /// </summary>
    public async Task<Result<(string Body, string Path)>> SendRawAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        using var request = requestFactory();
        var path = request.RequestUri?.ToString() ?? "(no uri)";

        if (_options.HeaderProvider is { } provider)
        {
            IReadOnlyDictionary<string, string> headers;
            try
            {
                headers = await provider(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Header construction is where signing and token lookup happen; failing there
                // means we have no usable session, not that the broker is down.
                _logger.LogError(ex, "{ConnectorId}: failed to build request headers.", _options.ConnectorId);
                return Failure(
                    ConnectorErrorCodes.SessionExpired,
                    "Could not build authenticated request headers.",
                    null,
                    null,
                    null,
                    path);
            }

            foreach (var (name, value) in headers)
            {
                // TryAddWithoutValidation: broker APIs use non-standard header names and
                // values (raw checksums, unencoded JSON) that strict validation rejects.
                if (!request.Headers.TryAddWithoutValidation(name, value)
                    && request.Content is not null)
                {
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as TaskCanceledException with an untriggered
            // caller token. This distinction is the whole reason for the `when` clause: a
            // caller-cancelled request must not be reported as a broker timeout, and a broker
            // timeout must not be swallowed as a cancellation.
            _logger.LogWarning("{ConnectorId}: request to {Path} timed out.", _options.ConnectorId, path);
            return Failure(ConnectorErrorCodes.Timeout, "The broker did not respond in time.", null, null, null, path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            // DNS failure, refused connection, TLS failure. All "the broker is not reachable".
            _logger.LogWarning(ex, "{ConnectorId}: transport failure calling {Path}.", _options.ConnectorId, path);
            return Failure(
                ConnectorErrorCodes.BrokerUnavailable,
                "Could not reach the broker.",
                null,
                ex.Message,
                null,
                path);
        }

        using (response)
        {
            var body = await ReadBodyAsync(response, ct);
            var (vendorCode, vendorMessage) = ExtractVendorError(body);

            if (response.IsSuccessStatusCode && !IsBodyLevelFailure(body))
            {
                return Result<(string, string)>.Success((body, path));
            }

            var status = (int)response.StatusCode;
            var context = new VendorErrorContext(
                status,
                vendorCode,
                vendorMessage,
                path,
                Truncate(body));

            var canonical = SafeMap(context) ?? MapStatus(response.StatusCode);
            var retryAfter = ReadRetryAfter(response);

            _logger.LogWarning(
                "{ConnectorId}: {Path} failed with HTTP {Status} → {Canonical} (vendor {VendorCode}: {VendorMessage}).",
                _options.ConnectorId,
                path,
                status,
                canonical,
                vendorCode ?? "-",
                vendorMessage ?? "-");

            return Failure(
                canonical,
                _errorMapper.DescribeCanonicalCode(canonical, context),
                vendorCode,
                vendorMessage ?? Truncate(body),
                status,
                path,
                retryAfter);
        }
    }

    /// <summary>
    /// Exposed so a connector can normalise an error it produced itself (a gateway socket
    /// error, a parse failure) through exactly the same mapping the HTTP path uses.
    /// </summary>
    public Error Normalise(VendorErrorContext context)
    {
        var canonical = SafeMap(context) ?? (context.HttpStatus is { } s
            ? MapStatus((HttpStatusCode)s)
            : ConnectorErrorCodes.Unknown);

        return BuildError(
            canonical,
            _errorMapper.DescribeCanonicalCode(canonical, context),
            context.VendorCode,
            context.VendorMessage,
            context.HttpStatus,
            context.Path,
            null);
    }

    private string? SafeMap(VendorErrorContext context)
    {
        try
        {
            return _errorMapper.MapToCanonicalCode(context);
        }
        catch (Exception ex)
        {
            // A throwing mapper must not turn a recoverable broker error into an unhandled
            // exception on the trading path. Fall back to the status mapping and shout.
            _logger.LogError(ex, "{ConnectorId}: the vendor error mapper threw.", _options.ConnectorId);
            return null;
        }
    }

    private static string MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ConnectorErrorCodes.SessionExpired,
        HttpStatusCode.Forbidden => ConnectorErrorCodes.SessionExpired,
        HttpStatusCode.NotFound => ConnectorErrorCodes.InvalidRequest,
        HttpStatusCode.RequestTimeout => ConnectorErrorCodes.Timeout,
        HttpStatusCode.TooManyRequests => ConnectorErrorCodes.RateLimited,
        HttpStatusCode.BadGateway => ConnectorErrorCodes.BrokerUnavailable,
        HttpStatusCode.ServiceUnavailable => ConnectorErrorCodes.BrokerUnavailable,
        HttpStatusCode.GatewayTimeout => ConnectorErrorCodes.Timeout,
        _ when (int)status >= 500 => ConnectorErrorCodes.BrokerUnavailable,
        _ when (int)status >= 400 => ConnectorErrorCodes.InvalidRequest,
        _ => ConnectorErrorCodes.Unknown,
    };

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        TimeSpan? delay = header.Delta;

        if (delay is null && header.Date is { } date)
        {
            // Absolute form. Compute against the response's own Date header when present so a
            // clock skew between us and the broker does not turn into a negative wait.
            var reference = response.Headers.Date ?? date;
            var computed = date - reference;
            delay = computed > TimeSpan.Zero ? computed : TimeSpan.Zero;
        }

        if (delay is null)
        {
            return null;
        }

        return delay > _options.MaxRetryAfter ? _options.MaxRetryAfter : delay;
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A truncated body on an otherwise-classified response should not mask the status.
            _logger.LogDebug(ex, "{ConnectorId}: could not read the response body.", _options.ConnectorId);
            return string.Empty;
        }
    }

    private bool IsBodyLevelFailure(string body)
    {
        if (_options.BodyFailureStatusValues.Count == 0 || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(_options.BodyStatusField, out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = statusElement.GetString();
            return value is not null
                   && _options.BodyFailureStatusValues.Contains(value, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pulls the vendor's code and message out of an arbitrary JSON body, probing the
    /// configured field names at the root and one level down (brokers habitually nest the real
    /// error under <c>data</c>, <c>error</c> or <c>errors[0]</c>). Never throws: a body that is
    /// not JSON at all just yields nulls and the status mapping takes over.
    /// </summary>
    private (string? Code, string? Message) ExtractVendorError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var code = Probe(root, _options.VendorCodeFields);
            var message = Probe(root, _options.VendorMessageFields);

            if (code is not null || message is not null)
            {
                return (code, message);
            }

            foreach (var nestedName in NestedErrorContainers)
            {
                if (!root.TryGetProperty(nestedName, out var nested))
                {
                    continue;
                }

                var target = nested.ValueKind == JsonValueKind.Array && nested.GetArrayLength() > 0
                    ? nested[0]
                    : nested;

                if (target.ValueKind == JsonValueKind.String)
                {
                    return (null, target.GetString());
                }

                if (target.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                code = Probe(target, _options.VendorCodeFields);
                message = Probe(target, _options.VendorMessageFields);
                if (code is not null || message is not null)
                {
                    return (code, message);
                }
            }

            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Probe(JsonElement element, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }

                    break;

                case JsonValueKind.Number:
                    return value.GetRawText();

                default:
                    continue;
            }
        }

        return null;
    }

    private string Truncate(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        return body.Length <= _options.MaxErrorBodyChars
            ? body
            : string.Concat(body.AsSpan(0, _options.MaxErrorBodyChars), "…[truncated]");
    }

    private Result<(string Body, string Path)> Failure(
        string canonicalCode,
        string message,
        string? vendorCode,
        string? vendorMessage,
        int? httpStatus,
        string path,
        TimeSpan? retryAfter = null) =>
        Result<(string, string)>.Failure(
            BuildError(canonicalCode, message, vendorCode, vendorMessage, httpStatus, path, retryAfter));

    private static Error BuildError(
        string canonicalCode,
        string message,
        string? vendorCode,
        string? vendorMessage,
        int? httpStatus,
        string? path,
        TimeSpan? retryAfter)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal);

        if (httpStatus is { } status)
        {
            context[HttpStatusKey] = status.ToString(CultureInfo.InvariantCulture);
        }

        if (path is not null)
        {
            context[PathKey] = path;
        }

        if (retryAfter is { } delay)
        {
            // The resilience decorator reads this instead of guessing a backoff. Honouring the
            // broker's own number is the difference between recovering and getting banned.
            context[RetryAfterSecondsKey] =
                delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return new Error(canonicalCode, message, vendorCode, vendorMessage, context);
    }
}

/// <summary>
/// Small helpers for building request URIs without the classic double-slash and
/// unescaped-query bugs.
/// </summary>
public static class HttpConnectorPath
{
    /// <summary>Appends a query string, escaping values. Null values are skipped, not sent as "null".</summary>
    public static string WithQuery(string path, params (string Key, string? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(parameters);

        var builder = new StringBuilder(path);
        var first = !path.Contains('?', StringComparison.Ordinal);

        foreach (var (key, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    /// <summary>Joins path segments with exactly one separator between them.</summary>
    public static string Join(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return string.Join('/', segments.Select(s => s.Trim('/')).Where(s => s.Length > 0));
    }
}

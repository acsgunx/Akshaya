using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// The one place in this connector that talks to <see cref="HttpConnectorClient"/>.
///
/// Everything else — auth, orders, portfolio, market data, reference — goes through here. That is
/// deliberate: the SDK's transport type is the seam most likely to move underneath us, and
/// concentrating it in one small class means a change to its shape is a one-file edit rather than
/// a six-file archaeology exercise.
///
/// It also owns the three things every Kite call needs and no facet should have to remember:
///
/// * the two required headers, <c>X-Kite-Version: 3</c> and the <c>Authorization</c> header,
///   which is Kite's own <c>token</c> scheme and not a bearer token;
/// * that POST and PUT bodies are FORM-ENCODED while the margin route alone takes JSON; and
/// * unwrapping the <c>{ "status": …, "data": … }</c> envelope, so no facet has to remember that
///   a failure is <c>status: "error"</c> with the reason under <c>error_type</c>.
/// </summary>
internal sealed class ZerodhaApi : IAsyncDisposable
{
    private readonly HttpConnectorClient _client;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ZerodhaOptions _options;
    private readonly ZerodhaErrorMapper _errors;
    private readonly ILogger _logger;
    private bool _disposed;

    private ZerodhaApi(
        HttpConnectorClient client,
        HttpClient http,
        bool ownsHttpClient,
        ZerodhaOptions options,
        ZerodhaErrorMapper errors,
        ILogger logger)
    {
        _client = client;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _options = options;
        _errors = errors;
        _logger = logger;
    }

    /// <summary>The api_key this client is authenticating with, when it has a session.</summary>
    public string? ApiKey { get; private init; }

    /// <summary>
    /// Builds a client. <paramref name="session"/> is null during the authentication handshake,
    /// where there is no token to send yet.
    /// </summary>
    public static ZerodhaApi Create(
        ZerodhaOptions options,
        ZerodhaErrorMapper errors,
        BrokerSession? session,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        var ownsClient = httpClient is null;

        // AutomaticDecompression matters here and only here: the instrument master is served
        // gzipped. It is requested on the client we build, and the reference facet ALSO sniffs
        // the gzip magic bytes, so a caller-supplied client with decompression turned off still
        // works. Belt and braces, because the failure mode otherwise is a CSV parser fed binary.
        var http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                     | System.Net.DecompressionMethods.Deflate,
        });

        // BaseAddress and Timeout CANNOT BE SET ONCE A CLIENT HAS SENT A REQUEST — HttpClient
        // throws InvalidOperationException, not a no-op. A supplied client is very likely to be a
        // pooled one (that is the entire point of ConnectorActivationContext.HttpClientFactory),
        // so a second facet configuring it would take down the call rather than the connector.
        try
        {
            http.BaseAddress ??= options.BaseUrl;

            // One HttpClient serves both ordinary calls and the instrument-master download, so
            // its own timeout is the longer of the two and the shorter one is enforced per call
            // with a linked token. Setting it to RequestTimeout would abort every nightly
            // instrument ingest at fifteen seconds.
            http.Timeout = options.InstrumentMasterTimeout > options.RequestTimeout
                ? options.InstrumentMasterTimeout
                : options.RequestTimeout;
        }
        catch (InvalidOperationException)
        {
            // Already in use. Its existing base address and timeout stand.
        }

        var apiKey = session?.Extras.GetValueOrDefault(ZerodhaSessionKeys.ApiKey);

        ApplyStandardHeaders(http, options, apiKey, session?.AccessToken);

        var log = logger ?? NullLogger.Instance;

        return new ZerodhaApi(
            new HttpConnectorClient(
                http,
                errors,
                new ConnectorHttpOptions
                {
                    ConnectorId = ZerodhaAuth.ConnectorId,
                    Json = ZerodhaJson.Options,

                    // Kite pairs every failure with a real 4xx/5xx, so this is belt and braces
                    // rather than the load-bearing check it is for brokers that answer 200 —
                    // but declaring it lets the SDK pull error_type and message out of the body
                    // on the way past.
                    BodyStatusField = ZerodhaJson.StatusField,
                    BodyFailureStatusValues = [ZerodhaJson.StatusError],
                    VendorCodeFields = ["error_type"],
                    VendorMessageFields = ["message"],
                },
                log),
            http,
            ownsClient,
            options,
            errors,
            log)
        {
            ApiKey = apiKey,
        };
    }

    /// <summary>
    /// The headers Kite requires on every call, including the unauthenticated token exchange.
    ///
    /// <c>Authorization: token {api_key}:{access_token}</c> is Kite's own scheme — not a bearer
    /// token and not basic auth. <see cref="AuthenticationHeaderValue"/> renders it correctly
    /// because <c>token</c> genuinely is the scheme here, which is the one place this differs
    /// from brokers that expect a bare, scheme-less value.
    /// </summary>
    private static void ApplyStandardHeaders(
        HttpClient http,
        ZerodhaOptions options,
        string? apiKey,
        string? accessToken)
    {
        http.DefaultRequestHeaders.Remove("X-Kite-Version");
        http.DefaultRequestHeaders.Add("X-Kite-Version", options.ApiVersion);

        http.DefaultRequestHeaders.Authorization = null;
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(accessToken))
        {
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("token", $"{apiKey}:{accessToken}");
        }

        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Re-stamps the auth headers after a login, so the same client instance can be used for the
    /// call that obtains the token and the calls that use it.
    /// </summary>
    public void UseSession(string apiKey, string accessToken) =>
        ApplyStandardHeaders(_http, _options, apiKey, accessToken);

    // --- verbs --------------------------------------------------------------------------

    public Task<Result<TData>> GetAsync<TData>(
        string path,
        ZerodhaQuery? query = null,
        CancellationToken ct = default) =>
        SendAsync(_client.GetAsync<KiteEnvelope<TData>>, Combine(path, query), path, ct);

    /// <summary>
    /// Form-encoded POST — the default shape for Kite's write routes.
    ///
    /// Kite's response-structure page is explicit: "POST and PUT parameters as form-encoded
    /// (application/x-www-form-urlencoded) parameters". Order placement, modification and
    /// position conversion are all forms. The margin calculator is the documented exception and
    /// uses <see cref="PostJsonAsync{TData}"/>.
    /// </summary>
    public Task<Result<TData>> PostFormAsync<TData>(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> form,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.PostFormAsync<KiteEnvelope<TData>>(uri, form, token), path, path, ct);

    /// <summary>Form-encoded PUT: order modification and position conversion.</summary>
    public Task<Result<TData>> PutFormAsync<TData>(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> form,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.PutFormAsync<KiteEnvelope<TData>>(uri, form, token), path, path, ct);

    /// <summary>JSON POST. The margin and charges calculator is the only route that wants one.</summary>
    public Task<Result<TData>> PostJsonAsync<TData>(
        string path,
        object body,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.PostJsonAsync<KiteEnvelope<TData>>(uri, body, token), path, path, ct);

    public Task<Result<TData>> DeleteAsync<TData>(
        string path,
        ZerodhaQuery? query = null,
        CancellationToken ct = default) =>
        SendAsync(_client.DeleteAsync<KiteEnvelope<TData>>, Combine(path, query), path, ct);

    /// <summary>
    /// Raw response body, for the instrument master. It is CSV, not JSON, and far too large to
    /// buffer — the caller reads it a row at a time.
    /// </summary>
    public async Task<Result<Stream>> GetRawStreamAsync(
        string path,
        CancellationToken ct = default)
    {
        try
        {
            // Straight to the underlying client: the SDK wrapper is JSON-typed, and the master is
            // several megabytes of CSV that must not be buffered into a string first.
            var response = await _client.Inner
                .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                response.Dispose();
                return Result<Stream>.Failure(_errors.MapHttp(status, body, path));
            }

            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A caller-requested cancellation is not a broker failure and must propagate as
            // itself; anything else becomes a canonical Result failure.
            return Result<Stream>.Failure(ZerodhaErrorMapper.MapException(ex));
        }
    }

    /// <summary>
    /// Sends, enforces the per-call deadline, and unwraps the envelope. Everything above funnels
    /// through here, so the envelope handling exists exactly once.
    /// </summary>
    private async Task<Result<TData>> SendAsync<TData>(
        Func<string, CancellationToken, Task<Result<KiteEnvelope<TData>>>> send,
        string requestUri,
        string route,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);

        Result<KiteEnvelope<TData>> response;
        try
        {
            response = await send(requestUri, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A caller-requested cancellation propagates as itself; the per-call deadline cancels
            // its own linked token, which does not trip this filter and becomes a Timeout.
            return Result<TData>.Failure(ZerodhaErrorMapper.MapException(ex));
        }

        if (response.IsFailure)
        {
            return Result<TData>.Failure(response.Error);
        }

        var envelope = response.Value;
        if (envelope is null)
        {
            return Result<TData>.Failure(ZerodhaErrors.MissingField(route, "body"));
        }

        if (!envelope.IsSuccess)
        {
            _logger.LogWarning(
                "{ConnectorId}: {Route} answered status=error ({ErrorType}): {Message}",
                ZerodhaAuth.ConnectorId,
                route,
                envelope.ErrorType ?? "(none)",
                envelope.Message ?? "(no message)");

            return Result<TData>.Failure(_errors.MapEnvelope(envelope.ErrorType, envelope.Message, route));
        }

        if (envelope.Data is null)
        {
            return Result<TData>.Failure(ZerodhaErrors.MissingField(route, "data"));
        }

        return envelope.Data;
    }

    private static string Combine(string path, ZerodhaQuery? query) =>
        query is null || query.IsEmpty ? path : path + query.ToQueryString();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A query-string builder that escapes correctly and supports REPEATED keys, which Kite's quote
/// routes rely on: instruments are passed as <c>?i=NSE:INFY&amp;i=NSE:SBIN</c>, one <c>i</c> per
/// instrument, up to five hundred of them.
/// </summary>
internal sealed class ZerodhaQuery
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public bool IsEmpty => _pairs.Count == 0;

    public ZerodhaQuery Add(string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        return this;
    }

    public ZerodhaQuery Add(string key, int value) =>
        Add(key, value.ToString(CultureInfo.InvariantCulture));

    public ZerodhaQuery AddAll(string key, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            Add(key, value);
        }

        return this;
    }

    public string ToQueryString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("?");
        for (var i = 0; i < _pairs.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(_pairs[i].Key))
                   .Append('=')
                   .Append(Uri.EscapeDataString(_pairs[i].Value));
        }

        return builder.ToString();
    }
}

/// <summary>
/// Keys under which Kite-specific material is stashed in <see cref="BrokerSession.Extras"/>.
///
/// The shared contract deliberately has no field for an api_key or a public token — putting one
/// there would mean every future broker inherits Kite's vocabulary. Extras is the escape hatch,
/// and these constants keep both ends of it spelled the same way.
/// </summary>
internal static class ZerodhaSessionKeys
{
    /// <summary>The api_key. Required on every request as half of the Authorization header.</summary>
    public const string ApiKey = "api_key";

    /// <summary>Kite's public session token. Not used for signing; kept for support triage.</summary>
    public const string PublicToken = "public_token";

    public const string UserName = "user_name";

    /// <summary>Exchanges this login is entitled to, comma separated, as Kite reported them.</summary>
    public const string Exchanges = "exchanges";

    public const string Products = "products";

    public const string OrderTypes = "order_types";
}

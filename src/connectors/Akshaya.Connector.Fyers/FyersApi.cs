using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// The one place in this connector that talks to <see cref="HttpConnectorClient"/>.
///
/// Everything else — auth, orders, portfolio, market data, reference — goes through here. That
/// is deliberate: the SDK's transport type is the seam most likely to move underneath us, and
/// concentrating it in one small class means a change to its shape is a one-file edit rather
/// than a six-file archaeology exercise.
///
/// It also owns the three things every FYERS call needs and no facet should have to remember:
///
/// * the <c>Authorization</c> header, which is NOT a bearer token (see
///   <see cref="ApplyStandardHeaders"/>);
/// * checking <c>s: "error"</c>, because FYERS reports business failures — a rejected order, an
///   expired token — as HTTP 200 with a negative <c>code</c>, so a facet that only looked at the
///   status code would report every rejection as a success; and
/// * keeping the authenticated client away from <c>public.fyers.in</c>, which serves the symbol
///   master and has no business seeing an access token.
/// </summary>
internal sealed class FyersApi : IAsyncDisposable
{
    private readonly HttpConnectorClient _client;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly FyersOptions _options;
    private readonly FyersErrorMapper _errors;
    private readonly ILogger _logger;

    /// <summary>
    /// A second client for the public symbol-master files.
    ///
    /// Separate from the authenticated one ON PURPOSE, not for tidiness. The master lives on a
    /// different host, needs no credentials, and must keep working before a user has linked an
    /// account at all. Reusing the authenticated client would send this user's access token to a
    /// static file server on every nightly ingest — a credential disclosed to a host that has no
    /// use for it, written into that host's logs, for no benefit whatsoever.
    /// </summary>
    private HttpClient? _publicHttp;

    private bool _disposed;

    private FyersApi(
        HttpConnectorClient client,
        HttpClient http,
        bool ownsHttpClient,
        FyersOptions options,
        FyersErrorMapper errors,
        ILogger logger)
    {
        _client = client;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _options = options;
        _errors = errors;
        _logger = logger;
    }

    /// <summary>The app id this client is authenticating with, when it has a session.</summary>
    public string? AppId { get; private init; }

    /// <summary>
    /// Builds a client. <paramref name="session"/> is null during the authentication handshake,
    /// where there is no token to send yet.
    /// </summary>
    public static FyersApi Create(
        FyersOptions options,
        FyersErrorMapper errors,
        BrokerSession? session,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        var ownsClient = httpClient is null;
        var http = httpClient ?? new HttpClient();

        // BaseAddress and Timeout CANNOT BE SET ONCE A CLIENT HAS SENT A REQUEST — HttpClient
        // throws InvalidOperationException, not a no-op. A supplied client is very likely to be a
        // pooled one (that is the entire point of ConnectorActivationContext.HttpClientFactory),
        // so a second facet configuring it would take down the call rather than the connector.
        //
        // Respecting an existing configuration is also the correct semantic: a caller who hands
        // us a configured client did not ask for its timeout to be rewritten. The per-call
        // deadline does not depend on this anyway — SendAsync enforces RequestTimeout with a
        // linked cancellation token regardless of what the client's own timeout says.
        try
        {
            http.BaseAddress ??= options.BaseUrl;
            http.Timeout = options.RequestTimeout;
        }
        catch (InvalidOperationException)
        {
            // Already in use. Its existing base address and timeout stand.
        }

        var appId = session?.Extras.GetValueOrDefault(FyersSessionKeys.AppId);

        ApplyStandardHeaders(http, appId, session?.AccessToken);

        var log = logger ?? NullLogger.Instance;

        return new FyersApi(
            new HttpConnectorClient(
                http,
                errors,
                new ConnectorHttpOptions
                {
                    ConnectorId = FyersAuth.ConnectorId,
                    Json = FyersJson.Options,

                    // FYERS reports business failures as HTTP 200 with s:"error". Declaring the
                    // field here lets the SDK client classify those as failures before a facet
                    // ever sees them, and pull the vendor code and message out of the same body.
                    BodyStatusField = FyersJson.StatusField,
                    BodyFailureStatusValues = [FyersJson.StatusError],
                },
                log),
            http,
            ownsClient,
            options,
            errors,
            log)
        {
            AppId = appId,
        };
    }

    /// <summary>
    /// The header FYERS requires on every authenticated call.
    ///
    /// <c>Authorization: {app_id}:{access_token}</c> is not a bearer token, not basic auth, and
    /// carries no scheme at all — which is why it is added without validation rather than through
    /// <see cref="AuthenticationHeaderValue"/>. That type always emits "scheme parameter", so
    /// setting it produces <c>Authorization: Bearer XC…-100:eyJ…</c> and FYERS answers 401 on
    /// REST and a bare handshake 403 on the socket, neither of which says why.
    /// </summary>
    private static void ApplyStandardHeaders(HttpClient http, string? appId, string? accessToken)
    {
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Remove("Authorization");

        if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(accessToken))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"{appId}:{accessToken}");
        }

        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Re-stamps the auth header after a login, so the same client instance can be used for the
    /// call that obtains the token and the calls that use it.
    /// </summary>
    public void UseSession(string appId, string accessToken) =>
        ApplyStandardHeaders(_http, appId, accessToken);

    // --- verbs ---------------------------------------------------------------------------

    public Task<Result<TResponse>> GetAsync<TResponse>(
        string path,
        FyersQuery? query = null,
        CancellationToken ct = default)
        where TResponse : FyersResponse =>
        SendAsync(_client.GetAsync<TResponse>, Combine(path, query), path, ct);

    public Task<Result<TResponse>> PostJsonAsync<TResponse>(
        string path,
        object body,
        CancellationToken ct = default)
        where TResponse : FyersResponse =>
        SendAsync((uri, token) => _client.PostJsonAsync<TResponse>(uri, body, token), path, path, ct);

    /// <summary>
    /// PATCH, which is how FYERS modifies an order and how it attaches a stop to a position.
    /// The SDK client has no PATCH shorthand, so this goes through the general form.
    /// </summary>
    public Task<Result<TResponse>> PatchJsonAsync<TResponse>(
        string path,
        object body,
        CancellationToken ct = default)
        where TResponse : FyersResponse =>
        SendAsync<TResponse>(
            (uri, token) => _client.SendAsync<TResponse>(
                () => new HttpRequestMessage(HttpMethod.Patch, uri)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(body, body.GetType(), options: FyersJson.Options),
                },
                token),
            path,
            path,
            ct);

    /// <summary>
    /// DELETE without a body. FYERS documents cancellation both ways; the path form is used
    /// because a DELETE carrying a body is unevenly supported by proxies and by HttpClient's
    /// own handlers.
    /// </summary>
    public Task<Result<TResponse>> DeleteAsync<TResponse>(
        string path,
        CancellationToken ct = default)
        where TResponse : FyersResponse =>
        SendAsync(_client.DeleteAsync<TResponse>, path, path, ct);

    /// <summary>
    /// DELETE with a JSON body. Needed for the exit-position route, whose entire vocabulary
    /// (<c>exit_all</c>, an id list, a segment/side/product filter) lives in the body.
    /// </summary>
    public Task<Result<TResponse>> DeleteJsonAsync<TResponse>(
        string path,
        object body,
        CancellationToken ct = default)
        where TResponse : FyersResponse =>
        SendAsync<TResponse>(
            (uri, token) => _client.SendAsync<TResponse>(
                () => new HttpRequestMessage(HttpMethod.Delete, uri)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(body, body.GetType(), options: FyersJson.Options),
                },
                token),
            path,
            path,
            ct);

    /// <summary>
    /// A symbol-master file, streamed. It is CSV, not JSON, and far too large to buffer — the
    /// caller reads it a row at a time. Fetched unauthenticated; see <see cref="_publicHttp"/>.
    /// </summary>
    public async Task<Result<Stream>> GetSymbolMasterAsync(string fileName, CancellationToken ct)
    {
        var uri = new Uri(_options.SymbolMasterUrl, fileName);

        try
        {
            var http = _publicHttp ??= new HttpClient { Timeout = _options.SymbolMasterTimeout };

            var response = await http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.Dispose();
                return Result<Stream>.Failure(
                    _errors.MapHttp((int)response.StatusCode, body, uri.ToString()));
            }

            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A caller-requested cancellation is not a broker failure and must propagate as
            // itself; anything else becomes a canonical Result failure.
            return Result<Stream>.Failure(FyersErrorMapper.MapException(ex));
        }
    }

    /// <summary>
    /// Sends, enforces the per-call deadline, and inspects the response envelope.
    ///
    /// The envelope check is why everything funnels through here. The SDK client already treats
    /// <c>s: "error"</c> as a failure, but only when the body parses as the shape it expects; a
    /// response that deserialises cleanly and still says "error" — which happens on routes that
    /// answer 200 for a rejected order — would otherwise reach a facet looking like success.
    /// </summary>
    private async Task<Result<TResponse>> SendAsync<TResponse>(
        Func<string, CancellationToken, Task<Result<TResponse>>> send,
        string requestUri,
        string route,
        CancellationToken ct)
        where TResponse : FyersResponse
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);

        Result<TResponse> response;
        try
        {
            response = await send(requestUri, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A caller-requested cancellation is not a broker failure and must propagate as
            // itself; the per-call deadline above cancels its own linked token, which does not
            // trip this filter and so becomes a canonical Timeout.
            return Result<TResponse>.Failure(FyersErrorMapper.MapException(ex));
        }

        if (response.IsFailure)
        {
            return response;
        }

        var body = response.Value;
        if (body is null)
        {
            return Result<TResponse>.Failure(FyersErrors.MissingField(route, "body"));
        }

        if (!body.IsOk)
        {
            _logger.LogWarning(
                "{ConnectorId}: {Route} answered s=error with code {Code}: {Message}",
                FyersAuth.ConnectorId,
                route,
                body.Code,
                body.Message ?? "(no message)");

            return Result<TResponse>.Failure(_errors.MapEnvelope(body.Code, body.Message, route));
        }

        return response;
    }

    private static string Combine(string path, FyersQuery? query) =>
        query is null || query.IsEmpty ? path : path + query.ToQueryString();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _publicHttp?.Dispose();

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A query-string builder that escapes correctly.
///
/// Escaping matters more than usual here: FYERS symbols legitimately contain an ampersand
/// (<c>NSE:M&amp;M-EQ</c>), and its own documentation calls out that an unescaped one truncates
/// the request — which does not fail, it silently asks for a different symbol.
/// </summary>
internal sealed class FyersQuery
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public bool IsEmpty => _pairs.Count == 0;

    public FyersQuery Add(string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        return this;
    }

    public FyersQuery Add(string key, int value) =>
        Add(key, value.ToString(CultureInfo.InvariantCulture));

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
/// Keys under which FYERS-specific material is stashed in <see cref="BrokerSession.Extras"/>.
///
/// The shared contract deliberately has no field for an app id — putting one there would mean
/// every future broker inherits the FYERS vocabulary. Extras is the escape hatch, and these
/// constants keep both ends of it spelled the same way.
/// </summary>
internal static class FyersSessionKeys
{
    /// <summary>The app id. Required on every request, as half of the Authorization header.</summary>
    public const string AppId = "app_id";

    /// <summary>The FYERS client id, as the profile route reported it.</summary>
    public const string ClientId = "client_id";

    /// <summary>The display name shown on the linked-account card.</summary>
    public const string UserName = "user_name";

    /// <summary>
    /// Whether the account is enabled for the margin-trading product, as the profile route
    /// reported it at login. Lets the order ticket refuse an MTF order locally rather than
    /// learning about the entitlement from a rejection.
    /// </summary>
    public const string MtfEnabled = "mtf_enabled";
}

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akshaya.Connector.MStock;

/// <summary>
/// The one place in this connector that talks to <see cref="HttpConnectorClient"/>.
///
/// Everything else — auth, orders, portfolio, market data, reference — goes through here. That
/// is deliberate: the SDK's transport type is the seam most likely to move underneath us, and
/// concentrating it in one small class means a change to its shape is a one-file edit rather
/// than a six-file archaeology exercise.
///
/// It also owns the two things every mStock call needs and no facet should have to remember:
///
/// * the four required headers (<c>X-Mirae-Version</c>, <c>Authorization</c>,
///   <c>X-PrivateKey</c>, and the content type), and
/// * unwrapping the <c>{ "status": ..., "data": ... }</c> envelope. mStock reports business
///   failures — a rejected order, an expired token — as HTTP 200 with
///   <c>status: "error"</c>. A facet that only checked the status code would report every
///   rejection as a success, so the envelope check happens here, once, for all of them.
/// </summary>
/// <remarks>
/// Assumed <see cref="HttpConnectorClient"/> surface (Akshaya.Connectors.Sdk):
/// <code>
/// HttpConnectorClient(HttpClient http, IVendorErrorMapper errors, JsonSerializerOptions? json = null)
/// Task&lt;Result&lt;T&gt;&gt; GetAsync&lt;T&gt;(string requestUri, CancellationToken ct = default)
/// Task&lt;Result&lt;T&gt;&gt; PostJsonAsync&lt;T&gt;(string requestUri, object body, CancellationToken ct = default)
/// Task&lt;Result&lt;T&gt;&gt; PostFormAsync&lt;T&gt;(string requestUri, IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt; form, CancellationToken ct = default)
/// Task&lt;Result&lt;T&gt;&gt; PutJsonAsync&lt;T&gt;(string requestUri, object body, CancellationToken ct = default)
/// Task&lt;Result&lt;T&gt;&gt; DeleteAsync&lt;T&gt;(string requestUri, CancellationToken ct = default)
/// Task&lt;Result&lt;Stream&gt;&gt; GetStreamAsync(string requestUri, CancellationToken ct = default)
/// </code>
/// </remarks>
internal sealed class MStockApi : IAsyncDisposable
{
    private readonly HttpConnectorClient _client;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly MStockOptions _options;
    private readonly MStockErrorMapper _errors;

    private MStockApi(
        HttpConnectorClient client,
        HttpClient http,
        bool ownsHttpClient,
        MStockOptions options,
        MStockErrorMapper errors)
    {
        _client = client;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _options = options;
        _errors = errors;
    }

    /// <summary>The api_key this client is authenticating with, when it has a session.</summary>
    public string? ApiKey { get; private init; }

    /// <summary>
    /// Builds a client. <paramref name="session"/> is null during the authentication handshake,
    /// where there is no token to send yet.
    /// </summary>
    public static MStockApi Create(
        MStockOptions options,
        MStockErrorMapper errors,
        BrokerSession? session,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        var ownsClient = httpClient is null;
        var http = httpClient ?? new HttpClient();

        // BaseAddress and Timeout CANNOT BE SET ONCE A CLIENT HAS SENT A REQUEST — HttpClient
        // throws InvalidOperationException, not a no-op. A supplied client is very likely to be
        // a pooled one (that is the entire point of ConnectorActivationContext.HttpClientFactory),
        // so a second facet configuring it would take down the call rather than the connector.
        //
        // Respecting an existing configuration is also the correct semantic: a caller who hands
        // us a configured client did not ask for its timeout to be rewritten. The per-call
        // deadline does not depend on this anyway — SendAsync enforces RequestTimeout with a
        // linked cancellation token regardless of what the client's own timeout says.
        try
        {
            http.BaseAddress ??= options.BaseUrl;

            // One HttpClient serves both ordinary calls and the script-master download, so its
            // own timeout is set to the longer of the two and the shorter one is enforced per
            // call with a linked token. Setting it to RequestTimeout would abort every nightly
            // instrument ingest at fifteen seconds.
            http.Timeout = options.ScriptMasterTimeout > options.RequestTimeout
                ? options.ScriptMasterTimeout
                : options.RequestTimeout;
        }
        catch (InvalidOperationException)
        {
            // Already in use. Its existing base address and timeout stand.
        }

        var apiKey = session?.Extras.GetValueOrDefault(MStockSessionKeys.ApiKey);

        ApplyStandardHeaders(http, options, apiKey, session?.AccessToken);

        return new MStockApi(
            new HttpConnectorClient(
                http,
                errors,
                new ConnectorHttpOptions
                {
                    ConnectorId = MStockAuth.ConnectorId,
                    Json = MStockJson.Options,
                    // mStock reports business failures as HTTP 200 with status:"error".
                    BodyStatusField = "status",
                    BodyFailureStatusValues = ["error"],
                },
                logger ?? NullLogger.Instance),
            http,
            ownsClient,
            options,
            errors)
        {
            ApiKey = apiKey,
        };
    }

    /// <summary>
    /// The headers mStock requires on EVERY call, including the unauthenticated login.
    ///
    /// <c>Authorization: token {api_key}:{access_token}</c> is not a bearer token and not
    /// basic auth — it is mStock's own scheme, and sending it as <c>Bearer</c> is rejected
    /// with an unhelpful 403. <c>X-PrivateKey</c> repeats the api_key; both are required.
    /// </summary>
    private static void ApplyStandardHeaders(
        HttpClient http,
        MStockOptions options,
        string? apiKey,
        string? accessToken)
    {
        http.DefaultRequestHeaders.Remove("X-Mirae-Version");
        http.DefaultRequestHeaders.Add("X-Mirae-Version", options.ApiVersion);

        http.DefaultRequestHeaders.Remove("X-PrivateKey");
        if (!string.IsNullOrEmpty(apiKey))
        {
            http.DefaultRequestHeaders.Add("X-PrivateKey", apiKey);
        }

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
    /// Re-stamps the auth headers after a login or a refresh, so the same client instance can
    /// be used for the call that obtains the token and the calls that use it.
    /// </summary>
    public void UseSession(string apiKey, string accessToken) =>
        ApplyStandardHeaders(_http, _options, apiKey, accessToken);

    // --- verbs ---------------------------------------------------------------------------

    public Task<Result<TData>> GetAsync<TData>(
        string path,
        MStockQuery? query = null,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.GetAsync<MStockEnvelope<TData>>(uri, token), Combine(path, query), path, ct);

    public Task<Result<TData>> PostJsonAsync<TData>(
        string path,
        object body,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.PostJsonAsync<MStockEnvelope<TData>>(uri, body, token), path, path, ct);

    /// <summary>
    /// Form-encoded POST. The login and session routes — and only those — take
    /// <c>application/x-www-form-urlencoded</c>; sending them JSON produces a 400 that does
    /// not say why.
    /// </summary>
    public Task<Result<TData>> PostFormAsync<TData>(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> form,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.PostFormAsync<MStockEnvelope<TData>>(uri, form, token), path, path, ct);

    public Task<Result<TData>> PutJsonAsync<TData>(
        string path,
        object body,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.PutJsonAsync<MStockEnvelope<TData>>(uri, body, token), path, path, ct);

    public Task<Result<TData>> DeleteAsync<TData>(
        string path,
        MStockQuery? query = null,
        CancellationToken ct = default) =>
        SendAsync((uri, token) => _client.DeleteAsync<MStockEnvelope<TData>>(uri, token), Combine(path, query), path, ct);

    /// <summary>
    /// A GET whose envelope carries no <c>data</c> payload — logout being the case that
    /// matters. Only the envelope's status is inspected, so a bare
    /// <c>{"status":"success"}</c> is a success rather than a malformed response.
    /// </summary>
    public async Task<Result> GetVoidAsync(
        string path,
        MStockQuery? query = null,
        CancellationToken ct = default)
    {
        // JsonElement, not a placeholder class: logout answers {"status":"success",
        // "data":"Success"} — data is a STRING. Deserialising that into an object threw,
        // so every successful logout was reported as a malformed response. JsonElement
        // accepts whatever shape the route puts there, which is the point of a route whose
        // payload we have decided not to read.
        var result = await GetAsync<JsonElement>(Combine(path, query), query: null, ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Result.Success();
        }

        // A missing `data` node is expected here and is not a failure; anything else is.
        return result.Error.Code == ConnectorErrorCodes.Unknown
               && result.Error.Context?.GetValueOrDefault("field") == "data"
            ? Result.Success()
            : Result.Failure(result.Error);
    }

    /// <summary>
    /// Raw response body, for the script master. It is CSV, not JSON, and it is far too large
    /// to buffer — the caller reads it as a stream.
    /// </summary>
    public async Task<Result<Stream>> GetRawStreamAsync(
        string path,
        MStockQuery? query = null,
        CancellationToken ct = default)
    {
        try
        {
            // The SDK client is JSON-typed; the script master is a huge CSV, so this one call
            // goes straight to the underlying HttpClient and streams the body without buffering.
            var response = await _client.Inner
                .GetAsync(Combine(path, query), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Result<Stream>.Failure(_errors.MapHttp((int)response.StatusCode, body));
            }

            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A caller-requested cancellation is not a broker failure and must propagate as
            // itself; anything else becomes a canonical Result failure.
            return Result<Stream>.Failure(_errors.MapException(ex));
        }
    }

    private async Task<Result<TData>> SendAsync<TData>(
        Func<string, CancellationToken, Task<Result<MStockEnvelope<TData>>>> send,
        string requestUri,
        string route,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);

        Result<MStockEnvelope<TData>> response;
        try
        {
            response = await send(requestUri, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<TData>.Failure(_errors.MapException(ex));
        }

        if (response.IsFailure)
        {
            return Result<TData>.Failure(response.Error);
        }

        var envelope = response.Value;
        if (envelope is null)
        {
            return Result<TData>.Failure(MStockErrors.MissingField(route, "body"));
        }

        if (!envelope.IsSuccess)
        {
            return Result<TData>.Failure(_errors.MapEnvelope(
                envelope.Status,
                envelope.ErrorType ?? envelope.ErrorCode,
                envelope.Message));
        }

        if (envelope.Data is null)
        {
            return Result<TData>.Failure(MStockErrors.MissingField(route, "data"));
        }

        return envelope.Data;
    }

    private static string Combine(string path, MStockQuery? query) =>
        query is null || query.IsEmpty ? path : path + query.ToQueryString();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A query-string builder that escapes correctly and supports repeated keys, which mStock's
/// quote routes rely on (<c>?i=NSE:INFY&amp;i=NSE:TCS</c>).
/// </summary>
internal sealed class MStockQuery
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public bool IsEmpty => _pairs.Count == 0;

    public MStockQuery Add(string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        return this;
    }

    public MStockQuery Add(string key, decimal value) =>
        Add(key, value.ToString(CultureInfo.InvariantCulture));

    public MStockQuery AddAll(string key, IEnumerable<string> values)
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
/// Keys under which mStock-specific material is stashed in <see cref="BrokerSession.Extras"/>.
///
/// The shared contract deliberately has no field for an api_key or an enctoken — putting one
/// there would mean every future broker inherits mStock's vocabulary. Extras is the escape
/// hatch, and these constants keep both ends of it spelled the same way.
/// </summary>
internal static class MStockSessionKeys
{
    public const string ApiKey = "api_key";

    /// <summary>The token the streaming socket authenticates with. Not the access token.</summary>
    public const string EncToken = "enctoken";

    public const string PublicToken = "public_token";

    /// <summary>Exchanges this login is entitled to, comma separated, as mStock reported them.</summary>
    public const string Exchanges = "exchanges";

    public const string Products = "products";

    public const string OrderTypes = "order_types";

    public const string UserName = "user_name";
}

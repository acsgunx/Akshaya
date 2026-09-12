using System.Globalization;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// What a REST call authenticates with. Immutable: the host builds a connector per request, so a refreshed
/// token arrives as a new session and a new connector, never as a change under a live one.
/// </summary>
internal sealed record LongbridgeCredentials(string ClientId, string AccessToken, bool Paper)
{
    /// <summary>
    /// The data centre the credential belongs to. Longbridge prefixes tokens with <c>us_</c> for its US
    /// data centre; everything else is Asia-Pacific. Requests carry it in <c>x-dc-region</c> so the gateway
    /// routes them to where the account lives.
    /// </summary>
    public string DataCentre => AccessToken.StartsWith("us_", StringComparison.Ordinal) ? "us" : "ap";

    /// <summary>Keeps the token out of logs and exception messages.</summary>
    public override string ToString() => $"LongbridgeCredentials {{ ClientId = {ClientId}, Paper = {Paper} }}";
}

/// <summary>
/// The one place this connector talks to <see cref="HttpConnectorClient"/> for the REST routes.
///
/// It owns what every call needs and no facet should remember: the bearer token and client id, the
/// timestamp and data-centre headers, the paper-trading header, and unwrapping the
/// <c>{ code, message, data }</c> envelope — a failure can arrive as HTTP 200 with a non-zero code, so the
/// HTTP status alone is never trusted.
/// </summary>
internal sealed class LongbridgeApi : IAsyncDisposable
{
    private readonly HttpConnectorClient _client;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly LongbridgeOptions _options;
    private readonly LongbridgeErrorMapper _errors;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private bool _disposed;

    private LongbridgeApi(
        HttpClient http,
        bool ownsHttpClient,
        LongbridgeOptions options,
        LongbridgeErrorMapper errors,
        LongbridgeCredentials? credentials,
        IClock clock,
        ILogger logger)
    {
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _options = options;
        _errors = errors;
        _clock = clock;
        _logger = logger;
        Credentials = credentials;

        _client = new HttpConnectorClient(
            http,
            errors,
            new ConnectorHttpOptions
            {
                ConnectorId = LongbridgeAuth.ConnectorId,
                Json = LongbridgeJson.Options,
                VendorCodeFields = ["code", "error"],
                VendorMessageFields = ["message", "error_description", "msg"],
                HeaderProvider = BuildHeadersAsync,
            },
            logger);
    }

    public LongbridgeCredentials? Credentials { get; }

    public static LongbridgeApi Create(
        LongbridgeOptions options,
        LongbridgeErrorMapper errors,
        LongbridgeCredentials? credentials,
        IClock clock,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        var owns = httpClient is null;
        var http = httpClient ?? new HttpClient();

        try
        {
            http.BaseAddress ??= options.HttpBaseUrl;
            http.Timeout = options.RequestTimeout + TimeSpan.FromSeconds(5);
        }
        catch (InvalidOperationException)
        {
            // A pooled client already in use keeps its settings.
        }

        return new LongbridgeApi(http, owns, options, errors, credentials, clock, logger ?? NullLogger.Instance);
    }

    /// <summary>The underlying transport, for the OAuth token endpoint, which is not enveloped.</summary>
    public HttpConnectorClient Transport => _client;

    public Task<Result<T>> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(token => _client.GetAsync<LongbridgeEnvelope<T>>(path, token), path, isTradeWrite: false, ct);

    public Task<Result<T>> PostJsonAsync<T>(string path, object body, bool isTradeWrite, CancellationToken ct) =>
        SendAsync<T>(token => _client.PostJsonAsync<LongbridgeEnvelope<T>>(path, body, token), path, isTradeWrite, ct);

    public Task<Result<T>> PutJsonAsync<T>(string path, object body, bool isTradeWrite, CancellationToken ct) =>
        SendAsync<T>(token => _client.PutJsonAsync<LongbridgeEnvelope<T>>(path, body, token), path, isTradeWrite, ct);

    public Task<Result<T>> DeleteAsync<T>(string path, bool isTradeWrite, CancellationToken ct) =>
        SendAsync<T>(token => _client.DeleteAsync<LongbridgeEnvelope<T>>(path, token), path, isTradeWrite, ct);

    private async Task<Result<T>> SendAsync<T>(
        Func<CancellationToken, Task<Result<LongbridgeEnvelope<T>>>> send,
        string path,
        bool isTradeWrite,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.RequestTimeout);

        Result<LongbridgeEnvelope<T>> response;
        try
        {
            response = await send(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<T>.Failure(LongbridgeErrorMapper.MapException(ex, path));
        }

        if (response.IsFailure)
        {
            return Result<T>.Failure(response.Error);
        }

        var envelope = response.Value;
        if (envelope.Code != 0)
        {
            _logger.LogWarning(
                "{ConnectorId}: {Route} answered code {Code}: {Message}",
                LongbridgeAuth.ConnectorId,
                path,
                envelope.Code,
                envelope.Message ?? "(no message)");

            return Result<T>.Failure(_errors.MapEnvelope(envelope.Code, envelope.Message, path, isTradeWrite));
        }

        if (envelope.Data is { } data)
        {
            return Result<T>.Success(data);
        }

        return typeof(T) == typeof(LongbridgeEmpty)
            ? Result<T>.Success((T)(object)LongbridgeEmpty.Instance)
            : Result<T>.Failure(LongbridgeErrors.MissingField(path, "data"));
    }

    private ValueTask<IReadOnlyDictionary<string, string>> BuildHeadersAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept-Language"] = _options.Language,
            ["X-Timestamp"] = _clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };

        if (Credentials is { } credentials)
        {
            headers["Authorization"] = $"Bearer {credentials.AccessToken}";
            headers["X-Api-Key"] = credentials.ClientId;
            headers["x-dc-region"] = credentials.DataCentre;

            if (credentials.Paper)
            {
                headers["x-papertrading"] = "true";
            }
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(headers);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_ownsHttpClient)
            {
                _http.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Query strings with correct escaping. Longbridge's routes take snake_case parameters.</summary>
internal static class LongbridgeQuery
{
    public static string Build(string path, params (string Key, string? Value)[] parameters) =>
        HttpConnectorPath.WithQuery(path, parameters);
}

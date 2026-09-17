using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Tiger;

/// <summary>A request body under construction: only the fields that have a value are sent.</summary>
internal sealed class TigerBiz
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public static TigerBiz New() => new();

    public TigerBiz Add(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _values[key] = value;
        }

        return this;
    }

    public TigerBiz Add(string key, decimal? value)
    {
        if (value is { } number)
        {
            _values[key] = number;
        }

        return this;
    }

    public TigerBiz Add(string key, long? value)
    {
        if (value is { } number)
        {
            _values[key] = number;
        }

        return this;
    }

    public TigerBiz Add(string key, bool? value)
    {
        if (value is { } flag)
        {
            _values[key] = flag;
        }

        return this;
    }

    public TigerBiz Add(string key, object? value)
    {
        if (value is not null)
        {
            _values[key] = value;
        }

        return this;
    }

    public IReadOnlyDictionary<string, object> Values => _values;
}

/// <summary>
/// The one place this connector talks to Tiger's OpenAPI.
///
/// EVERY REQUEST IS ONE POST to one endpoint. The method is a parameter, not a path: the body carries the common
/// parameters (tiger id, method, charset, version, sign type, timestamp, device id), the method's own arguments as
/// the <c>biz_content</c> STRING, and the signature over all of them. See <see cref="TigerSigner"/>.
///
/// EVERY ANSWER IS CHECKED. Tiger signs the request's timestamp with its own key; an answer whose signature does not
/// verify is discarded rather than parsed, because something other than Tiger produced it. A non-zero <c>code</c> is
/// a failure even when the HTTP status is 200, and <c>data</c> is sometimes a JSON string rather than an object.
/// </summary>
internal sealed class TigerApi
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    private readonly HttpConnectorClient _client;
    private readonly TigerOptions _options;
    private readonly TigerErrorMapper _errors;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private TigerApi(HttpClient http, Uri endpoint, TigerOptions options, TigerErrorMapper errors, IClock clock, ILogger logger)
    {
        Endpoint = endpoint;
        _options = options;
        _errors = errors;
        _clock = clock;
        _logger = logger;

        _client = new HttpConnectorClient(
            http,
            errors,
            new ConnectorHttpOptions
            {
                ConnectorId = TigerAuth.ConnectorId,
                Json = TigerJson.Options,
                VendorCodeFields = ["code"],
                VendorMessageFields = ["message", "msg"],
            },
            logger);
    }

    public Uri Endpoint { get; }

    public static TigerApi Create(Uri endpoint, TigerOptions options, TigerErrorMapper errors, IClock clock, ILogger logger)
    {
        var http = Clients.GetOrAdd(endpoint.ToString(), _ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        return new TigerApi(http, endpoint, options, errors, clock, logger);
    }

    /// <summary>Calls a method and returns its <c>data</c>.</summary>
    public async Task<Result<JsonElement>> CallAsync(
        string method,
        TigerBiz? biz,
        TigerCredentials credentials,
        bool isTradeWrite,
        CancellationToken ct,
        string? version = null)
    {
        var encoding = Encoding.UTF8;
        var timestamp = TigerTime.RequestStamp(_clock.UtcNow);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tiger_id"] = credentials.TigerId,
            ["method"] = method,
            ["charset"] = _options.Charset,
            ["version"] = version ?? _options.Version,
            ["sign_type"] = _options.SignType,
            ["timestamp"] = timestamp,
            ["device_id"] = _options.DeviceId,
        };

        if (biz is not null)
        {
            parameters["biz_content"] = JsonSerializer.Serialize(biz.Values, TigerJson.Options);
        }

        var signature = TigerSigner.Sign(credentials.PrivateKey, TigerSigner.SignContent(parameters), encoding);
        if (signature.IsFailure)
        {
            return Result<JsonElement>.Failure(signature.Error);
        }

        parameters["sign"] = signature.Value;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.RequestTimeout);

        Result<(string Body, string Path)> raw;
        try
        {
            raw = await _client.SendRawAsync(() => Build(parameters, credentials, encoding), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<JsonElement>.Failure(TigerErrorMapper.MapException(ex, method));
        }

        if (raw.IsFailure)
        {
            return Result<JsonElement>.Failure(raw.Error);
        }

        TigerEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<TigerEnvelope>(raw.Value.Body, TigerJson.Options);
        }
        catch (JsonException ex)
        {
            return Result<JsonElement>.Failure(TigerErrorMapper.MapException(ex, method));
        }

        if (envelope is null)
        {
            return Result<JsonElement>.Failure(TigerErrors.MissingField(method, "body"));
        }

        if (_options.VerifyResponseSignature
            && envelope.Sign is { Length: > 0 } sign
            && !TigerSigner.Verify(PublicKeyFor(credentials), timestamp, sign, encoding))
        {
            return Result<JsonElement>.Failure(TigerErrors.UnverifiedResponse(method));
        }

        if (envelope.Code != 0)
        {
            _logger.LogWarning(
                "{ConnectorId}: {Method} answered code {Code}: {Message}",
                TigerAuth.ConnectorId,
                method,
                envelope.Code,
                envelope.Message ?? "(no message)");

            return Result<JsonElement>.Failure(_errors.MapEnvelope(envelope.Code, envelope.Message, method, isTradeWrite));
        }

        return Unwrap(envelope.Data, method);
    }

    /// <summary>Calls a method and deserialises its <c>data</c>.</summary>
    public async Task<Result<T>> CallAsync<T>(
        string method,
        TigerBiz? biz,
        TigerCredentials credentials,
        bool isTradeWrite,
        CancellationToken ct,
        string? version = null)
    {
        var data = await CallAsync(method, biz, credentials, isTradeWrite, ct, version).ConfigureAwait(false);
        if (data.IsFailure)
        {
            return Result<T>.Failure(data.Error);
        }

        try
        {
            return data.Value.Deserialize<T>(TigerJson.Options) is { } value
                ? value
                : Result<T>.Failure(TigerErrors.MissingField(method, "data"));
        }
        catch (JsonException ex)
        {
            return Result<T>.Failure(TigerErrorMapper.MapException(ex, method));
        }
    }

    /// <summary><c>data</c> is an object on most methods and a JSON string on a few.</summary>
    private static Result<JsonElement> Unwrap(JsonElement data, string method)
    {
        if (data.ValueKind != JsonValueKind.String)
        {
            return data;
        }

        var text = data.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<JsonElement>.Failure(TigerErrors.MissingField(method, "data"));
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Result<JsonElement>.Failure(TigerErrorMapper.MapException(ex, method));
        }
    }

    private string PublicKeyFor(TigerCredentials credentials) =>
        credentials.Paper ? _options.SandboxTigerPublicKey : _options.TigerPublicKey;

    private HttpRequestMessage Build(Dictionary<string, string> parameters, TigerCredentials credentials, Encoding encoding)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(parameters, TigerJson.Options), encoding, "application/json"),
        };

        if (credentials.Token is { Length: > 0 } token)
        {
            request.Headers.TryAddWithoutValidation("Authorization", token);
        }

        return request;
    }
}

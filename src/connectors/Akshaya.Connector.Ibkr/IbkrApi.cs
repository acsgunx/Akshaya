using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>The gateway's self-signed certificate, and when it may be trusted.</summary>
internal static class IbkrCertificates
{
    public static bool IsLoopback(string host)
    {
        var trimmed = host.Trim().Trim('[', ']');
        return string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address));
    }

    /// <summary>Trusted when the chain validates, or when the operator's policy says a self-signed gateway is acceptable.</summary>
    public static bool Trusts(GatewayAddress gateway, IbkrOptions options) =>
        options.AllowUntrustedCertificate || IsLoopback(gateway.Host);

    public static RemoteCertificateValidationCallback Validator(bool trustSelfSigned) =>
        (_, _, _, errors) => errors == SslPolicyErrors.None || trustSelfSigned;

    public static string HostForUri(string host)
    {
        var trimmed = host.Trim();
        return trimmed.Contains(':', StringComparison.Ordinal) && !trimmed.StartsWith('[') ? $"[{trimmed}]" : trimmed;
    }

    public static string Authority(GatewayAddress gateway) =>
        $"{HostForUri(gateway.Host)}:{gateway.Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// The one place this connector talks HTTP to the Client Portal Gateway.
///
/// ONE HTTP CLIENT PER GATEWAY, FOR THE PROCESS. The host builds a connector per request; a handler per connector
/// would put a TLS handshake on every call. Clients are cached by address and certificate policy and never
/// disposed, which is how <see cref="HttpClient"/> is meant to be used.
///
/// THE ORDER WALK IS HERE. Submitting or modifying an order may answer with a question — an order reply message —
/// instead of an acknowledgement, and the order does not work until the question is confirmed. Only message ids
/// the operator listed in <see cref="IbkrOptions.AutoConfirmMessageIds"/> are confirmed; any other question ends
/// the walk as a rejection that carries IBKR's text and the ids, because every question is one of the username's
/// own precautions.
///
/// Paths are relative (<c>iserver/accounts</c>), resolved under <see cref="IbkrOptions.BasePath"/>.
/// </summary>
internal sealed class IbkrApi
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    private readonly HttpConnectorClient _client;
    private readonly IbkrOptions _options;
    private readonly IbkrErrorMapper _errors;
    private readonly ILogger _logger;

    private IbkrApi(HttpClient http, GatewayAddress gateway, string baseAddress, IbkrOptions options, IbkrErrorMapper errors, ILogger logger)
    {
        Gateway = gateway;
        BaseAddress = baseAddress;
        _options = options;
        _errors = errors;
        _logger = logger;

        _client = new HttpConnectorClient(
            http,
            errors,
            new ConnectorHttpOptions
            {
                ConnectorId = IbkrAuth.ConnectorId,
                Json = IbkrJson.Options,
                VendorCodeFields = ["code"],
                VendorMessageFields = ["error", "message", "msg"],
            },
            logger);
    }

    public GatewayAddress Gateway { get; }

    /// <summary>The gateway's API root, which also keys per-gateway state.</summary>
    public string BaseAddress { get; }

    public static IbkrApi Create(GatewayAddress gateway, IbkrOptions options, IbkrErrorMapper errors, ILogger logger)
    {
        var trust = IbkrCertificates.Trusts(gateway, options);
        var basePath = "/" + options.BasePath.Trim('/') + "/";
        var baseAddress = $"https://{IbkrCertificates.Authority(gateway)}{basePath}";

        var http = Clients.GetOrAdd($"{baseAddress}|{trust}", _ => CreateClient(baseAddress, trust, options));
        return new IbkrApi(http, gateway, baseAddress, options, errors, logger);
    }

    public Task<Result<T>> GetAsync<T>(string path, CancellationToken ct) =>
        Guard(token => _client.GetAsync<T>(path, token), path, ct);

    /// <summary>A POST with a JSON body, or with none when <paramref name="body"/> is null.</summary>
    public Task<Result<T>> PostAsync<T>(string path, object? body, CancellationToken ct) =>
        Guard(token => _client.SendAsync<T>(() => Build(HttpMethod.Post, path, body), token), path, ct);

    public Task<Result<T>> DeleteAsync<T>(string path, CancellationToken ct) =>
        Guard(token => _client.DeleteAsync<T>(path, token), path, ct);

    /// <summary>For answers whose shape varies — an array here, an object there, an error sometimes.</summary>
    public Task<Result<JsonElement>> SendElementAsync(HttpMethod method, string path, object? body, CancellationToken ct) =>
        Guard(
            async token =>
            {
                var raw = await _client.SendRawAsync(() => Build(method, path, body), token).ConfigureAwait(false);
                if (raw.IsFailure)
                {
                    return Result<JsonElement>.Failure(raw.Error);
                }

                if (string.IsNullOrWhiteSpace(raw.Value.Body))
                {
                    return Result<JsonElement>.Failure(IbkrErrors.MissingField(path, "body"));
                }

                try
                {
                    using var document = JsonDocument.Parse(raw.Value.Body);
                    return Result<JsonElement>.Success(document.RootElement.Clone());
                }
                catch (JsonException ex)
                {
                    return Result<JsonElement>.Failure(IbkrErrorMapper.MapException(ex, path));
                }
            },
            path,
            ct);

    /// <summary>Submits or modifies an order and walks any reply messages to an acknowledgement.</summary>
    public async Task<Result<IbkrOrderAck>> WalkOrderAsync(string path, object body, CancellationToken ct)
    {
        var response = await SendElementAsync(HttpMethod.Post, path, body, ct).ConfigureAwait(false);

        for (var round = 0; ; round++)
        {
            if (response.IsFailure)
            {
                return Result<IbkrOrderAck>.Failure(response.Error);
            }

            var element = response.Value is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0
                ? array[0]
                : response.Value;

            if (element.ValueKind != JsonValueKind.Object)
            {
                return Result<IbkrOrderAck>.Failure(IbkrErrors.MissingField(path, "order_id"));
            }

            if (Text(element, "error") is { Length: > 0 } error)
            {
                return Result<IbkrOrderAck>.Failure(_errors.MapOrderText(error, path));
            }

            if (Text(element, "order_id") is { Length: > 0 } orderId)
            {
                return new IbkrOrderAck { OrderId = orderId, OrderStatus = Text(element, "order_status") };
            }

            if (Text(element, "id") is { Length: > 0 } replyId && element.TryGetProperty("message", out _))
            {
                var prompt = element.Deserialize<IbkrReplyPrompt>(IbkrJson.Options);
                var ids = prompt?.MessageIds ?? [];
                var text = string.Join(" ", prompt?.Message ?? []).Trim();

                if (round >= _options.MaxReplyRounds)
                {
                    return Result<IbkrOrderAck>.Failure(IbkrErrors.UnconfirmedPrompt(
                        text,
                        ids,
                        $"IBKR asked more than {_options.MaxReplyRounds} questions about one order"));
                }

                if (ids.Count == 0 || !ids.All(id => _options.AutoConfirmMessageIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
                {
                    return Result<IbkrOrderAck>.Failure(IbkrErrors.UnconfirmedPrompt(text, ids, reason: null));
                }

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "{ConnectorId}: confirming IBKR order reply message {MessageIds}, as the autoConfirmMessageIds setting allows.",
                        IbkrAuth.ConnectorId,
                        string.Join(",", ids));
                }

                response = await SendElementAsync(
                    HttpMethod.Post,
                    $"iserver/reply/{Uri.EscapeDataString(replyId)}",
                    new Dictionary<string, bool>(StringComparer.Ordinal) { ["confirmed"] = true },
                    ct).ConfigureAwait(false);

                continue;
            }

            // An "advanced rejection" (orderId, text, options) needs a person to dismiss it in IBKR's own tools.
            if (Text(element, "text") is { Length: > 0 } rejection)
            {
                return Result<IbkrOrderAck>.Failure(_errors.MapOrderText(rejection, path));
            }

            return Result<IbkrOrderAck>.Failure(IbkrErrors.MissingField(path, "order_id"));
        }
    }

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private async Task<Result<T>> Guard<T>(Func<CancellationToken, Task<Result<T>>> send, string path, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.RequestTimeout);

        try
        {
            return await send(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<T>.Failure(IbkrErrorMapper.MapException(ex, path));
        }
    }

    private static HttpRequestMessage Build(HttpMethod method, string path, object? body) => new(method, path)
    {
        Content = body is null
            ? null
            : new StringContent(JsonSerializer.Serialize(body, IbkrJson.Options), Encoding.UTF8, "application/json"),
    };

    private static HttpClient CreateClient(string baseAddress, bool trustSelfSigned, IbkrOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = IbkrCertificates.Validator(trustSelfSigned),
            },
        };

        // Per-call deadlines are applied by Guard; the client's own timeout would only race them.
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd("Akshaya/1.0");
        return http;
    }
}

using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>Anything that can carry a quote-protocol request: one socket, or the channel that keeps one open.</summary>
internal interface ILongbridgeQuoteRequester
{
    Task<Result<ReadOnlyMemory<byte>>> RequestAsync(byte command, byte[] body, CancellationToken ct);
}

/// <summary>Keys this connector writes into <see cref="BrokerSession.Extras"/>.</summary>
internal static class LongbridgeSessionKeys
{
    /// <summary>The OAuth client id: every REST call sends it as <c>X-Api-Key</c>, and a refresh needs it.</summary>
    public const string ClientId = "clientId";

    /// <summary><c>real</c> or <c>paper</c>.</summary>
    public const string Environment = "environment";

    /// <summary>The Longbridge member id the quote gateway reported at sign-in.</summary>
    public const string MemberId = "memberId";
}

/// <summary>What every call needs from a linked session, validated in one place.</summary>
internal sealed record LongbridgeAccount(string ClientId, string MemberId, bool Paper, string AccessToken)
{
    public LongbridgeCredentials Credentials => new(ClientId, AccessToken, Paper);

    public static Result<LongbridgeAccount> FromSession(BrokerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Extras.GetValueOrDefault(LongbridgeSessionKeys.ClientId) is not { Length: > 0 } clientId)
        {
            return Result<LongbridgeAccount>.Failure(LongbridgeErrors.MalformedSession("no client id"));
        }

        if (string.IsNullOrWhiteSpace(session.AccessToken))
        {
            return Result<LongbridgeAccount>.Failure(LongbridgeErrors.MalformedSession("no access token"));
        }

        var paper = string.Equals(
            session.Extras.GetValueOrDefault(LongbridgeSessionKeys.Environment),
            LongbridgeMaps.EnvironmentPaper,
            StringComparison.OrdinalIgnoreCase);

        var memberId = session.Extras.GetValueOrDefault(LongbridgeSessionKeys.MemberId) ?? session.AccountId;

        return new LongbridgeAccount(clientId, memberId, paper, session.AccessToken);
    }

    /// <summary>Keeps the token out of logs and exception messages.</summary>
    public override string ToString() => $"LongbridgeAccount {{ ClientId = {ClientId}, MemberId = {MemberId}, Paper = {Paper} }}";
}

/// <summary>
/// The transports one connector instance shares across its facets: the REST client, and a quote socket
/// opened on first use.
///
/// The host builds a connector per request, and most requests never touch the quote gateway — an order or
/// a balance read is REST — so opening the socket eagerly would put a one-time-password round trip and a
/// WebSocket handshake on requests that do not need them. It is opened on the first quote request and
/// reopened if it has dropped by the next one. The stream keeps sockets of its own: pushes belong to a
/// connection, and a request socket that also carried them would buffer them for nobody.
/// </summary>
internal sealed class LongbridgeChannel : ILongbridgeQuoteRequester, IAsyncDisposable
{
    private readonly LongbridgeOptions _options;
    private readonly LongbridgeErrorMapper _errors;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LongbridgeSocket? _quote;
    private int _disposed;

    public LongbridgeChannel(LongbridgeApi api, LongbridgeOptions options, LongbridgeErrorMapper errors, ILogger logger)
    {
        Api = api;
        _options = options;
        _errors = errors;
        _logger = logger;
    }

    public LongbridgeApi Api { get; }

    public async Task<Result<ReadOnlyMemory<byte>>> RequestAsync(byte command, byte[] body, CancellationToken ct)
    {
        var socket = await QuoteSocketAsync(ct).ConfigureAwait(false);
        return socket.IsFailure
            ? Result<ReadOnlyMemory<byte>>.Failure(socket.Error)
            : await socket.Value.RequestAsync(command, body, ct).ConfigureAwait(false);
    }

    private async Task<Result<LongbridgeSocket>> QuoteSocketAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (_quote is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_quote is { IsOpen: true } opened)
            {
                return opened;
            }

            if (_quote is { } stale)
            {
                _quote = null;
                await stale.DisposeAsync().ConfigureAwait(false);
            }

            var socket = await LongbridgeSocket
                .OpenAsync(_options.QuoteSocketUrl, Api, _options, _errors, _logger, receivePushes: false, ct)
                .ConfigureAwait(false);

            if (socket.IsSuccess)
            {
                _quote = socket.Value;
            }

            return socket;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_quote is { } quote)
        {
            await quote.DisposeAsync().ConfigureAwait(false);
        }

        await Api.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

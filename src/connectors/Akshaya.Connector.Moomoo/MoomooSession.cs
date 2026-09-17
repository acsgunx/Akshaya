using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>What can send a request to OpenD: one connection, or the connector's shared channel.</summary>
internal interface IMoomooRequester
{
    Task<Result<TS2C>> RequestAsync<TC2S, TS2C>(int protoId, TC2S c2s, CancellationToken ct);

    Task<Result<TS2C>> RequestAsync<TC2S, TS2C>(int protoId, TC2S c2s, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Keys under which moomoo-specific facts are stashed in <see cref="BrokerSession.Extras"/>. The shared
/// contract has no field for a trading environment or a market list, and must not grow one for a
/// single broker.
/// </summary>
internal static class MoomooSessionKeys
{
    /// <summary>Trd_Common.TrdEnv as a number: 1 real, 0 paper.</summary>
    public const string TrdEnv = "trdEnv";

    /// <summary>In-scope markets the account may trade, as SDK prefixes: <c>US,HK</c>.</summary>
    public const string Markets = "markets";

    public const string Environment = "environment";

    /// <summary>cash or margin, when OpenD said.</summary>
    public const string AccountType = "accountType";

    public const string SecurityFirm = "securityFirm";

    /// <summary>The last four digits only. The full card number identifies the customer and is not needed.</summary>
    public const string CardNumberSuffix = "cardSuffix";

    public const string LoginUserId = "loginUserId";
}

/// <summary>
/// The account a session is bound to, parsed once per call. Everything a trading header needs lives
/// here, so no facet reads <see cref="BrokerSession.Extras"/> by string key.
/// </summary>
internal sealed record MoomooAccount(ulong AccId, int TrdEnv, IReadOnlyList<MoomooMarket> Markets)
{
    public bool IsPaper => TrdEnv == MoomooMaps.TrdEnvSimulate;

    public bool Trades(MoomooMarket market) => Markets.Contains(market);

    /// <summary>
    /// The header every trading protocol requires. The market in it must be one the account is
    /// authorised for, or OpenD rejects the request — which is why it is chosen per call rather than
    /// fixed per session: a universal account trades both.
    /// </summary>
    public OpenDTrdHeader HeaderFor(MoomooMarket market) => new()
    {
        TrdEnv = TrdEnv,
        AccId = AccId,
        TrdMarket = market.TrdMarket,
    };

    /// <summary>Gates on the host's session check first, then parses the account out of it.</summary>
    public static Result<MoomooAccount> Require(Func<Result<BrokerSession>> requireSession)
    {
        var session = requireSession();
        return session.IsFailure ? Result<MoomooAccount>.Failure(session.Error) : FromSession(session.Value);
    }

    public static Result<MoomooAccount> FromSession(BrokerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!ulong.TryParse(session.AccountId, NumberStyles.None, CultureInfo.InvariantCulture, out var accId) || accId == 0)
        {
            return Result<MoomooAccount>.Failure(MoomooErrors.MalformedSession("the account id is not an OpenD account id"));
        }

        if (!session.Extras.TryGetValue(MoomooSessionKeys.TrdEnv, out var envText)
            || !int.TryParse(envText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var trdEnv)
            || trdEnv is not (MoomooMaps.TrdEnvReal or MoomooMaps.TrdEnvSimulate))
        {
            return Result<MoomooAccount>.Failure(MoomooErrors.MalformedSession("the trading environment is missing"));
        }

        var markets = new List<MoomooMarket>(2);
        foreach (var prefix in (session.Extras.GetValueOrDefault(MoomooSessionKeys.Markets) ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var market = MoomooMaps.MarketForPrefix(prefix);
            if (market.IsSuccess && !markets.Contains(market.Value))
            {
                markets.Add(market.Value);
            }
        }

        return markets.Count == 0
            ? Result<MoomooAccount>.Failure(MoomooErrors.MalformedSession("it names no US or Hong Kong market"))
            : Result<MoomooAccount>.Success(new MoomooAccount(accId, trdEnv, markets));
    }
}

/// <summary>
/// The connector's one request connection to OpenD, opened on first use and reused for the connector's
/// lifetime.
///
/// Lazy, unlike the Zerodha connector's eagerly built HTTP client, because constructing a connector must
/// not touch the network — the host builds one per request, and many requests never call OpenD at all.
/// The race a lazy resource invites (two callers opening two sockets) is closed by the gate. A connection
/// that drops is replaced on the next call, never retried within one: a trade write is not repeated here,
/// and the host's resilience decorator decides about reads.
/// </summary>
internal sealed class MoomooChannel : IMoomooRequester, IAsyncDisposable
{
    private readonly GatewayAddress? _address;
    private readonly MoomooOptions _options;
    private readonly MoomooErrorMapper _errors;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MoomooConnection? _connection;
    private bool _disposed;

    public MoomooChannel(
        GatewayAddress? address,
        MoomooOptions options,
        MoomooErrorMapper errors,
        IClock clock,
        ILogger logger)
    {
        _address = address;
        _options = options;
        _errors = errors;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<MoomooConnection>> GetAsync(CancellationToken ct)
    {
        if (_address is null)
        {
            return Result<MoomooConnection>.Failure(MoomooErrors.NoGatewayAddress());
        }

        if (Volatile.Read(ref _connection) is { IsOpen: true } current)
        {
            return Result<MoomooConnection>.Success(current);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is { IsOpen: true } opened)
            {
                return Result<MoomooConnection>.Success(opened);
            }

            if (_connection is { } dead)
            {
                await dead.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _connection, null);
            }

            var fresh = await MoomooConnection
                .OpenAsync(_address, _options, _errors, _clock, _logger, receivePushes: false, ct)
                .ConfigureAwait(false);

            if (fresh.IsFailure)
            {
                return fresh;
            }

            Volatile.Write(ref _connection, fresh.Value);
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<TS2C>> RequestAsync<TC2S, TS2C>(int protoId, TC2S c2s, CancellationToken ct)
    {
        var connection = await GetAsync(ct).ConfigureAwait(false);
        return connection.IsFailure
            ? Result<TS2C>.Failure(connection.Error)
            : await connection.Value.RequestAsync<TC2S, TS2C>(protoId, c2s, ct).ConfigureAwait(false);
    }

    public async Task<Result<TS2C>> RequestAsync<TC2S, TS2C>(int protoId, TC2S c2s, TimeSpan timeout, CancellationToken ct)
    {
        var connection = await GetAsync(ct).ConfigureAwait(false);
        return connection.IsFailure
            ? Result<TS2C>.Failure(connection.Error)
            : await connection.Value.RequestAsync<TC2S, TS2C>(protoId, c2s, timeout, ct).ConfigureAwait(false);
    }

    public async Task<Result<TS2C>> RequestTradeWriteAsync<TC2S, TS2C>(
        int protoId,
        Func<OpenDPacketId, TC2S> build,
        CancellationToken ct)
    {
        var connection = await GetAsync(ct).ConfigureAwait(false);
        return connection.IsFailure
            ? Result<TS2C>.Failure(connection.Error)
            : await connection.Value.RequestTradeWriteAsync<TC2S, TS2C>(protoId, build, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Volatile.Read(ref _connection) is { } connection)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}

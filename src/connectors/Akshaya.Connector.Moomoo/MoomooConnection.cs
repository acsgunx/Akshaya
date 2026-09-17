using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>A frame OpenD sent without being asked: a quote, an order update, a status notification.</summary>
internal readonly record struct MoomooPush(int ProtoId, byte[] Body);

/// <summary>
/// One TCP connection to OpenD. Framing, request/response correlation, KeepAlive and push routing, and
/// nothing about any particular protocol's meaning.
///
/// How it fits together:
///
/// <code>
///   RequestAsync ─► serial n ─► pending[n] ─► write frame
///                                   ▲
///   read loop ── frame ─┬─ push id? ─► pushes channel (stream only)
///                       └─ serial n ─► pending[n].SetResult ─► decode ─► Result&lt;S2C&gt;
/// </code>
///
/// Three things are worth knowing before changing anything.
///
/// ONE READER. Only the read loop touches the socket's read side, and it never blocks on a consumer:
/// responses complete a waiter and pushes go to an unbounded channel. A read loop that waited on a slow
/// quote consumer would stall every order acknowledgement queued behind it.
///
/// PUSHES ARE ROUTED BY PROTOCOL ID, NEVER BY SERIAL. OpenD numbers its pushes itself, and those numbers
/// can equal a serial we are waiting on. See <see cref="MoomooProtoId.IsPush"/>.
///
/// A CLOSED CONNECTION FAILS EVERYTHING AT ONCE. When the socket dies, every pending request completes
/// with the same GatewayUnavailable error rather than each waiting out its own timeout — the difference
/// between one clear failure and fifteen seconds of a trader staring at a spinner.
/// </summary>
internal sealed class MoomooConnection : IMoomooRequester, IAsyncDisposable
{
    /// <summary>Marks a waiter completed because the connection closed, not because OpenD answered.</summary>
    private const int ClosedSentinel = -1;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly GatewayAddress _address;
    private readonly MoomooOptions _options;
    private readonly MoomooErrorMapper _errors;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<MoomooFrame>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<Error> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<MoomooPush>? _pushes;

    private Task? _readLoop;
    private Task? _keepAliveLoop;
    private int _serial;
    private int _disposed;

    private MoomooConnection(
        TcpClient tcp,
        GatewayAddress address,
        MoomooOptions options,
        MoomooErrorMapper errors,
        IClock clock,
        ILogger logger,
        bool receivePushes)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _address = address;
        _options = options;
        _errors = errors;
        _clock = clock;
        _logger = logger;

        _pushes = receivePushes
            ? Channel.CreateUnbounded<MoomooPush>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true })
            : null;
    }

    /// <summary>OpenD's id for this connection. Trade writes carry it as replay protection.</summary>
    public ulong ConnId { get; private set; }

    /// <summary>The moomoo user OpenD is signed in as.</summary>
    public ulong LoginUserId { get; private set; }

    public GatewayAddress Address => _address;

    /// <summary>Pushes, for a connection opened to receive them; null otherwise.</summary>
    public ChannelReader<MoomooPush>? Pushes => _pushes?.Reader;

    /// <summary>Completes, with the reason, when the connection is gone for any cause.</summary>
    public Task<Error> Closed => _closed.Task;

    public bool IsOpen => !_closed.Task.IsCompleted;

    /// <summary>
    /// Connects, performs InitConnect and starts the KeepAlive loop. A refused or silent gateway is a
    /// failure result, never an exception.
    /// </summary>
    /// <param name="address">Where OpenD listens, as the host resolved it.</param>
    /// <param name="options">Timeouts, frame limits and the client identity sent in InitConnect.</param>
    /// <param name="errors">Maps a refused InitConnect.</param>
    /// <param name="clock">Stamps KeepAlive requests.</param>
    /// <param name="logger">Receives transport warnings.</param>
    /// <param name="ct">Cancels the handshake.</param>
    /// <param name="receivePushes">
    /// True for the stream's connection: OpenD is asked for notifications and JSON pushes, and they are
    /// kept. Request-scoped connections leave it false, so a quote subscription made for one depth
    /// lookup cannot fill an unread buffer.
    /// </param>
    public static async Task<Result<MoomooConnection>> OpenAsync(
        GatewayAddress address,
        MoomooOptions options,
        MoomooErrorMapper errors,
        IClock clock,
        ILogger logger,
        bool receivePushes,
        CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };

        using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectDeadline.CancelAfter(options.ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(address.Host, address.Port, connectDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                return Result<MoomooConnection>.Failure(MoomooErrors.GatewayUnreachable(address, "the connection timed out"));
            }
            catch (SocketException ex)
            {
                tcp.Dispose();
                return Result<MoomooConnection>.Failure(MoomooErrors.GatewayUnreachable(address, ex.SocketErrorCode.ToString()));
            }
        }

        var connection = new MoomooConnection(tcp, address, options, errors, clock, logger, receivePushes);
        connection._readLoop = Task.Run(connection.ReadLoopAsync, CancellationToken.None);

        var init = await connection.SendAsync<OpenDInitConnectC2S, OpenDInitConnectS2C>(
            MoomooProtoId.InitConnect,
            connection.NextSerial(),
            new OpenDInitConnectC2S
            {
                ClientVer = options.ClientVersion,
                ClientId = options.ClientId,
                RecvNotify = receivePushes,
                PacketEncAlgo = MoomooMaps.PacketEncAlgoNone,
                PushProtoFmt = MoomooMaps.ProtoFmtJson,
                ProgrammingLanguage = "C#",
            },
            options.ConnectTimeout,
            ct).ConfigureAwait(false);

        if (init.IsFailure)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            // Whatever went wrong — a timeout, a closed socket, OpenD refusing the client version — the
            // handshake did not complete, and to every caller that means the gateway is not usable.
            return Result<MoomooConnection>.Failure(init.Error with { Code = ConnectorErrorCodes.GatewayUnavailable });
        }

        connection.ConnId = init.Value.ConnId;
        connection.LoginUserId = init.Value.LoginUserId;

        var interval = init.Value.KeepAliveInterval > 0
            ? TimeSpan.FromSeconds(init.Value.KeepAliveInterval)
            : options.DefaultKeepAliveInterval;

        connection._keepAliveLoop = Task.Run(() => connection.KeepAliveLoopAsync(interval), CancellationToken.None);

        return Result<MoomooConnection>.Success(connection);
    }

    /// <summary>One request, one response, with the default timeout.</summary>
    public Task<Result<TS2C>> RequestAsync<TC2S, TS2C>(int protoId, TC2S c2s, CancellationToken ct) =>
        SendAsync<TC2S, TS2C>(protoId, NextSerial(), c2s, _options.RequestTimeout, ct);

    /// <summary>One request, one response, with an explicit timeout (the whole-market static lists).</summary>
    public Task<Result<TS2C>> RequestAsync<TC2S, TS2C>(int protoId, TC2S c2s, TimeSpan timeout, CancellationToken ct) =>
        SendAsync<TC2S, TS2C>(protoId, NextSerial(), c2s, timeout, ct);

    /// <summary>
    /// A trade write. OpenD requires a <c>packetID</c> of this connection's id and a serial number inside
    /// the body, as replay protection, so the body is built only once the serial is allocated — and the
    /// frame carries the same serial, which keeps one number on both sides of the exchange.
    /// </summary>
    public Task<Result<TS2C>> RequestTradeWriteAsync<TC2S, TS2C>(
        int protoId,
        Func<OpenDPacketId, TC2S> build,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);

        var serial = NextSerial();
        var c2s = build(new OpenDPacketId { ConnId = ConnId, SerialNo = serial });
        return SendAsync<TC2S, TS2C>(protoId, serial, c2s, _options.RequestTimeout, ct);
    }

    private uint NextSerial() => unchecked((uint)Interlocked.Increment(ref _serial));

    private async Task<Result<TS2C>> SendAsync<TC2S, TS2C>(
        int protoId,
        uint serial,
        TC2S c2s,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (_closed.Task.IsCompleted)
        {
            return Result<TS2C>.Failure(await _closed.Task.ConfigureAwait(false));
        }

        var waiter = new TaskCompletionSource<MoomooFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[serial] = waiter;

        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new MoomooRequest<TC2S> { C2S = c2s }, MoomooJson.Options);
            var frame = MoomooFrameCodec.Encode(protoId, serial, body);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(timeout);

            try
            {
                await _writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    await _stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }

                var response = await waiter.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                return Decode<TS2C>(protoId, response);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Either the per-call deadline or the connection's own lifetime ended. Which one matters:
                // a closed connection has a precise reason, a slow OpenD has only a timeout.
                return _closed.Task.IsCompleted
                    ? Result<TS2C>.Failure(await _closed.Task.ConfigureAwait(false))
                    : Result<TS2C>.Failure(MoomooErrors.TimedOut(protoId, timeout));
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                var error = MoomooErrorMapper.MapException(ex, protoId);
                Close(error);
                return Result<TS2C>.Failure(error);
            }
        }
        finally
        {
            _pending.TryRemove(serial, out _);
        }
    }

    private Result<TS2C> Decode<TS2C>(int protoId, MoomooFrame frame)
    {
        if (frame.ProtoId == ClosedSentinel)
        {
            return Result<TS2C>.Failure(_closed.Task.IsCompleted
                ? _closed.Task.Result
                : MoomooErrors.ConnectionClosed(_address, "closed while waiting for a response"));
        }

        if (frame.ProtoId != protoId)
        {
            // A response under our serial but for a different protocol means the two sides are out of
            // step. Reading it as the protocol we asked for would be reading an order as a quote.
            return Result<TS2C>.Failure(MoomooErrors.Unreadable(protoId, $"protocol {MoomooProtoId.Name(frame.ProtoId)}"));
        }

        MoomooResponse<TS2C>? response;
        try
        {
            response = JsonSerializer.Deserialize<MoomooResponse<TS2C>>(frame.Body, MoomooJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "{ConnectorId}: could not read the {Protocol} response at {JsonPath}.",
                MoomooAuth.ConnectorId,
                MoomooProtoId.Name(protoId),
                ex.Path ?? "(unknown member)");

            return Result<TS2C>.Failure(MoomooErrors.Unreadable(protoId, ex.Path));
        }

        if (response is null)
        {
            return Result<TS2C>.Failure(MoomooErrors.Unreadable(protoId, null));
        }

        if (response.RetType != MoomooRetType.Succeed)
        {
            _logger.LogWarning(
                "{ConnectorId}: {Protocol} answered retType={RetType} errCode={ErrCode}: {Message}",
                MoomooAuth.ConnectorId,
                MoomooProtoId.Name(protoId),
                response.RetType,
                response.ErrCode,
                response.RetMsg ?? "(no message)");

            return Result<TS2C>.Failure(_errors.MapResponse(protoId, response.RetType, response.ErrCode, response.RetMsg));
        }

        if (response.S2C is { } s2c)
        {
            return Result<TS2C>.Success(s2c);
        }

        // Several protocols legitimately answer with no s2c at all.
        return typeof(TS2C) == typeof(OpenDEmpty)
            ? Result<TS2C>.Success((TS2C)(object)OpenDEmpty.Instance)
            : Result<TS2C>.Failure(MoomooErrors.MissingField(protoId, "s2c"));
    }

    private async Task ReadLoopAsync()
    {
        var ct = _lifetime.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await MoomooFrameCodec.ReadAsync(_stream, _options.MaxFrameBytes, ct).ConfigureAwait(false);

                if (MoomooProtoId.IsPush(frame.ProtoId))
                {
                    // Dropped on a request-scoped connection, which never asked for pushes but can still
                    // receive a notification OpenD sends every client.
                    _pushes?.Writer.TryWrite(new MoomooPush(frame.ProtoId, frame.Body));
                    continue;
                }

                if (_pending.TryGetValue(frame.SerialNo, out var waiter))
                {
                    waiter.TrySetResult(frame);
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "{ConnectorId}: discarded an unmatched {Protocol} frame (serial {Serial}); its caller had already given up.",
                        MoomooAuth.ConnectorId,
                        MoomooProtoId.Name(frame.ProtoId),
                        frame.SerialNo);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Close(MoomooErrors.ConnectionClosed(_address, "closed by the connector"));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            if (!_closed.Task.IsCompleted)
            {
                _logger.LogWarning(ex, "{ConnectorId}: the OpenD connection at {Gateway} failed.", MoomooAuth.ConnectorId, _address);
            }

            var mapped = MoomooErrorMapper.MapException(ex, null);
            Close(MoomooErrors.ConnectionClosed(_address, mapped.Message));
        }
    }

    /// <summary>
    /// OpenD drops a connection that misses its KeepAlive, at the interval InitConnect announced. A failed
    /// KeepAlive on a live connection is logged rather than fatal: the next one either succeeds or the read
    /// loop sees the socket die.
    /// </summary>
    private async Task KeepAliveLoopAsync(TimeSpan interval)
    {
        var ct = _lifetime.Token;
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var sent = await RequestAsync<OpenDKeepAliveC2S, OpenDKeepAliveS2C>(
                    MoomooProtoId.KeepAlive,
                    new OpenDKeepAliveC2S { Time = _clock.UtcNow.ToUnixTimeSeconds() },
                    ct).ConfigureAwait(false);

                if (sent.IsFailure && IsOpen)
                {
                    _logger.LogWarning(
                        "{ConnectorId}: KeepAlive to OpenD at {Gateway} failed: {Error}",
                        MoomooAuth.ConnectorId,
                        _address,
                        sent.Error.ToString());
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Connection closing.
        }
    }

    private void Close(Error reason)
    {
        if (!_closed.TrySetResult(reason))
        {
            return;
        }

        foreach (var (serial, waiter) in _pending)
        {
            waiter.TrySetResult(new MoomooFrame(ClosedSentinel, serial, 0, []));
        }

        _pushes?.Writer.TryComplete();

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed; nothing is left to cancel.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Close(MoomooErrors.ConnectionClosed(_address, "the connector finished with it"));

        // Disposing the socket is what unblocks a read that is not observing cancellation.
        _stream.Dispose();
        _tcp.Dispose();

        foreach (var loop in (Task?[])[_readLoop, _keepAliveLoop])
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}

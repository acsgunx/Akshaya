using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>A packet the gateway pushed without being asked.</summary>
internal readonly record struct LongbridgePush(byte Command, byte[] Body);

/// <summary>
/// One authenticated connection to a Longbridge gateway (quote or trade), speaking the binary packet
/// protocol inside WebSocket binary messages.
///
/// <code>
///   request   [header][cmd][request_id u32][timeout_ms u16][body_len u24][body]
///   response  [header][cmd][request_id u32][status u8][body_len u24][body]
///   push      [header][cmd][body_len u24][body]
///
///   header:  low four bits = type (1 request, 2 response, 3 push)
///            0x10 = a 24-byte signature (8-byte nonce + 16-byte signature) follows the body
///            0x20 = the body is gzipped
/// </code>
///
/// All integers are BIG-endian — the opposite of OpenD's framing, and the documentation says so in as
/// many words. The handshake is REST then socket: a one-time password from <c>GET /v1/socket/token</c>,
/// the WebSocket upgrade with <c>version=1&amp;codec=1&amp;platform=9</c>, then an AUTH request carrying
/// the password. The password is single-use, so every reconnect asks for a new one.
/// </summary>
internal sealed class LongbridgeSocket : ILongbridgeQuoteRequester, IAsyncDisposable
{
    private const byte TypeRequest = 1;
    private const byte TypeResponse = 2;
    private const byte TypePush = 3;
    private const byte FlagVerify = 0x10;
    private const byte FlagGzip = 0x20;
    private const int SignatureLength = 24;
    private const int MaxBodyLength = (1 << 24) - 1;

    private readonly ClientWebSocket _socket;
    private readonly LongbridgeOptions _options;
    private readonly LongbridgeErrorMapper _errors;
    private readonly ILogger _logger;
    private readonly string _name;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Result<ReadOnlyMemory<byte>>>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<Error> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<LongbridgePush>? _pushes;

    private Task? _readLoop;
    private int _requestId;
    private int _disposed;

    private LongbridgeSocket(ClientWebSocket socket, string name, LongbridgeOptions options, LongbridgeErrorMapper errors, ILogger logger, bool receivePushes)
    {
        _socket = socket;
        _name = name;
        _options = options;
        _errors = errors;
        _logger = logger;
        _pushes = receivePushes
            ? Channel.CreateUnbounded<LongbridgePush>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true })
            : null;
    }

    public ChannelReader<LongbridgePush>? Pushes => _pushes?.Reader;

    public Task<Error> Closed => _closed.Task;

    public bool IsOpen => !_closed.Task.IsCompleted;

    /// <summary>The Longbridge member id, learned from the quote profile. Zero until asked for.</summary>
    public long MemberId { get; private set; }

    /// <summary>Opens and authenticates a socket. Any failure is a result, never an exception.</summary>
    /// <param name="endpoint">The quote or trade gateway.</param>
    /// <param name="api">An authenticated REST client, for the one-time password.</param>
    /// <param name="options">Timeouts and frame limits.</param>
    /// <param name="errors">Maps a refused request's status and error body.</param>
    /// <param name="logger">Receives transport warnings.</param>
    /// <param name="receivePushes">True for the stream's sockets; request-only sockets drop pushes.</param>
    /// <param name="ct">Cancels the handshake.</param>
    public static async Task<Result<LongbridgeSocket>> OpenAsync(
        Uri endpoint,
        LongbridgeApi api,
        LongbridgeOptions options,
        LongbridgeErrorMapper errors,
        ILogger logger,
        bool receivePushes,
        CancellationToken ct)
    {
        var name = endpoint.Host.Contains("trade", StringComparison.OrdinalIgnoreCase) ? "trade socket" : "quote socket";

        var token = await api.GetAsync<LbSocketToken>("/v1/socket/token", ct).ConfigureAwait(false);
        if (token.IsFailure)
        {
            return Result<LongbridgeSocket>.Failure(token.Error);
        }

        if (string.IsNullOrWhiteSpace(token.Value.Otp))
        {
            return Result<LongbridgeSocket>.Failure(LongbridgeErrors.MissingField("/v1/socket/token", "otp"));
        }

        if (token.Value.Limit > 0 && token.Value.Online >= token.Value.Limit)
        {
            // Opening one more would be refused, or would push out a connection another tool is using.
            return Result<LongbridgeSocket>.Failure(new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                $"Longbridge already has {token.Value.Online} of this account's {token.Value.Limit} socket connections open. "
                + "Close a Longbridge app or tool that is connected with the same credentials."));
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Accept-Language", options.Language);
        if (api.Credentials is { } credentials)
        {
            socket.Options.SetRequestHeader("x-dc-region", credentials.DataCentre);
        }

        socket.Options.KeepAliveInterval = options.SocketKeepAlive;
        socket.Options.KeepAliveTimeout = options.SocketKeepAlive;

        var uri = new UriBuilder(endpoint) { Query = "version=1&codec=1&platform=9" }.Uri;

        using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectDeadline.CancelAfter(options.SocketConnectTimeout);
            try
            {
                await socket.ConnectAsync(uri, connectDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException
                                           or OperationCanceledException && !ct.IsCancellationRequested)
            {
                socket.Dispose();
                return Result<LongbridgeSocket>.Failure(LongbridgeErrorMapper.MapException(ex, $"the {name} connection"));
            }
        }

        var connection = new LongbridgeSocket(socket, name, options, errors, logger, receivePushes);
        connection._readLoop = Task.Run(connection.ReadLoopAsync, CancellationToken.None);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["accept-language"] = options.Language };
        var auth = await connection.RequestAsync(LongbridgeCommand.Auth, LbAuthRequest.Encode(token.Value.Otp, metadata), ct)
            .ConfigureAwait(false);

        if (auth.IsFailure)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return Result<LongbridgeSocket>.Failure(auth.Error with
            {
                Code = auth.Error.Code == ConnectorErrorCodes.Unknown ? ConnectorErrorCodes.SessionExpired : auth.Error.Code,
            });
        }

        return Result<LongbridgeSocket>.Success(connection);
    }

    /// <summary>One request, one response body.</summary>
    public async Task<Result<ReadOnlyMemory<byte>>> RequestAsync(byte command, byte[] body, CancellationToken ct)
    {
        if (_closed.Task.IsCompleted)
        {
            return Result<ReadOnlyMemory<byte>>.Failure(await _closed.Task.ConfigureAwait(false));
        }

        if (body.Length > MaxBodyLength)
        {
            return Result<ReadOnlyMemory<byte>>.Failure(LongbridgeErrors.InvalidRequest(
                $"A {LongbridgeCommand.Name(command)} request of {body.Length} bytes exceeds the protocol's 16 MB body limit."));
        }

        var id = unchecked((uint)Interlocked.Increment(ref _requestId));
        var waiter = new TaskCompletionSource<Result<ReadOnlyMemory<byte>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            var timeoutMs = (ushort)Math.Clamp(_options.SocketRequestTimeout.TotalMilliseconds, 1000, 60000);
            var frame = new byte[1 + 1 + 4 + 2 + 3 + body.Length];
            frame[0] = TypeRequest;
            frame[1] = command;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2), id);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), timeoutMs);
            WriteUInt24BigEndian(frame.AsSpan(8), body.Length);
            body.CopyTo(frame.AsSpan(11));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(_options.SocketRequestTimeout);

            try
            {
                await _sendGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    await _socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendGate.Release();
                }

                return await waiter.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return _closed.Task.IsCompleted
                    ? Result<ReadOnlyMemory<byte>>.Failure(await _closed.Task.ConfigureAwait(false))
                    : Result<ReadOnlyMemory<byte>>.Failure(new Error(
                        ConnectorErrorCodes.Timeout,
                        $"Longbridge did not answer {LongbridgeCommand.Name(command)} on the {_name} in time."));
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                var error = LongbridgeErrorMapper.MapException(ex, _name);
                Close(error);
                return Result<ReadOnlyMemory<byte>>.Failure(error);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Asks the quote gateway who this is (QueryUserQuoteProfile, command 4) and returns the member id.</summary>
    public async Task<Result<long>> QueryMemberIdAsync(CancellationToken ct)
    {
        var profile = await RequestAsync(4, [], ct).ConfigureAwait(false);
        if (profile.IsFailure)
        {
            return Result<long>.Failure(profile.Error);
        }

        try
        {
            var reader = new ProtoReader(profile.Value);
            while (reader.TryReadTag(out var field, out var wire))
            {
                if (field == 1)
                {
                    MemberId = reader.ReadInt64();
                }
                else
                {
                    reader.Skip(wire);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            return Result<long>.Failure(LongbridgeErrorMapper.MapException(ex, "QueryUserQuoteProfile"));
        }

        return MemberId > 0
            ? Result<long>.Success(MemberId)
            : Result<long>.Failure(LongbridgeErrors.MissingField("QueryUserQuoteProfile", "member_id"));
    }

    private async Task ReadLoopAsync()
    {
        var ct = _lifetime.Token;
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;

                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        var reason = string.IsNullOrWhiteSpace(_socket.CloseStatusDescription)
                            ? $"closed by Longbridge ({_socket.CloseStatus})"
                            : _socket.CloseStatusDescription;

                        Close(LongbridgeErrors.SocketClosed(reason));
                        return;
                    }

                    message.Write(buffer, 0, received.Count);
                    if (message.Length > _options.MaxFrameBytes)
                    {
                        throw new InvalidDataException($"A {_name} message exceeded {_options.MaxFrameBytes} bytes.");
                    }
                }
                while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                }
            }

            Close(LongbridgeErrors.SocketClosed("the connection ended"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Close(LongbridgeErrors.SocketClosed("closed by the connector"));
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidDataException)
        {
            if (IsOpen)
            {
                _logger.LogWarning(ex, "{ConnectorId}: the Longbridge {Socket} failed.", LongbridgeAuth.ConnectorId, _name);
            }

            Close(LongbridgeErrorMapper.MapException(ex, _name));
        }
    }

    private void Dispatch(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            return;
        }

        var header = packet[0];
        var type = header & 0x0F;
        var verify = (header & FlagVerify) != 0;
        var gzip = (header & FlagGzip) != 0;

        if (type == TypeResponse && packet.Length >= 10)
        {
            var command = packet[1];
            var id = BinaryPrimitives.ReadUInt32BigEndian(packet[2..]);
            var status = packet[6];
            var length = ReadUInt24BigEndian(packet[7..]);

            if (!TryBody(packet, 10, length, verify, gzip, out var body))
            {
                return;
            }

            if (_pending.TryGetValue(id, out var waiter))
            {
                waiter.TrySetResult(status == 0
                    ? Result<ReadOnlyMemory<byte>>.Success(body)
                    : Result<ReadOnlyMemory<byte>>.Failure(_errors.MapSocket(command, status, LbError.TryDecode(body))));
            }
        }
        else if (type == TypePush && packet.Length >= 5)
        {
            var command = packet[1];
            var length = ReadUInt24BigEndian(packet[2..]);

            if (TryBody(packet, 5, length, verify, gzip, out var body))
            {
                _pushes?.Writer.TryWrite(new LongbridgePush(command, body));
            }
        }
    }

    /// <summary>
    /// Slices a body out of a packet and inflates it when flagged. A packet whose declared length runs past
    /// its end is dropped whole: half a quote is a different quote.
    /// </summary>
    private bool TryBody(ReadOnlySpan<byte> packet, int offset, int length, bool verify, bool gzip, out byte[] body)
    {
        body = [];
        if (offset + length + (verify ? SignatureLength : 0) > packet.Length)
        {
            _logger.LogWarning("{ConnectorId}: dropped a truncated packet on the {Socket}.", LongbridgeAuth.ConnectorId, _name);
            return false;
        }

        var raw = packet.Slice(offset, length).ToArray();
        if (!gzip)
        {
            body = raw;
            return true;
        }

        using var input = new MemoryStream(raw);
        using var inflater = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var chunk = new byte[16 * 1024];
        int read;
        while ((read = inflater.Read(chunk, 0, chunk.Length)) > 0)
        {
            output.Write(chunk, 0, read);
            if (output.Length > _options.MaxFrameBytes)
            {
                throw new InvalidDataException($"A gzipped {_name} packet inflated past {_options.MaxFrameBytes} bytes.");
            }
        }

        body = output.ToArray();
        return true;
    }

    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> span) => (span[0] << 16) | (span[1] << 8) | span[2];

    private static void WriteUInt24BigEndian(Span<byte> span, int value)
    {
        span[0] = (byte)((value >> 16) & 0xFF);
        span[1] = (byte)((value >> 8) & 0xFF);
        span[2] = (byte)(value & 0xFF);
    }

    private void Close(Error reason)
    {
        if (!_closed.TrySetResult(reason))
        {
            return;
        }

        foreach (var waiter in _pending.Values)
        {
            waiter.TrySetResult(Result<ReadOnlyMemory<byte>>.Failure(reason));
        }

        _pushes?.Writer.TryComplete();

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort: the connection is going away regardless.
            }
        }

        Close(LongbridgeErrors.SocketClosed("the connector finished with it"));
        _socket.Dispose();

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}

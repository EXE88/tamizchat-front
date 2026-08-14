using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TamizChat.Core.Protocol;

namespace TamizChat.Core;

/// <summary>A frame the server sent on its own, not as a reply to a request.</summary>
public sealed class ServerEventArgs(string type, JsonElement? data) : EventArgs
{
    public string Type { get; } = type;

    public JsonElement? Data { get; } = data;

    /// <summary>Reads the payload as <typeparamref name="T"/>, or null if it will not fit.</summary>
    public T? As<T>() where T : class =>
        Data is null ? null : Data.Value.Deserialize<T>(TamizChatClient.JsonOptions);
}

/// <summary>
/// The client half of the TamizChat protocol: one WebSocket, a hello handshake,
/// and request frames whose replies are matched back by id.
///
/// Everything except the handshake is asynchronous and unordered, so a reply is
/// found by its id rather than by being the next frame to arrive.
/// </summary>
public sealed class TamizChatClient : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The socket's frame limit on the server is 64 KB.</summary>
    private const int MaxFrameBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _readLoop;
    private long _nextId;

    /// <summary>Raised for every frame that is not a reply to a pending request.</summary>
    public event EventHandler<ServerEventArgs>? ServerEvent;

    /// <summary>Raised once when the connection ends, with the reason if there was one.</summary>
    public event EventHandler<string>? Disconnected;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public Welcome? Welcome { get; private set; }

    /// <summary>
    /// Opens the socket and completes the handshake. The reply carries the whole
    /// room tree, so nothing else needs fetching before the UI can be drawn.
    /// </summary>
    public async Task<Welcome> ConnectAsync(
        string webSocketUrl,
        string clientUuid,
        string username,
        string password = "",
        CancellationToken cancellationToken = default)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("This client is already connected.");
        }

        _socket = new ClientWebSocket();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token), CancellationToken.None);

        var reply = await RequestAsync(
            MessageTypes.Hello,
            new Hello { ClientUuid = clientUuid, Username = username, Password = password },
            cancellationToken).ConfigureAwait(false);

        Welcome = Deserialize<Welcome>(reply)
                  ?? throw new TamizChatProtocolException("bad_request", "the welcome frame had no payload");
        return Welcome;
    }

    /// <summary>Sends a frame and waits for the reply carrying the same id.</summary>
    public async Task<Envelope> RequestAsync(
        string type,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var completion = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await SendEnvelopeAsync(type, id, payload, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Deserializes a reply's payload.</summary>
    public static T? Deserialize<T>(Envelope envelope) where T : class =>
        envelope.Data is null ? null : envelope.Data.Value.Deserialize<T>(JsonOptions);

    /// <summary>
    /// Sends a frame without waiting for anything. Used for the messages the
    /// protocol defines no reply for, such as typing and paint appends.
    /// </summary>
    public Task SendAsync(string type, object? payload = null, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(type, null, payload, cancellationToken);

    private async Task SendEnvelopeAsync(
        string type,
        string? id,
        object? payload,
        CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("The client is not connected.");
        }

        var envelope = new Dictionary<string, object?> { ["t"] = type };
        if (id is not null)
        {
            envelope["id"] = id;
        }

        if (payload is not null)
        {
            envelope["d"] = payload;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

        // A WebSocket has a single writer, so sends are serialised.
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var reason = "closed";
        var buffer = ArrayPool<byte>.Shared.Rent(MaxFrameBytes);

        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                var frame = await ReadFrameAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                Dispatch(frame);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled";
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // Nothing is ever coming back for these, so fail them rather than
            // leaving callers awaiting forever.
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IOException($"connection ended: {reason}"));
            }

            _pending.Clear();
            Disconnected?.Invoke(this, reason);
        }
    }

    private async Task<Envelope?> ReadFrameAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        WebSocketReceiveResult result;

        do
        {
            if (offset == buffer.Length)
            {
                throw new IOException("the server sent a frame larger than the protocol allows");
            }

            result = await _socket!
                .ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            offset += result.Count;
        }
        while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<Envelope>(
            Encoding.UTF8.GetString(buffer, 0, offset), JsonOptions);
    }

    private void Dispatch(Envelope envelope)
    {
        if (envelope.Id is not null && _pending.TryRemove(envelope.Id, out var completion))
        {
            if (envelope.Type == MessageTypes.Error)
            {
                var error = Deserialize<ErrorBody>(envelope) ?? new ErrorBody { Code = "internal_error" };
                completion.TrySetException(new TamizChatProtocolException(error.Code, error.Message));
            }
            else
            {
                completion.TrySetResult(envelope);
            }

            return;
        }

        ServerEvent?.Invoke(this, new ServerEventArgs(envelope.Type, envelope.Data));
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();

        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The socket is going away regardless.
            }
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already reported through Disconnected.
            }
        }

        _socket?.Dispose();
        _lifetime?.Dispose();
        _sendGate.Dispose();
        _socket = null;
    }
}

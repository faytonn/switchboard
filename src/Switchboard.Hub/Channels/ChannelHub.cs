using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Switchboard.Core.Data;
using Switchboard.Hub.Events;

namespace Switchboard.Hub.Channels;

/// <summary>
/// Keeps one WebSocket per session to that session's channel server, and delivers messages
/// over it.
///
/// Delivery is driven by the database, not by memory. To send, a caller saves the message and
/// "kicks" the recipient's connection. The connection's delivery loop then reads every
/// undelivered message for that session from Postgres and sends them in order. When the
/// channel server has pushed one into Claude Code, it acks, and we set delivered_at. So:
///  - a message to an offline session waits in the table and goes out on the next connect;
///  - many kicks at once collapse into one pass, and messages never overtake each other;
///  - if a connection drops before an ack, the message is sent again on reconnect
///    ("at least once"). A rare duplicate beats a lost message.
/// </summary>
public sealed class ChannelHub(MessageStore messages, HubEvents events, ILogger<ChannelHub> log)
{
    private const int MaxFrameBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _deliveryWaiters = new();

    public bool IsConnected(string sessionId) => _connections.ContainsKey(sessionId);

    /// <summary>Tells the session's delivery loop to look for undelivered messages.</summary>
    public void Kick(string sessionId)
    {
        if (_connections.TryGetValue(sessionId, out var conn))
        {
            conn.Kick();
        }
    }

    /// <summary>
    /// Kicks the recipient and waits up to <paramref name="timeout"/> for the ack.
    /// Returns false if it wasn't delivered in time (it stays queued).
    /// </summary>
    public async Task<bool> DeliverAndWaitAsync(string sessionId, long messageId, TimeSpan timeout, CancellationToken ct)
    {
        // Register the waiter before kicking, so an ack that comes back very fast can't be missed.
        var waiter = _deliveryWaiters.GetOrAdd(messageId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            Kick(sessionId);
            await waiter.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _deliveryWaiters.TryRemove(messageId, out _);
        }
    }

    /// <summary>
    /// Sends the owner's Allow/Deny for a permission prompt to the session's channel server.
    /// Returns false if the session isn't connected. No ack here: Claude Code doesn't confirm
    /// verdicts either, and silently drops one for a prompt already answered in the terminal.
    /// </summary>
    public async Task<bool> SendPermissionAsync(string sessionId, string requestId, string behavior, CancellationToken ct)
    {
        if (!_connections.TryGetValue(sessionId, out var conn))
        {
            return false;
        }

        try
        {
            await SendAsync(conn, new { type = "permission", requestId, behavior }, ct);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
    }

    public async Task<bool> WaitForConnectionAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!IsConnected(sessionId))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(250, ct);
        }

        return true;
    }

    /// <summary>Runs one channel server connection until it closes.</summary>
    public async Task RunAsync(string sessionId, WebSocket socket, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var conn = new Connection(sessionId, socket, cts);

        // A session has one channel server. If it reconnects (or a stale socket lingers), the
        // newest connection wins and the old one is closed.
        if (_connections.TryGetValue(sessionId, out var old))
        {
            old.Cancel();
        }

        _connections[sessionId] = conn;
        log.LogInformation("Channel connected: {Session}", sessionId);
        events.Publish("sessions", new { id = sessionId });

        var pump = Task.Run(() => DeliveryLoopAsync(conn, cts.Token));
        conn.Kick(); // deliver anything that queued up while the session was offline
        try
        {
            await ReceiveLoopAsync(conn, cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // Closed or replaced: normal.
        }
        finally
        {
            await cts.CancelAsync();
            _connections.TryRemove(new KeyValuePair<string, Connection>(sessionId, conn));
            await pump.ContinueWith(_ => { }, TaskScheduler.Default);
            log.LogInformation("Channel disconnected: {Session}", sessionId);
            events.Publish("sessions", new { id = sessionId });
        }
    }

    private async Task DeliveryLoopAsync(Connection conn, CancellationToken ct)
    {
        try
        {
            while (await conn.Kicks.Reader.WaitToReadAsync(ct))
            {
                conn.Kicks.Reader.TryRead(out _);
                while (true)
                {
                    var batch = await messages.UndeliveredAsync(conn.SessionId, conn.LastSentId, 50, ct);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    foreach (var m in batch)
                    {
                        await SendAsync(conn, new
                        {
                            type = "message",
                            id = m.Id,
                            from = m.From ?? "owner",
                            text = m.Body,
                            sentAt = m.CreatedAt,
                        }, ct);
                        // Remember what this connection already sent, so the next pass doesn't
                        // send it again while its ack is still on the way.
                        conn.LastSentId = m.Id;
                    }
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
        }
        catch (Exception e)
        {
            log.LogError(e, "Delivery loop for {Session} failed; closing the connection so it reconnects", conn.SessionId);
            conn.Cancel();
        }
    }

    private async Task ReceiveLoopAsync(Connection conn, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var frame = new MemoryStream();
        while (conn.Socket.State == WebSocketState.Open)
        {
            var result = await conn.Socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return;
            }

            // A WebSocket message can arrive in several pieces; collect until EndOfMessage.
            frame.Write(buffer, 0, result.Count);
            if (frame.Length > MaxFrameBytes)
            {
                await conn.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", CancellationToken.None);
                return;
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            await HandleFrameAsync(conn, frame.ToArray(), ct);
            frame.SetLength(0);
        }
    }

    private async Task HandleFrameAsync(Connection conn, byte[] data, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() == "ack" && root.GetProperty("id").TryGetInt64(out var id))
        {
            await messages.MarkDeliveredAsync(id, ct);
            events.Publish("delivered", new { id });
            if (_deliveryWaiters.TryGetValue(id, out var waiter))
            {
                waiter.TrySetResult();
            }
        }
    }

    private static async Task SendAsync(Connection conn, object frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        // A WebSocket allows one send at a time; overlapping sends throw.
        await conn.SendLock.WaitAsync(ct);
        try
        {
            await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            conn.SendLock.Release();
        }
    }

    private sealed class Connection(string sessionId, WebSocket socket, CancellationTokenSource cts)
    {
        public string SessionId { get; } = sessionId;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public long LastSentId { get; set; }

        // A channel with room for one item, dropping extra writes: any number of kicks while
        // the loop is busy become a single "look again".
        public Channel<bool> Kicks { get; } = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        public void Kick() => Kicks.Writer.TryWrite(true);

        public void Cancel()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

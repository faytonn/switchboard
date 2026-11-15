using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Switchboard.Hub.Events;

/// <summary>
/// Live updates for the web UI, sent as Server-Sent Events (SSE): one long HTTP response the
/// server keeps writing "event: …\ndata: …\n\n" blocks to. Simpler than a WebSocket here,
/// because updates only flow server → browser, and the browser's EventSource reconnects by
/// itself.
///
/// Events are hints, not the source of truth. After every (re)connect the page reloads what it
/// shows from the API, so an event missed while the phone was asleep costs nothing.
/// </summary>
public sealed class HubEvents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private const int MaxQueued = 500;

    private readonly ConcurrentDictionary<Guid, Channel<SseItem<string>>> _subscribers = new();

    /// <summary>Sends an event to every open page.</summary>
    public void Publish(string type, object data)
    {
        var item = new SseItem<string>(JsonSerializer.Serialize(data, Json), type);
        foreach (var (_, queue) in _subscribers)
        {
            // A page that stops reading (a sleeping phone, a dead connection) mustn't make us
            // queue events forever. When its queue is full we close its stream instead; when
            // it reconnects, it reloads everything anyway.
            if (!queue.Writer.TryWrite(item))
            {
                queue.Writer.TryComplete();
            }
        }
    }

    public async IAsyncEnumerable<SseItem<string>> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var queue = Channel.CreateBounded<SseItem<string>>(new BoundedChannelOptions(MaxQueued) { SingleReader = true });
        _subscribers[id] = queue;
        try
        {
            yield return new SseItem<string>("{}", "hello");
            while (!ct.IsCancellationRequested)
            {
                SseItem<string> item;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(PingInterval);
                    try
                    {
                        if (!await queue.Reader.WaitToReadAsync(wait.Token))
                        {
                            yield break; // closed by Publish: the page was too slow
                        }

                        queue.Reader.TryRead(out item);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Nothing for a while. Send a ping so proxies (like Tailscale's) and the
                        // phone's network don't close the connection as idle.
                        item = new SseItem<string>("{}", "ping");
                    }
                }

                yield return item;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}

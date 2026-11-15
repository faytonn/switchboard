using System.Collections.Concurrent;

namespace Switchboard.Hub.Channels;

/// <summary>
/// Caps how many messages one session may send to other sessions: 20 per 10 minutes.
///
/// Two agents can get stuck answering each other ("Thanks!" "You're welcome!" ...), and every
/// message is a paid model turn on both sides. A sliding window per sender stops that loop
/// quickly while leaving normal coordination (a few messages per task) untouched. Replies to
/// the owner are not counted: they can't loop.
/// </summary>
public sealed class SessionSendLimiter
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _sent = new();

    /// <summary>Records a send if under the limit; otherwise says how long until the next one fits.</summary>
    public bool TryAcquire(string sender, out TimeSpan retryAfter)
    {
        var queue = _sent.GetOrAdd(sender, _ => new Queue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= Limit)
            {
                retryAfter = queue.Peek() + Window - now;
                return false;
            }

            queue.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

using System.Collections.Concurrent;
using Switchboard.Core.Claude;
using Switchboard.Core.Tmux;
using Switchboard.Hub.Events;

namespace Switchboard.Hub;

/// <summary>
/// Every 2 seconds, reads each running session's screen and works out whether it is idle,
/// working, or stuck on an approval prompt. Changes go to the web UI as "activity" events.
///
/// Why poll the screen? Claude Code doesn't tell an MCP server when it starts or finishes a
/// turn, but its terminal does: the footer shows "esc to interrupt" while it works. Reading a
/// tmux pane is cheap (one short-lived process), so polling a handful of sessions costs little.
/// </summary>
public sealed class ActivityMonitor(TmuxClient tmux, HubEvents events, ILogger<ActivityMonitor> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, string> _activity = new();

    /// <summary>idle, working or approval; null if the session has no live window.</summary>
    public string? Get(string sessionId) => _activity.GetValueOrDefault(sessionId);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await PollAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A window can close between listing and reading it. Try again next tick.
                log.LogDebug(e, "Activity poll failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var live = new HashSet<string>();
        foreach (var window in (await tmux.ListAsync(ct)).Where(w => !w.Dead))
        {
            live.Add(window.Id);
            var activity = ClaudeScreen.Activity(await tmux.ReadAsync(window.Id, ct: ct, check: false));
            if (_activity.TryGetValue(window.Id, out var old) && old == activity)
            {
                continue;
            }

            _activity[window.Id] = activity;
            events.Publish("activity", new { id = window.Id, activity });
        }

        foreach (var gone in _activity.Keys.Where(id => !live.Contains(id)))
        {
            _activity.TryRemove(gone, out _);
        }
    }
}

using Switchboard.Core.Data;
using Switchboard.Core.Tmux;
using Switchboard.Hub.Auth;
using Switchboard.Hub.Events;

namespace Switchboard.Hub;

/// <summary>
/// Keeps the manager session available without anyone typing commands:
///  - starts it when the hub starts;
///  - restarts it when it goes down by itself (Claude Code exited or crashed), resuming its
///    last conversation with --continue;
///  - backs off between attempts (0s, 10s, 1min, 5min) and gives up after
///    <see cref="MaxFailures"/> failures in a row: the session is marked "crashed", so a crash
///    loop can't keep spending tokens. Only the owner restarts it from there;
///  - once a day, when the manager is idle, restarts it with a fresh conversation, so its
///    context doesn't grow forever. Facts that must survive that live in Postgres (sessions,
///    messages), which the manager can look up with its tools.
/// A manager the owner stopped on purpose (sb stop, the UI) stays stopped.
/// </summary>
public sealed class ManagerSupervisor : BackgroundService
{
    public const string ManagerId = "manager";
    private const int MaxFailures = 4;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    // Up this long = healthy again: earlier failures no longer count.
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(10);
    // The daily fresh start waits until the manager has been quiet this long.
    private static readonly TimeSpan IdleBeforeFresh = TimeSpan.FromMinutes(10);

    private readonly HubOptions _options;
    private readonly SessionService _sessions;
    private readonly SessionStore _store;
    private readonly TmuxClient _tmux;
    private readonly ActivityMonitor _activity;
    private readonly HubEvents _events;
    private readonly ILogger<ManagerSupervisor> _log;

    private bool _stoppedOnPurpose;
    private bool _starting;            // true while the supervisor itself is starting the manager
    private bool _checkedOnce;         // the first check after the hub starts
    private bool _pendingStart;        // the manager should be up and isn't yet
    private DateTimeOffset? _upSince;
    private int _failures;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public ManagerSupervisor(HubOptions options, SessionService sessions, SessionStore store, TmuxClient tmux,
        ActivityMonitor activity, HubEvents events, ILogger<ManagerSupervisor> log)
    {
        (_options, _sessions, _store, _tmux, _activity, _events, _log) = (options, sessions, store, tmux, activity, events, log);

        sessions.StoppedOnPurpose += id =>
        {
            if (id == ManagerId)
            {
                _stoppedOnPurpose = true;
                _pendingStart = false;
            }
        };
        // Someone else (the owner) started or restarted the manager: that clears a crash or a
        // deliberate stop, and failures start counting from zero again.
        sessions.Started += id =>
        {
            if (id == ManagerId && !_starting)
            {
                _stoppedOnPurpose = false;
                _pendingStart = false;
                _failures = 0;
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.ManagerAutostart)
        {
            _log.LogInformation("Manager autostart is off (SWITCHBOARD_MANAGER_AUTOSTART=false)");
            return;
        }

        Directory.CreateDirectory(_options.ManagerDir);
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await CheckAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Manager supervisor check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (_stoppedOnPurpose)
        {
            return;
        }

        var row = await _store.GetAsync(ManagerId, ct);
        if (row?.Status == "crashed")
        {
            return; // gave up earlier; waiting for the owner
        }

        if (row?.Status == "starting")
        {
            return; // a start is in progress
        }

        var window = await _tmux.FindAsync(ManagerId, ct);
        var up = window is { Dead: false } && row?.Status == "running";
        var now = DateTimeOffset.UtcNow;
        var firstCheck = !_checkedOnce;
        _checkedOnce = true;

        if (up)
        {
            _pendingStart = false;
            _upSince ??= now;
            if (now - _upSince > HealthyAfter)
            {
                _failures = 0;
            }

            await MaybeStartFreshAsync(row!, window!, ct);
            return;
        }

        _upSince = null;
        // How to tell a crash from a deliberate stop: a stop through the hub (the owner, or a
        // restart in progress) marks the row "stopped" first. A Claude Code that died by itself
        // leaves the row saying "running" with no live window.
        var died = row?.Status == "running";
        if (died)
        {
            await _sessions.ShutDownAsync(ManagerId, TimeSpan.Zero, ct); // tidy up, so this is seen once
            if (!firstCheck)
            {
                _failures++;
                _nextAttempt = now + Delay(_failures - 1);
                _log.LogWarning("Manager went down by itself ({Failures} in a row); restarting at {When:t}",
                    _failures, _nextAttempt.ToLocalTime());
            }
        }

        // Start it when the hub has just started, or after it died. A manager that was stopped
        // through the hub stays down.
        if (firstCheck || died)
        {
            _pendingStart = true;
        }

        if (!_pendingStart)
        {
            return;
        }

        if (_failures >= MaxFailures)
        {
            await GiveUpAsync(ct);
            return;
        }

        if (now < _nextAttempt)
        {
            return;
        }

        // Resume the last conversation unless it is old enough for a fresh start.
        var resume = row?.ConversationStartedAt is { } started && now - started < _options.ManagerFreshAfter;
        await StartAsync(row, resume, ct);
    }

    private async Task StartAsync(SessionRow? row, bool resume, CancellationToken ct)
    {
        var request = (row is null
                ? new StartSessionRequest(ManagerId, _options.ManagerDir, Manager: true)
                : SessionService.ToRequest(row, resume)) with
        {
            // Always the persistent folder, even if the manager was once started somewhere else.
            ProjectDir = _options.ManagerDir,
            Manager = true,
            Icon = row?.Icon is null ? "waypoints" : null,
        };

        _starting = true;
        try
        {
            await _sessions.ShutDownAsync(ManagerId, TimeSpan.Zero, ct); // clear a dead window or stale row
            var result = await _sessions.StartAsync(Caller.Owner, request, ct);
            _log.LogInformation("Supervisor started the manager ({How})", result.Resumed ? "resumed its conversation" : "fresh conversation");
            _pendingStart = false;
            _upSince = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _failures++;
            _nextAttempt = DateTimeOffset.UtcNow + Delay(_failures);
            _log.LogWarning(e, "Couldn't start the manager ({Failures} failures in a row)", _failures);
            if (_failures >= MaxFailures)
            {
                await GiveUpAsync(ct);
            }
        }
        finally
        {
            _starting = false;
        }
    }

    // Once a day, a new conversation: but only when the manager is idle, never mid-task.
    private async Task MaybeStartFreshAsync(SessionRow row, TmuxWindow window, CancellationToken ct)
    {
        var age = DateTimeOffset.UtcNow - (row.ConversationStartedAt ?? row.StartedAt);
        var quiet = DateTimeOffset.UtcNow - window.LastActivity;
        if (age < _options.ManagerFreshAfter || quiet < IdleBeforeFresh || _activity.Get(ManagerId) != "idle")
        {
            return;
        }

        _log.LogInformation("Manager conversation is {Hours:0}h old and the manager is idle: starting it fresh", age.TotalHours);
        _pendingStart = true; // a planned restart, not a failure
        await StartAsync(row, resume: false, ct);
    }

    // Wait before restart attempt n (0-based); the last configured delay repeats.
    private TimeSpan Delay(int n) => _options.ManagerBackoff[Math.Clamp(n, 0, _options.ManagerBackoff.Count - 1)];

    private async Task GiveUpAsync(CancellationToken ct)
    {
        _log.LogError("Manager failed {Failures} times in a row. Marked crashed; restart it from the UI or with ./sb restart manager", _failures);
        await _sessions.ShutDownAsync(ManagerId, TimeSpan.Zero, ct);
        await _store.MarkCrashedAsync(ManagerId, ct);
        _events.Publish("sessions", new { id = ManagerId });
        _failures = 0;
        _pendingStart = false;
    }
}

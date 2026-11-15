using System.Collections.Concurrent;
using System.Text.Json;
using Switchboard.Core;
using Switchboard.Core.Claude;
using Switchboard.Core.Data;
using Switchboard.Core.Tmux;
using Switchboard.Hub.Auth;
using Switchboard.Hub.Channels;
using Switchboard.Hub.Events;
using Switchboard.Hub.Icons;

namespace Switchboard.Hub;

/// <summary>
/// Everything the API does to sessions. Each step that changes a session keeps tmux and
/// Postgres in agreement, and checks what the caller is allowed to do.
/// </summary>
public sealed class SessionService(
    HubOptions options,
    TmuxClient tmux,
    SessionStore sessions,
    MessageStore messages,
    ChannelHub channels,
    HubEvents events,
    PermissionService permissions,
    ActivityMonitor activity,
    IconCatalog icons,
    IconGuesser iconGuesser,
    ILogger<SessionService> log)
{
    // MCP server names inside a session. Claude Code names their tools after them, e.g.
    // mcp__switchboard-manager__start_session and mcp__switchboard-channel__reply.
    public const string ManagerServerName = "switchboard-manager";
    public const string ChannelServerName = "switchboard-channel";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ChannelConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(5);

    // One lock per session id. Without it, two requests could start the same id at once:
    // both see "no window yet", both create one, and tmux ends up with two windows of that name.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<StartSessionResponse> StartAsync(Caller caller, StartSessionRequest req, CancellationToken ct)
    {
        RequireControl(caller);
        SessionId.Validate(req.Id);
        if (req.Manager && !caller.IsOwner)
        {
            throw HubException.Forbidden("Only the owner can start a manager session.");
        }

        // Your connectors reach your Gmail, Notion and GitHub, so only you can give a session
        // access to them; the manager can't hand that out to the workers it starts.
        if (req.Connectors && !caller.IsOwner)
        {
            throw HubException.Forbidden("Only the owner can start a session with the claude.ai connectors.");
        }

        var role = req.Manager ? SessionRoles.Manager : SessionRoles.Worker;
        // Defaults: the manager mostly routes messages, so a faster model does; workers write
        // code, so they get the strongest one.
        var launch = new ClaudeLaunch(
            PermissionMode: req.PermissionMode ?? ClaudeCommand.DefaultPermissionMode,
            Model: req.Model ?? (req.Manager ? "sonnet" : "opus"),
            Effort: req.Effort ?? "medium",
            Connectors: req.Connectors,
            Continue: req.Resume);
        var projectDir = WslPath.ToLinux(req.ProjectDir);
        ValidateAppearance(req.Icon, req.Color);
        // No icon given: guess one from the folder. It is only used if the session has none
        // stored yet, so restarting never replaces an icon the owner picked.
        var guessedIcon = req.Icon is null && Directory.Exists(projectDir) ? iconGuesser.Guess(projectDir) : null;

        using var _ = await LockAsync(req.Id, ct);
        if (await tmux.FindAsync(req.Id, ct) is not null)
        {
            throw HubException.Conflict($"Session '{req.Id}' is already running.");
        }

        StartSessionResponse response;
        try
        {
            response = await StartLockedAsync(req, role, launch, projectDir, guessedIcon, ct);
        }
        catch (NoConversationException)
        {
            // --continue found nothing to resume (a new folder, or Claude's history was cleared).
            // That's not a crash: start a fresh conversation instead.
            log.LogInformation("No conversation to resume for {Id}; starting fresh", req.Id);
            response = await StartLockedAsync(req, role, launch with { Continue = false }, projectDir, guessedIcon, ct);
        }

        log.LogInformation("{Caller} started {Role} session {Id} in {Dir} ({Mode}, {Model}, effort {Effort}, connectors {Connectors}, resumed {Resumed})",
            caller.Describe(), role, req.Id, projectDir, launch.PermissionMode, launch.Model, launch.Effort, launch.Connectors,
            response.Resumed);
        events.Publish("sessions", new { id = req.Id });
        Started?.Invoke(req.Id);
        return response;
    }

    /// <summary>Raised after a session started (by anyone). The manager supervisor listens.</summary>
    public event Action<string>? Started;

    /// <summary>Raised when the owner or the manager stops a session on purpose.</summary>
    public event Action<string>? StoppedOnPurpose;

    private sealed class NoConversationException() : Exception("No conversation to continue.");

    // The actual start, with the session's lock held by the caller.
    private async Task<StartSessionResponse> StartLockedAsync(
        StartSessionRequest req, string role, ClaudeLaunch launch, string projectDir, string? guessedIcon, CancellationToken ct)
    {
        // A fresh token per start. Claude Code passes it to the session's MCP servers through
        // their config file. The old run's token stops working because its hash is replaced.
        var token = Tokens.New();
        var configPath = WriteMcpConfig(req.Id, token, req.Manager);

        var channelList = new[] { $"server:{ChannelServerName}" };
        var allowedTools = new List<string> { $"mcp__{ChannelServerName}" };
        if (req.Manager)
        {
            allowedTools.Add($"mcp__{ManagerServerName}");
        }

        var command = ClaudeCommand.Build(launch,
        [
            "--mcp-config", configPath,
            // Custom channels aren't on Anthropic's allowlist during the research preview, so
            // they need this flag, which shows a warning we answer below.
            "--dangerously-load-development-channels", .. channelList,
            // Pre-approve only our own servers' tools (a bare server name matches all its tools),
            // so replies and manager calls don't stop at a permission prompt. Everything else
            // still follows the permission mode. This flag takes several values, so it goes last.
            "--allowedTools", .. allowedTools,
        ]);

        // Order matters: the database row first (status 'starting'), then tmux. If anything
        // fails, we close the window and mark the row stopped.
        await sessions.MarkStartingAsync(req.Id, projectDir, tmux.DisplayTarget(req.Id), role, Tokens.Hash(token),
            icon: req.Icon, guessedIcon: guessedIcon, color: req.Color,
            model: launch.Model, effort: launch.Effort, connectors: launch.Connectors,
            permissionMode: launch.PermissionMode, resumed: launch.Continue, ct: ct);
        // Prompts left over from a previous run of this id belong to a Claude Code that's gone.
        await permissions.ExpireForSessionAsync(req.Id, ct);
        IReadOnlyList<ClaudeDialog> answered;
        try
        {
            await tmux.StartAsync(req.Id, projectDir, command, ct);
            answered = await new DialogConfirmer(tmux).ConfirmAllAsync(
                req.Id, [ClaudeDialog.FolderTrust, ClaudeDialog.DevChannels(channelList)], StartupTimeout, ct);

            if (!await channels.WaitForConnectionAsync(req.Id, ChannelConnectTimeout, ct))
            {
                throw new HubException(StatusCodes.Status502BadGateway,
                    $"Claude Code started, but its channel server didn't connect within {ChannelConnectTimeout.TotalSeconds:0}s. " +
                    $"Check `/mcp` in the session, or ~/.claude/debug with --debug.");
            }

            await sessions.MarkRunningAsync(req.Id, ct);
        }
        catch (Exception e)
        {
            // Read the screen before cleaning up: cleanup closes the window.
            var noConversation = e is TmuxException && launch.Continue && await ExitedForLackOfConversationAsync(req.Id);
            await CleanUpFailedStartAsync(req.Id);
            if (noConversation)
            {
                throw new NoConversationException();
            }

            throw;
        }

        return new StartSessionResponse(await GetInfoAsync(req.Id, ct), answered.Select(d => d.Name).ToList(), launch.Continue);
    }

    // Claude Code exited at once. Was it "No conversation found to continue"? The window is kept
    // after exit (remain-on-exit), so its last words are still readable.
    private async Task<bool> ExitedForLackOfConversationAsync(string id)
    {
        try
        {
            return (await tmux.ReadAsync(id, check: false)).Contains(ClaudeCommand.NoConversationToContinue, StringComparison.Ordinal);
        }
        catch (TmuxException)
        {
            return false;
        }
    }

    /// <summary>
    /// Restarts a session with the settings it last ran with. By default Claude Code resumes its
    /// previous conversation (--continue); <paramref name="fresh"/> starts a new one.
    /// </summary>
    public async Task<StartSessionResponse> RestartAsync(Caller caller, string id, bool fresh, CancellationToken ct)
    {
        if (!caller.IsOwner)
        {
            throw HubException.Forbidden("Only the owner restarts sessions.");
        }

        SessionId.Validate(id);
        var row = await sessions.GetAsync(id, ct) ?? throw HubException.NotFound($"No session named '{id}'.");
        var request = ToRequest(row, resume: !fresh);
        if (id == ManagerSupervisor.ManagerId && options.ManagerAutostart)
        {
            // The supervised manager always lives in its permanent folder.
            request = request with { ProjectDir = options.ManagerDir };
            Directory.CreateDirectory(options.ManagerDir);
        }

        await ShutDownAsync(id, TimeSpan.FromSeconds(5), ct);
        return await StartAsync(caller, request, ct);
    }

    /// <summary>A start request that repeats how a session last ran.</summary>
    public static StartSessionRequest ToRequest(SessionRow row, bool resume) => new(
        row.Id,
        row.ProjectDir,
        PermissionMode: row.PermissionMode,
        Manager: row.Role == SessionRoles.Manager,
        Model: row.Model,
        Effort: row.Effort,
        Connectors: row.Connectors,
        Resume: resume);

    /// <summary>
    /// Closes the session's window (if any) and marks it stopped, without the permission checks
    /// or the "stopped on purpose" signal. Used by restarts and the supervisor.
    /// </summary>
    public async Task ShutDownAsync(string id, TimeSpan grace, CancellationToken ct)
    {
        using var _ = await LockAsync(id, ct);
        if (await tmux.FindAsync(id, ct) is { } window)
        {
            await tmux.StopAsync(id, window.Dead ? TimeSpan.Zero : grace, ct);
        }

        await sessions.MarkStoppedAsync(id, ct);
        await permissions.ExpireForSessionAsync(id, ct);
        DeleteConfig(id);
    }

    /// <summary>Owner or manager sends a message into a session through its channel.</summary>
    public async Task<SendMessageResponse> SendAsync(Caller caller, string id, SendMessageRequest req, CancellationToken ct)
    {
        RequireControl(caller);
        RequireNotSelf(caller, id, "send a message to");
        RequireText(req.Text);
        await RequireRunningAsync(id, ct);

        // Save first (not delivered). The channel connection's delivery loop picks it up from
        // the database. If the channel is offline it simply waits there.
        var message = await messages.AddAsync(from: caller.SessionId, to: id, req.Text, ct);
        events.Publish("message", message);
        var delivered = await channels.DeliverAndWaitAsync(id, message.Id, DeliveryTimeout, ct);
        return new SendMessageResponse(message.Id, delivered);
    }

    /// <summary>A session answers through its channel server's reply tool.</summary>
    public async Task<SendMessageResponse> ReplyAsync(Caller caller, ReplyRequest req, CancellationToken ct)
    {
        var from = caller.SessionId ?? throw HubException.Forbidden("Only sessions reply; the owner uses /api/sessions/{id}/messages.");
        RequireText(req.Text);

        if (req.To == "owner")
        {
            // The owner reads messages from the database, so storing it is delivering it.
            var toOwner = await messages.AddAsync(from, to: null, req.Text, deliveredNow: true, ct);
            events.Publish("message", toOwner);
            return new SendMessageResponse(toOwner.Id, Delivered: true);
        }

        SessionId.Validate(req.To);
        if (req.To == from)
        {
            throw HubException.BadRequest("A session can't reply to itself.");
        }

        // Replies may only go back to someone who wrote to you. Free session-to-session
        // messaging, with its own rules, comes in phase 6.
        if (!await messages.HasMessagedAsync(from: req.To, to: from, ct))
        {
            throw HubException.Forbidden($"'{req.To}' hasn't sent you a message, so you can't reply to it. Reply to \"owner\" instead.");
        }

        var message = await messages.AddAsync(from, req.To, req.Text, ct);
        events.Publish("message", message);
        var delivered = await channels.DeliverAndWaitAsync(req.To, message.Id, DeliveryTimeout, ct);
        return new SendMessageResponse(message.Id, delivered);
    }

    public async Task<ScreenResponse> ReadScreenAsync(Caller caller, string id, int scrollback, CancellationToken ct)
    {
        RequireControl(caller);
        SessionId.Validate(id);
        if (await tmux.FindAsync(id, ct) is null)
        {
            throw HubException.NotFound($"No tmux window for session '{id}'.");
        }

        return new ScreenResponse(id, await tmux.ReadAsync(id, Math.Clamp(scrollback, 0, 2000), ct));
    }

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(Caller caller, CancellationToken ct)
    {
        RequireControl(caller);
        var windows = (await tmux.ListAsync(ct)).ToDictionary(w => w.Id);
        var latest = await messages.LatestPerSessionAsync(ct);
        return (await sessions.ListAsync(ct))
            .Select(row => ToInfo(row, windows.GetValueOrDefault(row.Id)) with
            {
                LastMessage = latest.TryGetValue(row.Id, out var m)
                    ? new MessagePreview(m.From, m.To, m.Body.Length > 160 ? m.Body[..160] + "…" : m.Body, m.CreatedAt)
                    : null,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<Message>> HistoryAsync(Caller caller, string id, int limit, CancellationToken ct)
    {
        RequireControl(caller);
        SessionId.Validate(id);
        return await messages.HistoryAsync(id, Math.Clamp(limit, 1, 500), ct);
    }

    /// <summary>The owner changes a session's tile in the UI picker.</summary>
    public async Task<SessionInfo> SetAppearanceAsync(Caller caller, string id, AppearanceRequest req, CancellationToken ct)
    {
        if (!caller.IsOwner)
        {
            throw HubException.Forbidden("Only the owner changes session icons and colours.");
        }

        SessionId.Validate(id);
        ValidateAppearance(req.Icon, req.Color);
        if (!await sessions.SetAppearanceAsync(id, req.Icon, req.Color, ct))
        {
            throw HubException.NotFound($"No session named '{id}'.");
        }

        events.Publish("sessions", new { id });
        return await GetInfoAsync(id, ct);
    }

    private void ValidateAppearance(string? icon, short? color)
    {
        if (icon is not null && !icons.Contains(icon))
        {
            throw HubException.BadRequest($"Unknown icon '{icon}'. GET /api/icons lists the available ones.");
        }

        if (color is < 0 or > 7)
        {
            throw HubException.BadRequest("Color must be 0-7.");
        }
    }

    public async Task<IReadOnlyList<Message>> SearchAsync(Caller caller, string query, string? sessionId, int limit, CancellationToken ct)
    {
        RequireControl(caller);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw HubException.BadRequest("Give some words to search for.");
        }

        if (sessionId is not null)
        {
            SessionId.Validate(sessionId);
        }

        return await messages.SearchAsync(query, sessionId, Math.Clamp(limit, 1, 100), ct);
    }

    public async Task StopAsync(Caller caller, string id, StopSessionRequest req, CancellationToken ct)
    {
        RequireControl(caller);
        RequireNotSelf(caller, id, "stop");
        SessionId.Validate(id);

        using var _ = await LockAsync(id, ct);
        var row = await sessions.GetAsync(id, ct) ?? throw HubException.NotFound($"No session named '{id}'.");
        if (row.Role == SessionRoles.Manager && !caller.IsOwner)
        {
            throw HubException.Forbidden("Only the owner can stop a manager session.");
        }

        if (await tmux.FindAsync(id, ct) is not null)
        {
            await tmux.StopAsync(id, req.Force ? TimeSpan.Zero : TimeSpan.FromSeconds(5), ct);
        }

        await sessions.MarkStoppedAsync(id, ct);
        await permissions.ExpireForSessionAsync(id, ct);
        DeleteConfig(id);
        log.LogInformation("{Caller} stopped session {Id}", caller.Describe(), id);
        events.Publish("sessions", new { id });
        StoppedOnPurpose?.Invoke(id);
    }

    private async Task<SessionInfo> GetInfoAsync(string id, CancellationToken ct)
    {
        var row = await sessions.GetAsync(id, ct) ?? throw HubException.NotFound($"No session named '{id}'.");
        return ToInfo(row, await tmux.FindAsync(id, ct));
    }

    private SessionInfo ToInfo(SessionRow row, TmuxWindow? window) => new(
        row.Id,
        row.Role,
        row.Status,
        Window: window is null ? "missing" : window.Dead ? "exited" : "running",
        ExitStatus: window?.Dead == true ? window.ExitStatus : null,
        Channel: channels.IsConnected(row.Id),
        row.ProjectDir,
        row.StartedAt,
        IdleSeconds: window is null ? null : (int)(DateTimeOffset.UtcNow - window.LastActivity).TotalSeconds,
        Activity: window is { Dead: false } && row.Status != "stopped" ? activity.Get(row.Id) : null,
        Icon: row.Icon,
        Color: row.Color,
        Model: row.Model,
        Effort: row.Effort,
        Connectors: row.Connectors);

    private async Task RequireRunningAsync(string id, CancellationToken ct)
    {
        SessionId.Validate(id);
        var row = await sessions.GetAsync(id, ct);
        var window = await tmux.FindAsync(id, ct);
        if (row is null || row.Status is "stopped" or "crashed" || window is null)
        {
            throw HubException.NotFound($"Session '{id}' is not running.");
        }

        if (window.Dead)
        {
            throw HubException.Conflict($"Claude Code in '{id}' has exited (status {window.ExitStatus}). Read its screen to see why.");
        }
    }

    private static void RequireText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw HubException.BadRequest("Message text is empty.");
        }
    }

    private static void RequireControl(Caller caller)
    {
        if (!caller.CanControlSessions)
        {
            throw HubException.Forbidden("Only the owner and the manager can control sessions.");
        }
    }

    // A session closing its own tmux window would cut off the very tool call that asked for it.
    private static void RequireNotSelf(Caller caller, string id, string action)
    {
        if (caller.SessionId == id)
        {
            throw HubException.BadRequest($"A session can't {action} itself.");
        }
    }

    /// <summary>
    /// Writes ~/.switchboard/sessions/&lt;id&gt;.mcp.json for `claude --mcp-config`: the channel
    /// server for every session, plus the manager server for managers. It holds the session's
    /// token, so only the owner may read it (mode 600). Passing the JSON on the command line
    /// instead would show the token to anyone running `ps`.
    /// </summary>
    private string WriteMcpConfig(string id, string token, bool manager)
    {
        var env = new Dictionary<string, string>
        {
            ["SWITCHBOARD_HUB_URL"] = options.Url,
            ["SWITCHBOARD_TOKEN"] = token,
            ["SWITCHBOARD_SESSION_ID"] = id,
        };

        var servers = new Dictionary<string, object> { [ChannelServerName] = DotnetServer(options.ChannelDll, env) };
        if (manager)
        {
            servers[ManagerServerName] = DotnetServer(options.ManagerDll, env);
        }

        var path = ConfigPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Create the file with mode 600 from the start. Writing it first and chmod-ing after
        // would leave a moment where it has the default mode (644: readable by every user).
        // UnixCreateMode only applies when the file is new, so delete any old one first.
        File.Delete(path);
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(path, fileOptions))
        {
            writer.Write(JsonSerializer.Serialize(new { mcpServers = servers }, new JsonSerializerOptions { WriteIndented = true }));
        }

        return path;
    }

    private static object DotnetServer(string dll, Dictionary<string, string> env)
    {
        if (!File.Exists(dll))
        {
            throw new HubException(StatusCodes.Status500InternalServerError,
                $"MCP server not built: {dll}. Start the hub with scripts/run-hub.sh, which builds it.");
        }

        // A login shell puts ~/.dotnet on PATH, as for tmux windows.
        return new { command = "bash", args = new[] { "-lc", "exec dotnet \"$0\"", dll }, env };
    }

    // File.Delete ignores a missing file but throws if the folder is missing.
    private void DeleteConfig(string id)
    {
        var path = ConfigPath(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string ConfigPath(string id) => Path.Combine(options.StateDir, "sessions", $"{id}.mcp.json");

    private async Task CleanUpFailedStartAsync(string id)
    {
        try
        {
            if (await tmux.FindAsync(id) is not null)
            {
                await tmux.StopAsync(id, TimeSpan.Zero);
            }

            await sessions.MarkStoppedAsync(id);
            DeleteConfig(id);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Cleanup after failed start of {Id} also failed", id);
        }
    }

    private async Task<IDisposable> LockAsync(string id, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

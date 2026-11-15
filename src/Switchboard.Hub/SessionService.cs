using System.Collections.Concurrent;
using System.Text.Json;
using Switchboard.Core;
using Switchboard.Core.Claude;
using Switchboard.Core.Data;
using Switchboard.Core.Tmux;
using Switchboard.Hub.Auth;

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
    ILogger<SessionService> log)
{
    // The MCP server name inside a manager session. Claude Code names its tools
    // mcp__switchboard-manager__start_session and so on.
    public const string ManagerServerName = "switchboard-manager";

    private static readonly TimeSpan TrustTimeout = TimeSpan.FromSeconds(30);

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

        var mode = req.PermissionMode ?? ClaudeCommand.DefaultPermissionMode;
        var projectDir = WslPath.ToLinux(req.ProjectDir);
        var role = req.Manager ? SessionRoles.Manager : SessionRoles.Worker;

        using var _ = await LockAsync(req.Id, ct);
        if (await tmux.FindAsync(req.Id, ct) is not null)
        {
            throw HubException.Conflict($"Session '{req.Id}' is already running.");
        }

        // A fresh token per start. Claude Code passes it to the session's MCP servers through
        // their config file. The old run's token stops working because its hash is replaced.
        var token = Tokens.New();
        var extraArgs = new List<string>();
        if (req.Manager)
        {
            var configPath = WriteManagerConfig(req.Id, token);
            // --allowedTools pre-approves this one server's tools (a bare server name matches
            // all its tools), so the manager doesn't stop at a permission prompt for each call.
            // Everything else still follows the permission mode.
            extraArgs.AddRange(["--mcp-config", configPath, "--allowedTools", $"mcp__{ManagerServerName}"]);
        }

        var command = ClaudeCommand.Build(mode, extraArgs);

        // Order matters: the database row first (status 'starting'), then tmux. If tmux fails,
        // we mark the row stopped. The other order could leave a running session that the
        // database knows nothing about.
        await sessions.MarkStartingAsync(req.Id, projectDir, tmux.DisplayTarget(req.Id), role, Tokens.Hash(token), ct: ct);
        string trust;
        try
        {
            await tmux.StartAsync(req.Id, projectDir, command, ct);
            var result = await new DialogConfirmer(tmux).ConfirmAsync(req.Id, ClaudeDialog.FolderTrust, TrustTimeout, ct);
            trust = result == ConfirmResult.Confirmed ? "confirmed" : "not-shown";
            await sessions.MarkRunningAsync(req.Id, ct);
        }
        catch
        {
            await CleanUpFailedStartAsync(req.Id);
            throw;
        }

        log.LogInformation("{Caller} started {Role} session {Id} in {Dir} ({Mode})", caller.Describe(), role, req.Id, projectDir, mode);
        return new StartSessionResponse(await GetInfoAsync(req.Id, ct), trust);
    }

    public async Task<SendMessageResponse> SendAsync(Caller caller, string id, SendMessageRequest req, CancellationToken ct)
    {
        RequireControl(caller);
        RequireNotSelf(caller, id, "send a message to");
        if (string.IsNullOrWhiteSpace(req.Text))
        {
            throw HubException.BadRequest("Message text is empty.");
        }

        await RequireRunningAsync(id, ct);

        // Save first (not delivered), then type it into tmux, then mark it delivered.
        var message = await messages.AddAsync(from: caller.SessionId, to: id, req.Text, ct);
        await tmux.SendTextAsync(id, req.Text, ct: ct);
        await messages.MarkDeliveredAsync(message.Id, ct);
        return new SendMessageResponse(message.Id, DateTimeOffset.UtcNow);
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
        return (await sessions.ListAsync(ct)).Select(row => ToInfo(row, windows.GetValueOrDefault(row.Id))).ToList();
    }

    public async Task<IReadOnlyList<Message>> HistoryAsync(Caller caller, string id, int limit, CancellationToken ct)
    {
        RequireControl(caller);
        SessionId.Validate(id);
        return await messages.HistoryAsync(id, Math.Clamp(limit, 1, 500), ct);
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
        DeleteConfig(id);
        log.LogInformation("{Caller} stopped session {Id}", caller.Describe(), id);
    }

    private async Task<SessionInfo> GetInfoAsync(string id, CancellationToken ct)
    {
        var row = await sessions.GetAsync(id, ct) ?? throw HubException.NotFound($"No session named '{id}'.");
        return ToInfo(row, await tmux.FindAsync(id, ct));
    }

    private static SessionInfo ToInfo(SessionRow row, TmuxWindow? window) => new(
        row.Id,
        row.Role,
        row.Status,
        Window: window is null ? "missing" : window.Dead ? "exited" : "running",
        ExitStatus: window?.Dead == true ? window.ExitStatus : null,
        row.ProjectDir,
        row.StartedAt,
        IdleSeconds: window is null ? null : (int)(DateTimeOffset.UtcNow - window.LastActivity).TotalSeconds);

    private async Task RequireRunningAsync(string id, CancellationToken ct)
    {
        SessionId.Validate(id);
        var row = await sessions.GetAsync(id, ct);
        var window = await tmux.FindAsync(id, ct);
        if (row is null || row.Status == "stopped" || window is null)
        {
            throw HubException.NotFound($"Session '{id}' is not running.");
        }

        if (window.Dead)
        {
            throw HubException.Conflict($"Claude Code in '{id}' has exited (status {window.ExitStatus}). Read its screen to see why.");
        }
    }

    private static void RequireControl(Caller caller)
    {
        if (!caller.CanControlSessions)
        {
            throw HubException.Forbidden("Only the owner and the manager can control sessions.");
        }
    }

    // A session typing into its own tmux window, or closing it, would cut off the very tool
    // call that asked for it.
    private static void RequireNotSelf(Caller caller, string id, string action)
    {
        if (caller.SessionId == id)
        {
            throw HubException.BadRequest($"A session can't {action} itself.");
        }
    }

    /// <summary>
    /// Writes ~/.switchboard/sessions/&lt;id&gt;.mcp.json for `claude --mcp-config`. It holds the
    /// session's token, so only the owner may read it (mode 600). Passing the JSON on the
    /// command line instead would show the token to anyone running `ps`.
    /// </summary>
    private string WriteManagerConfig(string id, string token)
    {
        if (!File.Exists(options.ManagerDll))
        {
            throw new HubException(StatusCodes.Status500InternalServerError,
                $"Manager MCP server not built: {options.ManagerDll}. Start the hub with scripts/run-hub.sh, which builds it.");
        }

        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ManagerServerName] = new
                {
                    // A login shell puts ~/.dotnet on PATH, as for tmux windows.
                    command = "bash",
                    args = new[] { "-lc", "exec dotnet \"$0\"", options.ManagerDll },
                    env = new Dictionary<string, string>
                    {
                        ["SWITCHBOARD_HUB_URL"] = options.Url,
                        ["SWITCHBOARD_TOKEN"] = token,
                        ["SWITCHBOARD_SESSION_ID"] = id,
                    },
                },
            },
        };

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
            writer.Write(JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }

        return path;
    }

    // File.Delete ignores a missing file but throws if the folder is missing, which it is
    // until the first manager session starts.
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

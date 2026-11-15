using System.Globalization;

namespace Switchboard.Core.Tmux;

/// <summary>
/// Controls Claude Code sessions through tmux. All sessions are windows inside one tmux
/// session (default name "switchboard"), so `tmux attach -t switchboard` shows them all.
/// </summary>
public sealed class TmuxClient(string sessionName)
{
    // A plain shell window that keeps the tmux session alive when every Claude window is gone.
    private const string HomeWindow = "_home";

    // Pause between pasting text and pressing Enter. Without it, Claude Code can receive
    // Enter before it has finished handling the paste, and the message is not submitted.
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(300);

    public string SessionName { get; } = sessionName;

    public static TmuxClient FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("SWITCHBOARD_TMUX_SESSION") ?? "switchboard");

    /// <summary>
    /// Opens a new window named <paramref name="id"/> in <paramref name="projectDir"/> and runs
    /// <paramref name="command"/> in it (normally ["claude", ...flags]).
    /// </summary>
    public async Task StartAsync(string id, string projectDir, IReadOnlyList<string> command, CancellationToken ct = default)
    {
        SessionId.Validate(id);
        if (command.Count == 0)
        {
            throw new ArgumentException("Command must not be empty.", nameof(command));
        }

        if (!Directory.Exists(projectDir))
        {
            throw new TmuxException($"Project folder not found: {projectDir}");
        }

        await EnsureSessionAsync(ct);
        if (await FindAsync(id, ct) is not null)
        {
            throw new TmuxException($"Session '{id}' already exists. Stop it first or pick another id.");
        }

        // `bash -lc 'exec "$0" "$@"' claude --flag ...` runs the command through a login shell,
        // so ~/.profile sets PATH (that is where ~/.local/bin/claude and ~/.dotnet come from).
        // The command and its arguments go in as separate arguments ($0, $1, ...), never as
        // one string, so nothing in them is parsed by the shell. `exec` replaces bash with
        // the command, so the window's process is claude itself.
        var args = new List<string>
        {
            "new-window", "-d",
            "-t", $"={SessionName}:",
            "-n", id,
            "-c", projectDir,
            "--", "bash", "-lc", "exec \"$0\" \"$@\"",
        };
        args.AddRange(command);
        await RunAsync(args, ct: ct);

        // Keep the window after the program exits, so we can still read why it exited.
        await SetWindowOptionAsync(id, "remain-on-exit", "on", ct);
        // Claude Code sets the terminal title. Without these, tmux could rename the window,
        // and we would lose the id that we use to find it.
        await SetWindowOptionAsync(id, "allow-rename", "off", ct);
        await SetWindowOptionAsync(id, "automatic-rename", "off", ct);
    }

    /// <summary>
    /// Types <paramref name="text"/> into the session and presses Enter (unless
    /// <paramref name="submit"/> is false). Multi-line text arrives as one message.
    /// </summary>
    public async Task SendTextAsync(string id, string text, bool submit = true, CancellationToken ct = default)
    {
        await RequireAsync(id, ct);

        // `send-keys` would turn each "\n" into an Enter press and submit the message too early.
        // Instead we load the text into a tmux paste buffer and paste it with -p (bracketed
        // paste). The program then sees one paste, newlines included, like Ctrl+V.
        // A unique buffer name keeps two concurrent sends from overwriting each other.
        var buffer = $"sb-{Guid.NewGuid():N}";
        await RunAsync(["load-buffer", "-b", buffer, "-"], stdin: text, ct: ct);
        await RunAsync(["paste-buffer", "-p", "-d", "-b", buffer, "-t", Target(id)], ct: ct);

        if (submit)
        {
            await Task.Delay(SubmitDelay, ct);
            await SendKeysAsync(id, ["Enter"], ct);
        }
    }

    /// <summary>
    /// Presses keys by tmux name: "Enter", "Escape", "Up", "Down", "C-c", "y" and so on.
    /// Used to answer dialogs such as the folder trust prompt.
    /// </summary>
    public async Task SendKeysAsync(string id, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        await RequireAsync(id, ct);
        var args = new List<string> { "send-keys", "-t", Target(id) };
        args.AddRange(keys);
        await RunAsync(args, ct: ct);
    }

    /// <summary>
    /// Returns what the window shows: the visible screen plus up to
    /// <paramref name="scrollback"/> lines of history above it.
    /// </summary>
    public async Task<string> ReadAsync(string id, int scrollback = 0, CancellationToken ct = default)
    {
        await RequireAsync(id, ct);
        // -p prints to stdout. -J joins lines that tmux wrapped because the window was narrow.
        var args = new List<string> { "capture-pane", "-p", "-J", "-t", Target(id) };
        if (scrollback > 0)
        {
            args.AddRange(["-S", $"-{scrollback}"]);
        }

        var result = await RunAsync(args, ct: ct);
        return result.StdOut.TrimEnd();
    }

    /// <summary>
    /// Polls the visible screen until it contains <paramref name="text"/> (case-insensitive).
    /// Returns false on timeout. This is how we check that a dialog really is on screen
    /// before pressing a key to answer it.
    /// </summary>
    public async Task<bool> WaitForTextAsync(string id, string text, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var screen = await ReadAsync(id, ct: ct);
            if (screen.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    public async Task<IReadOnlyList<TmuxWindow>> ListAsync(CancellationToken ct = default)
    {
        if (!await SessionExistsAsync(ct))
        {
            return [];
        }

        // Tab-separated fields; the format variables are described in `man tmux` (FORMATS).
        const string format = "#{window_name}\t#{pane_current_path}\t#{pane_dead}\t#{pane_dead_status}\t#{pane_pid}\t#{window_activity}";
        var result = await RunAsync(["list-windows", "-t", $"={SessionName}", "-F", format], ct: ct);

        var windows = new List<TmuxWindow>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length < 6 || f[0] == HomeWindow)
            {
                continue;
            }

            windows.Add(new TmuxWindow(
                Id: f[0],
                CurrentPath: f[1],
                Dead: f[2] == "1",
                ExitStatus: int.TryParse(f[3], out var status) ? status : null,
                Pid: int.TryParse(f[4], out var pid) ? pid : 0,
                LastActivity: DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[5], CultureInfo.InvariantCulture))));
        }

        return windows;
    }

    public async Task<TmuxWindow?> FindAsync(string id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(w => w.Id == id);

    /// <summary>
    /// Asks Claude Code to exit with /exit and waits up to <paramref name="grace"/>.
    /// Then closes the window, which ends the program if it is still running.
    /// </summary>
    public async Task StopAsync(string id, TimeSpan grace, CancellationToken ct = default)
    {
        var window = await RequireAsync(id, ct);

        if (!window.Dead && grace > TimeSpan.Zero)
        {
            await SendTextAsync(id, "/exit", ct: ct);
            var deadline = DateTimeOffset.UtcNow + grace;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                if ((await FindAsync(id, ct))?.Dead != false)
                {
                    break;
                }
            }
        }

        // kill-window sends SIGHUP to whatever still runs in the window.
        await RunAsync(["kill-window", "-t", Target(id)], ct: ct);
    }

    // "=" asks tmux for an exact name match. Without it, tmux also accepts a prefix or a
    // pattern, so the target "api" could hit the window "api-fix".
    private string Target(string id) => $"={SessionName}:={id}";

    private async Task<TmuxWindow> RequireAsync(string id, CancellationToken ct)
    {
        SessionId.Validate(id);
        return await FindAsync(id, ct) ?? throw new TmuxException($"No session named '{id}'. Run `sb list` to see running sessions.");
    }

    private async Task<bool> SessionExistsAsync(CancellationToken ct)
    {
        var result = await RunAsync(["has-session", "-t", $"={SessionName}"], check: false, ct: ct);
        return result.ExitCode == 0;
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (await SessionExistsAsync(ct))
        {
            return;
        }

        // A detached tmux session has no real terminal, so tmux picks a size (80x24 by default).
        // Claude Code's screen is cramped at that size, so we ask for 200x50.
        await RunAsync(["new-session", "-d", "-s", SessionName, "-n", HomeWindow, "-x", "200", "-y", "50"], ct: ct);
        // set-option reads -t as a pane target, where "=name" alone does not resolve to a
        // session. The trailing ":" makes it "session name, current window".
        await RunAsync(["set-option", "-t", $"={SessionName}:", "default-size", "200x50"], ct: ct);
    }

    private Task SetWindowOptionAsync(string id, string option, string value, CancellationToken ct) =>
        RunAsync(["set-option", "-w", "-t", Target(id), option, value], ct: ct);

    private static async Task<ProcessResult> RunAsync(
        IEnumerable<string> args, string? stdin = null, bool check = true, CancellationToken ct = default)
    {
        var argList = args.ToList();
        var result = await ProcessRunner.RunAsync("tmux", argList, stdin, ct);
        if (check && result.ExitCode != 0)
        {
            throw new TmuxException($"tmux {argList[0]} failed: {result.StdErr.Trim()}");
        }

        return result;
    }
}

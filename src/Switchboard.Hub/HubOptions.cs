namespace Switchboard.Hub;

/// <summary>Hub settings, read from environment variables (scripts/run-hub.sh loads .env).</summary>
public sealed class HubOptions
{
    /// <summary>Where the hub listens, e.g. http://127.0.0.1:7700. Also given to MCP servers.</summary>
    public required string Url { get; init; }

    /// <summary>The owner's token for scripts and curl. Requests with it act as the owner.</summary>
    public required string AdminToken { get; init; }

    /// <summary>PBKDF2 hash of the owner's web UI password (set with `./sb passwd`), or null.</summary>
    public string? OwnerPasswordHash { get; init; }

    /// <summary>~/.switchboard: per-session MCP config files and cookie keys live here.</summary>
    public required string StateDir { get; init; }

    /// <summary>The built Manager MCP server that manager sessions run.</summary>
    public required string ManagerDll { get; init; }

    /// <summary>The built channel server that every session runs.</summary>
    public required string ChannelDll { get; init; }

    /// <summary>Keep a manager session running (SWITCHBOARD_MANAGER_AUTOSTART, default true).</summary>
    public bool ManagerAutostart { get; init; } = true;

    /// <summary>
    /// The manager's working folder (SWITCHBOARD_MANAGER_DIR, default ~/.switchboard/manager).
    /// It must outlive reboots: Claude Code keys the conversation that --continue resumes to the
    /// folder, so /tmp (wiped when WSL restarts) would lose it.
    /// </summary>
    public required string ManagerDir { get; init; }

    /// <summary>Start the manager with a fresh conversation once its current one is this old.</summary>
    public TimeSpan ManagerFreshAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Waits before restart attempts 1, 2, 3, 4... (the last value repeats). From
    /// SWITCHBOARD_MANAGER_BACKOFF as comma-separated seconds; default 0,10,60,300.
    /// </summary>
    public IReadOnlyList<TimeSpan> ManagerBackoff { get; init; } =
        [TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    public static HubOptions FromEnvironment()
    {
        var adminToken = Required("SWITCHBOARD_ADMIN_TOKEN");
        if (adminToken.Length < 32)
        {
            throw new InvalidOperationException("SWITCHBOARD_ADMIN_TOKEN must be at least 32 characters.");
        }

        var root = FindRepoRoot();
        // SWITCHBOARD_STATE_DIR lets a second (test) hub keep its own MCP config files and keys.
        var stateDir = Environment.GetEnvironmentVariable("SWITCHBOARD_STATE_DIR") is { Length: > 0 } sd
            ? sd
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switchboard");
        var freshHours = int.TryParse(Environment.GetEnvironmentVariable("SWITCHBOARD_MANAGER_FRESH_HOURS"), out var h) && h > 0 ? h : 24;
        return new HubOptions
        {
            ManagerAutostart = !string.Equals(Environment.GetEnvironmentVariable("SWITCHBOARD_MANAGER_AUTOSTART"), "false", StringComparison.OrdinalIgnoreCase),
            ManagerDir = Environment.GetEnvironmentVariable("SWITCHBOARD_MANAGER_DIR") is { Length: > 0 } dir ? dir : Path.Combine(stateDir, "manager"),
            ManagerFreshAfter = TimeSpan.FromHours(freshHours),
            ManagerBackoff = Environment.GetEnvironmentVariable("SWITCHBOARD_MANAGER_BACKOFF") is { Length: > 0 } backoff
                ? backoff.Split(',').Select(s => TimeSpan.FromSeconds(int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture))).ToList()
                : [TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)],
            Url = Environment.GetEnvironmentVariable("SWITCHBOARD_HUB_URL") ?? "http://127.0.0.1:7700",
            AdminToken = adminToken,
            OwnerPasswordHash = Environment.GetEnvironmentVariable("SWITCHBOARD_OWNER_PASSWORD_HASH") is { Length: > 0 } hash ? hash : null,
            StateDir = stateDir,
            ManagerDll = Path.Combine(root, "src", "Switchboard.Manager", "bin", "Debug", "net10.0", "Switchboard.Manager.dll"),
            ChannelDll = Path.Combine(root, "src", "Switchboard.Channel", "bin", "Debug", "net10.0", "Switchboard.Channel.dll"),
        };
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set. Run scripts/setup-env.sh and start the hub with scripts/run-hub.sh.");

    // The hub runs from src/Switchboard.Hub/bin/..., so walk up until we find the solution file.
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Switchboard.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find Switchboard.slnx above " + AppContext.BaseDirectory);
    }
}

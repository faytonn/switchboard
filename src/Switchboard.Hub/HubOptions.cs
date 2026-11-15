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

    public static HubOptions FromEnvironment()
    {
        var adminToken = Required("SWITCHBOARD_ADMIN_TOKEN");
        if (adminToken.Length < 32)
        {
            throw new InvalidOperationException("SWITCHBOARD_ADMIN_TOKEN must be at least 32 characters.");
        }

        var root = FindRepoRoot();
        return new HubOptions
        {
            Url = Environment.GetEnvironmentVariable("SWITCHBOARD_HUB_URL") ?? "http://127.0.0.1:7700",
            AdminToken = adminToken,
            OwnerPasswordHash = Environment.GetEnvironmentVariable("SWITCHBOARD_OWNER_PASSWORD_HASH") is { Length: > 0 } hash ? hash : null,
            StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switchboard"),
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

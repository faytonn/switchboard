namespace Switchboard.Core.Claude;

/// <summary>Builds the command line that starts Claude Code in a session window.</summary>
public static class ClaudeCommand
{
    /// <summary>
    /// Sessions start in acceptEdits: Claude may edit files in the project without asking, but
    /// shell commands and other tools still need approval. We always pass the mode explicitly,
    /// so sessions never inherit whatever defaultMode the owner's own settings use.
    /// </summary>
    public const string DefaultPermissionMode = "acceptEdits";

    /// <summary>The choices `claude --help` lists for --permission-mode (Claude Code 2.1.325).</summary>
    public static readonly IReadOnlyList<string> PermissionModes =
        ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"];

    public static IReadOnlyList<string> Build(string permissionMode, IReadOnlyList<string> extraArgs)
    {
        // Checked here because a bad mode makes claude exit at once, and the only trace would
        // be an error inside a dead tmux window.
        if (!PermissionModes.Contains(permissionMode))
        {
            throw new ArgumentException(
                $"Unknown permission mode '{permissionMode}'. Use one of: {string.Join(", ", PermissionModes)}.");
        }

        if (extraArgs.Any(a => a == "--permission-mode" || a.StartsWith("--permission-mode=", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Set the permission mode with --mode, not in the claude arguments.");
        }

        return ["claude", "--permission-mode", permissionMode, .. extraArgs];
    }
}

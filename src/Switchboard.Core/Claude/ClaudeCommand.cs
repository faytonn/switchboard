using System.Text.RegularExpressions;

namespace Switchboard.Core.Claude;

/// <summary>How to start Claude Code in a session window.</summary>
/// <param name="PermissionMode">See <see cref="ClaudeCommand.PermissionModes"/>.</param>
/// <param name="Model">A model alias (opus, sonnet, haiku, fable) or a full model name.</param>
/// <param name="Effort">How much Claude thinks before answering: low ... max.</param>
/// <param name="Connectors">true also loads the owner's claude.ai connectors and plugin MCP
/// servers; false passes --strict-mcp-config so only the --mcp-config servers load.</param>
/// <param name="Continue">--continue: resume the latest conversation in the project folder.
/// Claude Code exits with "No conversation found to continue" if there is none, so callers
/// must be ready to start fresh instead.</param>
public sealed record ClaudeLaunch(string PermissionMode, string Model, string Effort, bool Connectors, bool Continue = false);

/// <summary>Builds the command line that starts Claude Code in a session window.</summary>
public static partial class ClaudeCommand
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

    /// <summary>The choices `claude --help` lists for --effort.</summary>
    public static readonly IReadOnlyList<string> EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>What Claude Code prints (then exits) when --continue finds nothing to resume.</summary>
    public const string NoConversationToContinue = "No conversation found to continue";

    /// <summary>Model aliases Claude Code resolves to the latest model of that family.</summary>
    public static readonly IReadOnlyList<string> ModelAliases = ["opus", "sonnet", "haiku", "fable"];

    // Full model names such as claude-sonnet-5-5 are allowed too.
    [GeneratedRegex("^claude-[a-z0-9-]{1,60}$")]
    private static partial Regex FullModelName();

    public static IReadOnlyList<string> Build(ClaudeLaunch launch, IReadOnlyList<string> extraArgs)
    {
        // Checked here because a bad value makes claude exit at once, and the only trace would
        // be an error inside a dead tmux window.
        if (!PermissionModes.Contains(launch.PermissionMode))
        {
            throw new ArgumentException(
                $"Unknown permission mode '{launch.PermissionMode}'. Use one of: {string.Join(", ", PermissionModes)}.");
        }

        if (!ModelAliases.Contains(launch.Model) && !FullModelName().IsMatch(launch.Model))
        {
            throw new ArgumentException(
                $"Unknown model '{launch.Model}'. Use one of {string.Join(", ", ModelAliases)}, or a full name like claude-sonnet-5-5.");
        }

        if (!EffortLevels.Contains(launch.Effort))
        {
            throw new ArgumentException($"Unknown effort '{launch.Effort}'. Use one of: {string.Join(", ", EffortLevels)}.");
        }

        if (extraArgs.Any(a => a.StartsWith("--permission-mode", StringComparison.Ordinal)
                               || a.StartsWith("--model", StringComparison.Ordinal)
                               || a.StartsWith("--effort", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Set the permission mode, model and effort with their own options, not in the claude arguments.");
        }

        var args = new List<string>
        {
            "claude",
            "--permission-mode", launch.PermissionMode,
            "--model", launch.Model,
            "--effort", launch.Effort,
        };

        // Without this, every session also loads the owner's claude.ai connectors and plugin
        // MCP servers: a few hundred tool definitions that Claude re-reads on every turn whose
        // prompt cache has expired. Most sessions never need Gmail or Notion.
        if (!launch.Connectors)
        {
            args.Add("--strict-mcp-config");
        }

        if (launch.Continue)
        {
            args.Add("--continue");
        }

        args.AddRange(extraArgs);
        return args;
    }
}

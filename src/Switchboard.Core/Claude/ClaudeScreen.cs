namespace Switchboard.Core.Claude;

/// <summary>
/// A menu dialog that Claude Code shows before its prompt, which we may answer automatically.
/// </summary>
/// <param name="Name">Name for messages, e.g. "folder trust".</param>
/// <param name="Markers">Text that must all be on screen. Pick text only the dialog shows.</param>
/// <param name="Option">The option to choose, e.g. "Yes, I trust this folder".</param>
/// <param name="ExactLines">Lines that must appear exactly (after trimming). Use these when a
/// substring match would be unsafe, such as the list of channels about to be loaded.</param>
public sealed record ClaudeDialog(string Name, IReadOnlyList<string> Markers, string Option, IReadOnlyList<string>? ExactLines = null)
{
    /// <summary>The dialog Claude Code shows the first time it opens a folder.</summary>
    public static readonly ClaudeDialog FolderTrust = new(
        "folder trust",
        ["Is this a project you created or one you trust?", "Yes, I trust this folder"],
        "Yes, I trust this folder");

    /// <summary>
    /// The warning shown on every start with --dangerously-load-development-channels. We only
    /// confirm it when the "Channels:" line lists exactly the channels we asked for. A substring
    /// check would also pass for "Channels: server:switchboard-channel, server:something-else".
    /// </summary>
    public static ClaudeDialog DevChannels(IReadOnlyList<string> channels) => new(
        "development channels warning",
        ["WARNING: Loading development channels", "I am using this for local development"],
        "I am using this for local development",
        ExactLines: [$"Channels: {string.Join(", ", channels)}"]);
}

public enum ScreenState
{
    /// <summary>Still starting, or showing something we do not recognize.</summary>
    Unknown,

    /// <summary>The dialog we are looking for is open and waiting for an answer.</summary>
    Dialog,

    /// <summary>Claude Code shows its input prompt, so no dialog is open.</summary>
    Prompt,
}

/// <summary>
/// Reads a captured Claude Code screen. Pure text checks, no tmux, so they are easy to test.
/// </summary>
public static class ClaudeScreen
{
    private const char Pointer = '❯';
    private const string MenuFooter = "Enter to confirm";

    public static ScreenState Classify(string screen, ClaudeDialog dialog)
    {
        var lines = Lines(screen);

        // Check the prompt first. Earlier chat output can contain the dialog's words, but a
        // visible input box means no dialog is open right now.
        if (HasInputPrompt(lines))
        {
            return ScreenState.Prompt;
        }

        // The dialog counts only when every marker is on screen and the menu footer is the
        // last line, which shows the menu is still open and waiting.
        var lastLine = lines.LastOrDefault(l => l.Length > 0) ?? "";
        var allMarkers = dialog.Markers.All(m => screen.Contains(m, StringComparison.Ordinal));
        var exactLines = (dialog.ExactLines ?? []).All(x => lines.Any(l => l.Trim() == x));
        return allMarkers && exactLines && lastLine.Contains(MenuFooter, StringComparison.Ordinal)
            ? ScreenState.Dialog
            : ScreenState.Unknown;
    }

    /// <summary>True when the menu pointer (❯) is on the line with <paramref name="option"/>.</summary>
    public static bool IsSelected(string screen, string option) =>
        Lines(screen).Any(l => l.TrimStart().StartsWith(Pointer) && l.Contains(option, StringComparison.Ordinal));

    public static bool HasInputPrompt(string screen) => HasInputPrompt(Lines(screen));

    /// <summary>
    /// What the session is doing, read from its screen: "approval" (a tool prompt is open,
    /// e.g. "Do you want to proceed?"), "working" (Claude Code's footer says "esc to interrupt"
    /// while it runs), or "idle". Only the bottom lines are checked, so the same words in
    /// earlier chat output don't count.
    /// </summary>
    public static string Activity(string screen)
    {
        var lines = Lines(screen);
        var bottom = lines.Where(l => l.Trim().Length > 0).TakeLast(12).ToList();
        if (!HasInputPrompt(lines)
            && bottom.Any(l => l.Contains("Do you want to", StringComparison.Ordinal))
            && bottom.Any(l => l.Contains("Esc to cancel", StringComparison.Ordinal)))
        {
            return "approval";
        }

        return bottom.TakeLast(3).Any(l => l.Contains("esc to interrupt", StringComparison.Ordinal)) ? "working" : "idle";
    }

    // Claude Code's input box is a "❯" line with a horizontal rule (────) right above and
    // right below. Dialog menus also use "❯" for the selected option, but without rules
    // around it.
    private static bool HasInputPrompt(List<string> lines)
    {
        for (var i = 1; i < lines.Count - 1; i++)
        {
            if (lines[i].TrimStart().StartsWith(Pointer) && IsRule(lines[i - 1]) && IsRule(lines[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Lines(string screen) => screen.Split('\n').Select(l => l.TrimEnd()).ToList();

    private static bool IsRule(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 20 && trimmed.All(c => c == '─');
    }
}

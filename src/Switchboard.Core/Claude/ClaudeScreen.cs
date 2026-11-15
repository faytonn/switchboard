namespace Switchboard.Core.Claude;

/// <summary>
/// A menu dialog that Claude Code shows before its prompt, which we may answer automatically.
/// </summary>
/// <param name="Name">Name for messages, e.g. "folder trust".</param>
/// <param name="Markers">Text that must all be on screen. Pick text only the dialog shows.</param>
/// <param name="Option">The option to choose, e.g. "Yes, I trust this folder".</param>
public sealed record ClaudeDialog(string Name, IReadOnlyList<string> Markers, string Option)
{
    /// <summary>The dialog Claude Code shows the first time it opens a folder.</summary>
    public static readonly ClaudeDialog FolderTrust = new(
        "folder trust",
        ["Is this a project you created or one you trust?", "Yes, I trust this folder"],
        "Yes, I trust this folder");
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
        var lines = screen.Split('\n').Select(l => l.TrimEnd()).ToList();

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
        return allMarkers && lastLine.Contains(MenuFooter, StringComparison.Ordinal)
            ? ScreenState.Dialog
            : ScreenState.Unknown;
    }

    /// <summary>True when the menu pointer (❯) is on the line with <paramref name="option"/>.</summary>
    public static bool IsSelected(string screen, string option) =>
        screen.Split('\n').Any(l =>
            l.TrimStart().StartsWith(Pointer) && l.Contains(option, StringComparison.Ordinal));

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

    private static bool IsRule(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 20 && trimmed.All(c => c == '─');
    }
}

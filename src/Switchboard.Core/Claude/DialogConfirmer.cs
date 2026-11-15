using Switchboard.Core.Tmux;

namespace Switchboard.Core.Claude;

public enum ConfirmResult
{
    /// <summary>The dialog was on screen and we chose the option.</summary>
    Confirmed,

    /// <summary>Claude Code was already at its prompt, so we pressed nothing.</summary>
    NotShown,
}

/// <summary>
/// Answers a Claude Code dialog through tmux, but only after checking that the dialog is
/// really on screen. Pressing keys blindly is dangerous: at the normal prompt, Down and Up
/// walk through message history, and Enter then sends whatever came up (for example /exit).
/// </summary>
public sealed class DialogConfirmer(TmuxClient tmux)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KeySettle = TimeSpan.FromMilliseconds(300);
    private const int MaxDownPresses = 5;

    public async Task<ConfirmResult> ConfirmAsync(string id, ClaudeDialog dialog, TimeSpan timeout, CancellationToken ct = default)
    {
        // 1. Wait until the screen is either the dialog or the prompt. Claude Code needs a
        //    moment to start, and until then the screen is neither.
        var state = ScreenState.Unknown;
        string screen = "";
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            await ThrowIfExitedAsync(id, ct);
            screen = await tmux.ReadAsync(id, ct: ct);
            state = ClaudeScreen.Classify(screen, dialog);
            if (state != ScreenState.Unknown)
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TmuxException(
                    $"Timed out after {timeout.TotalSeconds:0}s: neither the {dialog.Name} dialog nor the prompt appeared. Run `sb read {id}` to see the screen.");
            }

            await Task.Delay(PollInterval, ct);
        }

        if (state == ScreenState.Prompt)
        {
            return ConfirmResult.NotShown;
        }

        // 2. Move the pointer to our option one Down at a time. After every press, check that
        //    the dialog is still open before pressing again.
        for (var presses = 0; !ClaudeScreen.IsSelected(screen, dialog.Option); presses++)
        {
            if (presses == MaxDownPresses)
            {
                throw new TmuxException($"Could not select \"{dialog.Option}\" in the {dialog.Name} dialog. Pressed nothing else.");
            }

            await tmux.SendKeysAsync(id, ["Down"], ct);
            await Task.Delay(KeySettle, ct);
            screen = await tmux.ReadAsync(id, ct: ct);
            if (ClaudeScreen.Classify(screen, dialog) != ScreenState.Dialog)
            {
                throw new TmuxException($"The {dialog.Name} dialog closed unexpectedly. Pressed nothing else.");
            }
        }

        // 3. Our option is selected and the dialog is still open: confirm it.
        await tmux.SendKeysAsync(id, ["Enter"], ct);
        return ConfirmResult.Confirmed;
    }

    private async Task ThrowIfExitedAsync(string id, CancellationToken ct)
    {
        var window = await tmux.FindAsync(id, ct) ?? throw new TmuxException($"No session named '{id}'.");
        if (window.Dead)
        {
            throw new TmuxException($"Claude Code in '{id}' exited (status {window.ExitStatus}). Run `sb read {id}` to see why.");
        }
    }
}

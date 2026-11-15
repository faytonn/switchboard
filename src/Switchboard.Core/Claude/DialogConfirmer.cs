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
/// Answers Claude Code dialogs through tmux, but only after checking that the dialog is
/// really on screen. Pressing keys blindly is dangerous: at the normal prompt, Down and Up
/// walk through message history, and Enter then sends whatever came up (for example /exit).
/// </summary>
public sealed class DialogConfirmer(TmuxClient tmux)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KeySettle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private const int MaxDownPresses = 5;

    /// <summary>Answers one dialog if it shows up before the prompt does.</summary>
    public async Task<ConfirmResult> ConfirmAsync(string id, ClaudeDialog dialog, TimeSpan timeout, CancellationToken ct = default) =>
        (await ConfirmAllAsync(id, [dialog], timeout, ct)).Count > 0 ? ConfirmResult.Confirmed : ConfirmResult.NotShown;

    /// <summary>
    /// Waits for the prompt, answering any of <paramref name="dialogs"/> that appear on the
    /// way, in whatever order Claude Code shows them. Returns the dialogs it answered. Throws
    /// if the screen shows something else (another dialog, an error) until the timeout.
    /// </summary>
    public async Task<IReadOnlyList<ClaudeDialog>> ConfirmAllAsync(
        string id, IReadOnlyList<ClaudeDialog> dialogs, TimeSpan timeout, CancellationToken ct = default)
    {
        var answered = new List<ClaudeDialog>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            await ThrowIfExitedAsync(id, ct);
            var screen = await tmux.ReadAsync(id, ct: ct);
            if (ClaudeScreen.HasInputPrompt(screen))
            {
                return answered;
            }

            var open = dialogs.FirstOrDefault(d => ClaudeScreen.Classify(screen, d) == ScreenState.Dialog);
            if (open is not null)
            {
                // Each dialog appears once per start. Seeing one again means our answer
                // didn't take, and pressing more keys would only make things worse.
                if (answered.Contains(open))
                {
                    throw new TmuxException($"The {open.Name} dialog came back after we answered it. Pressed nothing else.");
                }

                await AnswerAsync(id, open, screen, ct);
                answered.Add(open);
                await WaitUntilClosedAsync(id, open, ct);
                continue;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var names = string.Join(" or ", dialogs.Select(d => d.Name));
                throw new TmuxException(
                    $"Timed out after {timeout.TotalSeconds:0}s: neither the prompt nor the {names} dialog appeared. Run `sb read {id}` to see the screen.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    // Move the pointer to the option one Down at a time, checking after every press that the
    // dialog is still open. Then press Enter.
    private async Task AnswerAsync(string id, ClaudeDialog dialog, string screen, CancellationToken ct)
    {
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

        await tmux.SendKeysAsync(id, ["Enter"], ct);
    }

    // The old dialog can stay on screen for a moment after Enter. Without this wait, the next
    // loop could see it again and treat it as a dialog that came back.
    private async Task WaitUntilClosedAsync(string id, ClaudeDialog dialog, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + CloseTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(KeySettle, ct);
            var screen = await tmux.ReadAsync(id, ct: ct);
            if (ClaudeScreen.Classify(screen, dialog) != ScreenState.Dialog)
            {
                return;
            }
        }
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

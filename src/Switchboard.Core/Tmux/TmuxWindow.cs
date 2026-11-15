namespace Switchboard.Core.Tmux;

/// <summary>One Claude Code session = one tmux window inside the "switchboard" tmux session.</summary>
/// <param name="Id">Session id, which is also the tmux window name.</param>
/// <param name="CurrentPath">Working folder of the process in the window.</param>
/// <param name="Dead">True when the program exited. The window stays open (remain-on-exit) so its last output can still be read.</param>
/// <param name="ExitStatus">Exit code when <paramref name="Dead"/> is true.</param>
/// <param name="Pid">Process id of the program in the window.</param>
/// <param name="LastActivity">Last time the window printed output.</param>
public sealed record TmuxWindow(
    string Id,
    string CurrentPath,
    bool Dead,
    int? ExitStatus,
    int Pid,
    DateTimeOffset LastActivity);

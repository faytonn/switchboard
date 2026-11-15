namespace Switchboard.Hub;

// JSON bodies of the hub API. ASP.NET Core writes them in camelCase.

public sealed record StartSessionRequest(string Id, string ProjectDir, string? PermissionMode = null, bool Manager = false);

public sealed record SendMessageRequest(string Text);

public sealed record StopSessionRequest(bool Force = false);

/// <param name="To">"owner", or the id of a session that has messaged the caller.</param>
public sealed record ReplyRequest(string To, string Text);

/// <summary>A session as the API reports it: the database row plus the live state.</summary>
/// <param name="Status">From Postgres: starting, running or stopped.</param>
/// <param name="Window">From tmux: running, exited (Claude Code quit, window kept) or missing.</param>
/// <param name="Channel">True while the session's channel server is connected to the hub.</param>
/// <param name="Activity">From the screen: idle, working or approval; null if not running.</param>
/// <param name="LastMessage">The newest message to or from the session, for the list preview.</param>
public sealed record SessionInfo(
    string Id,
    string Role,
    string Status,
    string Window,
    int? ExitStatus,
    bool Channel,
    string ProjectDir,
    DateTimeOffset StartedAt,
    int? IdleSeconds,
    string? Activity = null,
    MessagePreview? LastMessage = null);

public sealed record MessagePreview(string? From, string? To, string Body, DateTimeOffset CreatedAt);

/// <param name="Dialogs">The startup dialogs we answered, e.g. ["folder trust", "development channels warning"].</param>
public sealed record StartSessionResponse(SessionInfo Session, IReadOnlyList<string> Dialogs);

/// <param name="Delivered">True if the session's channel pushed it into Claude Code; false if
/// it is queued (the channel is offline or slow) and will be delivered on reconnect.</param>
public sealed record SendMessageResponse(long MessageId, bool Delivered);

public sealed record ScreenResponse(string Id, string Screen);

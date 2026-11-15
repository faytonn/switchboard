namespace Switchboard.Hub;

// JSON bodies of the hub API. ASP.NET Core writes them in camelCase.

public sealed record StartSessionRequest(string Id, string ProjectDir, string? PermissionMode = null, bool Manager = false);

public sealed record SendMessageRequest(string Text);

public sealed record StopSessionRequest(bool Force = false);

/// <summary>A session as the API reports it: the database row plus the live tmux state.</summary>
/// <param name="Status">From Postgres: starting, running or stopped.</param>
/// <param name="Window">From tmux: running, exited (Claude Code quit, window kept) or missing.</param>
public sealed record SessionInfo(
    string Id,
    string Role,
    string Status,
    string Window,
    int? ExitStatus,
    string ProjectDir,
    DateTimeOffset StartedAt,
    int? IdleSeconds);

/// <param name="Trust">"confirmed" if we answered the folder trust dialog, "not-shown" if there was none.</param>
public sealed record StartSessionResponse(SessionInfo Session, string Trust);

public sealed record SendMessageResponse(long MessageId, DateTimeOffset? DeliveredAt);

public sealed record ScreenResponse(string Id, string Screen);

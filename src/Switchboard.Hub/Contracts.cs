namespace Switchboard.Hub;

// JSON bodies of the hub API. ASP.NET Core writes them in camelCase.

/// <param name="Icon">Optional Lucide icon name for the UI tile (GET /api/icons lists them).
/// Without one, the hub guesses from the folder name and README.</param>
/// <param name="Color">Optional palette colour 0-7 for the tile.</param>
/// <param name="Model">opus, sonnet, haiku, fable or a full model name. Default: sonnet for the
/// manager (it mostly routes), opus for workers.</param>
/// <param name="Effort">low, medium, high, xhigh or max. Default: medium.</param>
/// <param name="Connectors">Also load the owner's claude.ai connectors and plugin MCP servers
/// (Gmail, Notion, GitHub...). Off by default; only the owner can turn it on.</param>
public sealed record StartSessionRequest(
    string Id, string ProjectDir, string? PermissionMode = null, bool Manager = false, string? Icon = null, short? Color = null,
    string? Model = null, string? Effort = null, bool Connectors = false);

/// <summary>The owner's choice in the UI's tile picker. null icon = initials, null color = automatic.</summary>
public sealed record AppearanceRequest(string? Icon, short? Color);

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
    MessagePreview? LastMessage = null,
    string? Icon = null,
    short? Color = null,
    string? Model = null,
    string? Effort = null,
    bool Connectors = false);

public sealed record MessagePreview(string? From, string? To, string Body, DateTimeOffset CreatedAt);

/// <param name="Dialogs">The startup dialogs we answered, e.g. ["folder trust", "development channels warning"].</param>
public sealed record StartSessionResponse(SessionInfo Session, IReadOnlyList<string> Dialogs);

/// <param name="Delivered">True if the session's channel pushed it into Claude Code; false if
/// it is queued (the channel is offline or slow) and will be delivered on reconnect.</param>
public sealed record SendMessageResponse(long MessageId, bool Delivered);

public sealed record ScreenResponse(string Id, string Screen);

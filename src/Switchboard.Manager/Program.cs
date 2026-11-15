using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Switchboard.Manager;

// The manager's MCP server. Claude Code starts it (see the --mcp-config file the hub writes)
// and talks to it over stdin/stdout. stdout carries the MCP protocol, so this program must
// never print anything else there: all diagnostics go to stderr.

var hubUrl = Required("SWITCHBOARD_HUB_URL");
var token = Required("SWITCHBOARD_TOKEN");
var sessionId = Environment.GetEnvironmentVariable("SWITCHBOARD_SESSION_ID") ?? "manager";

// Starting a session waits for Claude Code to boot and for the trust dialog, so allow time.
using var http = new HttpClient { BaseAddress = new Uri(hubUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(90) };
http.DefaultRequestHeaders.Authorization = new("Bearer", token);
var hub = new HubClient(http);

var tools = new McpServerPrimitiveCollection<McpServerTool>
{
    McpServerTool.Create(
        ([Description("Short session id: 1-32 lowercase letters, digits or hyphens, e.g. 'api-fix'.")] string id,
         [Description("Project folder for the session. A Linux path (/home/...) or a Windows path (C:\\...).")] string project_dir,
         CancellationToken ct,
         // Default values make a parameter optional in the tool's JSON schema.
         [Description("Optional Claude Code permission mode: acceptEdits (default), plan, manual, auto, dontAsk or bypassPermissions.")] string? permission_mode = null) =>
            hub.StartAsync(id, project_dir, permission_mode, ct),
        new() { Name = "start_session", Description = "Start a new Claude Code session for a project folder, in its own tmux window." }),

    McpServerTool.Create(
        ([Description("Session id.")] string id,
         [Description("The message. Multi-line text arrives as one message.")] string text,
         CancellationToken ct) => hub.SendAsync(id, text, ct),
        new() { Name = "send_message", Description = "Send a message to a session through its channel. Returns once delivered, not when the session has answered; its reply arrives later as a channel message." }),

    McpServerTool.Create(
        ([Description("Session id.")] string id,
         CancellationToken ct,
         [Description("Extra lines of scrollback above the visible screen (0-2000). Default 0.")] int scrollback = 0) =>
            hub.ReadScreenAsync(id, scrollback, ct),
        new() { Name = "read_screen", Description = "Read what a session's terminal shows right now: its latest output, or a dialog waiting for an answer." }),

    McpServerTool.Create(
        (CancellationToken ct) => hub.ListAsync(ct),
        new() { Name = "list_sessions", Description = "List all sessions with their role, status, tmux window state, idle time and folder." }),

    McpServerTool.Create(
        ([Description("Session id.")] string id,
         CancellationToken ct,
         [Description("true closes the window at once instead of asking Claude Code to /exit first.")] bool force = false) =>
            hub.StopAsync(id, force, ct),
        new() { Name = "stop_session", Description = "Stop a session: /exit, then close its tmux window." }),
};

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "switchboard-manager", Version = "0.3.0" },
    ServerInstructions = $"""
        You are the Switchboard manager (session '{sessionId}'). These tools start and control
        other Claude Code sessions, each in its own tmux window, through the Switchboard hub.
        - send_message delivers the text through the session's channel and returns at once.
          The session then works on its own and answers with its reply tool. Its answer
          arrives in YOUR session as a channel message from="<its id>". Don't poll for it:
          end your turn, and you will be woken when the reply comes.
        - read_screen shows a session's terminal as text. Use it when a session seems stuck
          (for example on a permission prompt), not to wait for answers.
        - Your own user writes to you through the channel with from="owner". Answer them with
          the reply tool (to="owner"), since they may not be watching your terminal.
        """,
    ToolCollection = tools,
};

await using var transport = new StdioServerTransport(options, loggerFactory: null);
await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
Console.Error.WriteLine($"switchboard-manager: serving session '{sessionId}', hub {hubUrl}");
await server.RunAsync();

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is not set. This server is started by Claude Code from the config file the hub writes.");

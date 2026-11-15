using System.Globalization;
using Npgsql;
using Switchboard.Core;
using Switchboard.Core.Claude;
using Switchboard.Core.Data;
using Switchboard.Core.Tmux;

// `sb` is a small command-line tool for driving sessions by hand. Phase 1 uses it to test
// tmux control, phase 2 adds message logging. Later phases reuse the same Core classes
// from the hub.

var tmux = TmuxClient.FromEnvironment();

// Postgres is optional for the tmux commands: without SWITCHBOARD_DB they still work but log
// nothing. The ./sb script loads SWITCHBOARD_DB from .env.
var connectionString = Environment.GetEnvironmentVariable("SWITCHBOARD_DB");
await using var db = string.IsNullOrWhiteSpace(connectionString) ? null : NpgsqlDataSource.Create(connectionString);
var sessions = db is null ? null : new SessionStore(db);
var messages = db is null ? null : new MessageStore(db);

try
{
    return args switch
    {
        ["start", var id, var dir, .. var rest] => await Start(id, dir, rest),
        ["trust", var id, .. var opts] => await Trust(id, opts),
        ["send", var id, .. var words] when words.Length > 0 => await Send(id, words),
        ["keys", var id, .. var keys] when keys.Length > 0 => await Keys(id, keys),
        ["read", var id, .. var opts] => await Read(id, opts),
        ["wait", var id, var text, .. var opts] => await Wait(id, text, opts),
        ["list"] => await List(),
        ["stop", var id, .. var opts] => await Stop(id, opts),
        ["migrate"] => await Migrate(),
        ["log", var id, .. var opts] => await Log(id, opts),
        ["search", .. var words] when words.Length > 0 => await Search(words),
        _ => Usage(),
    };
}
catch (Exception e) when (e is TmuxException or ArgumentException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
{
    Console.Error.WriteLine("error: the database has no Switchboard tables yet. Run: ./sb migrate");
    return 1;
}
catch (NpgsqlException e)
{
    Console.Error.WriteLine($"error: database: {e.Message}");
    Console.Error.WriteLine("Is Postgres running? Try: docker compose up -d");
    return 1;
}

async Task<int> Start(string id, string dir, string[] rest)
{
    // Options come first; everything after "--" is passed to claude,
    // e.g. `sb start demo ~/proj --mode plan -- --model sonnet`.
    var split = Array.IndexOf(rest, "--");
    var opts = split < 0 ? rest : rest[..split];
    string[] claudeArgs = split < 0 ? [] : rest[(split + 1)..];
    var unknown = opts.Where((o, i) => o != "--mode" && (i == 0 || opts[i - 1] != "--mode")).ToList();
    if (unknown.Count > 0)
    {
        throw new ArgumentException($"Unknown option '{unknown[0]}'. Put claude arguments after --.");
    }

    var mode = StringOption(opts, "--mode") ?? ClaudeCommand.DefaultPermissionMode;
    var projectDir = WslPath.ToLinux(dir);

    await tmux.StartAsync(id, projectDir, ClaudeCommand.Build(mode, claudeArgs));
    if (sessions is not null)
    {
        // Sessions started by sb get no hub token: they have no MCP tools that call the hub.
        await sessions.MarkStartingAsync(id, projectDir, tmux.DisplayTarget(id), SessionRoles.Worker, tokenHash: null, status: "running");
    }
    else
    {
        WarnNoDatabase();
    }

    Console.WriteLine($"Started '{id}' in {projectDir} (permission mode: {mode})");
    Console.WriteLine($"If the folder is new to Claude Code, answer the trust dialog with: sb trust {id}");
    Console.WriteLine($"Watch it live: tmux attach -t {tmux.SessionName}  (Ctrl-b w picks a window, Ctrl-b d detaches)");
    return 0;
}

async Task<int> Trust(string id, string[] opts)
{
    var timeout = TimeSpan.FromSeconds(IntOption(opts, "--timeout") ?? 30);
    var result = await new DialogConfirmer(tmux).ConfirmAsync(id, ClaudeDialog.FolderTrust, timeout);
    Console.WriteLine(result == ConfirmResult.Confirmed
        ? "Trust dialog was on screen: chose \"Yes, I trust this folder\"."
        : "No trust dialog: Claude Code is already at its prompt. Pressed nothing.");
    return 0;
}

async Task<int> Send(string id, string[] words)
{
    // `sb send demo -` reads the message from stdin, which is handy for multi-line text.
    var text = (words is ["-"] ? await Console.In.ReadToEndAsync() : string.Join(' ', words)).TrimEnd('\n');
    if (messages is null)
    {
        WarnNoDatabase();
        await tmux.SendTextAsync(id, text);
        return 0;
    }

    // Save first (delivered_at = null), deliver, then mark delivered. If tmux fails, the row
    // stays and shows the message never arrived.
    var message = await messages.AddAsync(from: null, to: id, text);
    await tmux.SendTextAsync(id, text);
    await messages.MarkDeliveredAsync(message.Id);
    return 0;
}

async Task<int> Keys(string id, string[] keys)
{
    await tmux.SendKeysAsync(id, keys);
    return 0;
}

async Task<int> Read(string id, string[] opts)
{
    var lines = IntOption(opts, "--lines") ?? 0;
    Console.WriteLine(await tmux.ReadAsync(id, lines));
    return 0;
}

async Task<int> Wait(string id, string text, string[] opts)
{
    var timeout = TimeSpan.FromSeconds(IntOption(opts, "--timeout") ?? 30);
    if (await tmux.WaitForTextAsync(id, text, timeout))
    {
        Console.WriteLine($"Found \"{text}\" on screen.");
        return 0;
    }

    Console.Error.WriteLine($"Timed out after {timeout.TotalSeconds:0}s: \"{text}\" is not on screen.");
    return 1;
}

async Task<int> List()
{
    var windows = await tmux.ListAsync();
    if (windows.Count == 0)
    {
        Console.WriteLine("No sessions running.");
        return 0;
    }

    Console.WriteLine($"{"ID",-20} {"STATE",-12} {"IDLE",-8} DIR");
    foreach (var w in windows)
    {
        var state = w.Dead ? $"exited({w.ExitStatus})" : "running";
        var idle = DateTimeOffset.UtcNow - w.LastActivity;
        Console.WriteLine($"{w.Id,-20} {state,-12} {FormatIdle(idle),-8} {w.CurrentPath}");
    }

    return 0;
}

async Task<int> Stop(string id, string[] opts)
{
    var grace = opts.Contains("--force") ? TimeSpan.Zero : TimeSpan.FromSeconds(5);
    await tmux.StopAsync(id, grace);
    if (sessions is not null)
    {
        await sessions.MarkStoppedAsync(id);
    }

    Console.WriteLine($"Stopped '{id}'.");
    return 0;
}

async Task<int> Migrate()
{
    var applied = await Migrator.MigrateAsync(RequireDatabase());
    Console.WriteLine(applied.Count == 0
        ? "Database is up to date."
        : $"Applied: {string.Join(", ", applied)}");
    return 0;
}

async Task<int> Log(string id, string[] opts)
{
    SessionId.Validate(id);
    RequireDatabase();
    var history = await messages!.HistoryAsync(id, IntOption(opts, "--limit") ?? 20);
    if (history.Count == 0)
    {
        Console.WriteLine($"No messages for '{id}'.");
    }

    foreach (var m in history)
    {
        PrintMessage(m);
    }

    return 0;
}

async Task<int> Search(string[] words)
{
    // `sb search <words...> [--session id] [--limit N]`: options may come anywhere.
    var session = StringOption(words, "--session");
    var limit = IntOption(words, "--limit") ?? 10;
    var query = string.Join(' ', words.Where((w, i) =>
        w is not ("--session" or "--limit") && (i == 0 || words[i - 1] is not ("--session" or "--limit"))));
    if (query.Length == 0)
    {
        throw new ArgumentException("Give some words to search for.");
    }

    if (session is not null)
    {
        SessionId.Validate(session);
    }

    RequireDatabase();
    var found = await messages!.SearchAsync(query, session, limit);
    Console.WriteLine(found.Count == 0 ? $"No messages match \"{query}\"." : $"{found.Count} match(es), best first:");
    foreach (var m in found)
    {
        PrintMessage(m);
    }

    return 0;
}

NpgsqlDataSource RequireDatabase() =>
    db ?? throw new ArgumentException("SWITCHBOARD_DB is not set. Run scripts/setup-env.sh, then use ./sb (it loads .env).");

static void PrintMessage(Message m)
{
    var from = m.From ?? "you";
    var to = m.To ?? "you";
    var when = m.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    var state = m.DeliveredAt is null ? "  [not delivered]" : "";
    Console.WriteLine($"#{m.Id}  {when}  {from} → {to}{state}");
    foreach (var line in m.Body.Split('\n'))
    {
        Console.WriteLine($"    {line}");
    }
}

static void WarnNoDatabase() =>
    Console.Error.WriteLine("note: SWITCHBOARD_DB is not set, so nothing is saved to Postgres.");

static string? StringOption(string[] opts, string name)
{
    var i = Array.IndexOf(opts, name);
    if (i < 0)
    {
        return null;
    }

    return i + 1 < opts.Length ? opts[i + 1] : throw new ArgumentException($"{name} needs a value.");
}

static int? IntOption(string[] opts, string name)
{
    var i = Array.IndexOf(opts, name);
    if (i < 0)
    {
        return null;
    }

    if (i + 1 >= opts.Length || !int.TryParse(opts[i + 1], CultureInfo.InvariantCulture, out var value) || value < 0)
    {
        throw new ArgumentException($"{name} needs a whole number.");
    }

    return value;
}

static string FormatIdle(TimeSpan t) =>
    t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds}s"
    : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m"
    : $"{(int)t.TotalHours}h";

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          sb start <id> <project-dir> [--mode M] [-- <claude args>]
                                                           start Claude Code in a new tmux window
                                                           M: acceptEdits (default), auto, plan,
                                                              manual, dontAsk, bypassPermissions
          sb trust <id> [--timeout S]                      answer the folder trust dialog, only if it is on screen
          sb send  <id> <text...>                          type a message and press Enter
          sb send  <id> -                                  same, message read from stdin
          sb keys  <id> <key...>                           press raw keys (careful: Up/Down at the prompt
                                                           walk through message history)
          sb read  <id> [--lines N]                        print the screen (+ N lines of scrollback)
          sb wait  <id> <text> [--timeout S]               wait until text is on screen (default 30s)
          sb list                                          list sessions
          sb stop  <id> [--force]                          /exit, then close the window

        Postgres (needs SWITCHBOARD_DB, which ./sb loads from .env):
          sb migrate                                       create or update the tables
          sb log    <id> [--limit N]                       messages sent to or by a session (default 20)
          sb search <words...> [--session id] [--limit N]  search all messages (default 10)

        Session ids: lowercase letters, digits and hyphens. Quote Windows paths: 'C:\Users\me\proj'.
        """);
    return 2;
}

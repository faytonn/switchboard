using System.Globalization;
using System.Text;
using System.Text.Json;
using Switchboard.Cli;
using Switchboard.Core;
using Switchboard.Core.Claude;
using Switchboard.Core.Security;
using Switchboard.Core.Tmux;

// `sb` is the owner's command-line client. Session commands go through the hub's API (with
// the admin token), so every session gets a channel, and every message is saved and delivered
// the same way, whether it comes from sb, the web UI or the manager.
// The "debug" commands still talk to tmux directly, for looking at a stuck session.

try
{
    return args switch
    {
        ["start", var id, var dir, .. var opts] => await Start(id, dir, opts),
        ["send", var id, .. var words] when words.Length > 0 => await Send(id, words),
        ["read", var id, .. var opts] => await Read(id, opts),
        ["list", .. var opts] => await List(opts),
        ["stop", var id, .. var opts] => await Stop(id, opts),
        ["log", var id, .. var opts] => await Log(id, opts),
        ["search", .. var words] when words.Length > 0 => await Search(words),
        ["passwd"] => Passwd(),
        ["trust", var id, .. var opts] => await Trust(id, opts),
        ["keys", var id, .. var keys] when keys.Length > 0 => await Keys(id, keys),
        ["wait", var id, var text, .. var opts] => await Wait(id, text, opts),
        _ => Usage(),
    };
}
catch (Exception e) when (e is CliException or TmuxException or ArgumentException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static async Task<int> Start(string id, string dir, string[] opts)
{
    RejectUnknown(opts, valueOptions: ["--mode"], flags: ["--manager"]);
    SessionId.Validate(id);
    var mode = StringOption(opts, "--mode");
    var manager = opts.Contains("--manager");
    using var hub = new HubApi();

    Console.Error.WriteLine($"Starting '{id}' (this waits until its channel is connected)...");
    var result = await hub.PostAsync("api/sessions", new { id, projectDir = dir, permissionMode = mode, manager });
    var session = result.GetProperty("session");
    var dialogs = result.GetProperty("dialogs").EnumerateArray().Select(d => d.GetString()).ToList();
    Console.WriteLine($"Started {session.GetProperty("role").GetString()} '{id}' in {session.GetProperty("projectDir").GetString()}, channel connected.");
    if (dialogs.Count > 0)
    {
        Console.WriteLine($"Answered: {string.Join(", ", dialogs)}");
    }

    Console.WriteLine("Watch it live: tmux attach -t switchboard  (Ctrl-b w picks a window, Ctrl-b d detaches)");
    return 0;
}

static async Task<int> Send(string id, string[] words)
{
    // `sb send demo -` reads the message from stdin, which is handy for multi-line text.
    var text = (words is ["-"] ? await Console.In.ReadToEndAsync() : string.Join(' ', words)).TrimEnd('\n');
    using var hub = new HubApi();
    var result = await hub.PostAsync($"api/sessions/{id}/messages", new { text });
    var messageId = result.GetProperty("messageId").GetInt64();
    Console.WriteLine(result.GetProperty("delivered").GetBoolean()
        ? $"#{messageId} delivered to '{id}'. Replies show up in: ./sb log {id}"
        : $"#{messageId} queued: '{id}' has no channel connection; it is delivered when it reconnects.");
    return 0;
}

static async Task<int> Read(string id, string[] opts)
{
    var lines = IntOption(opts, "--lines") ?? 0;
    using var hub = new HubApi();
    var result = await hub.GetAsync($"api/sessions/{id}/screen?scrollback={lines}");
    Console.WriteLine(result.GetProperty("screen").GetString());
    return 0;
}

static async Task<int> List(string[] opts)
{
    var all = opts.Contains("--all");
    using var hub = new HubApi();
    var sessions = (await hub.GetAsync("api/sessions")).EnumerateArray()
        .Where(s => all || s.GetProperty("status").GetString() != "stopped")
        .ToList();
    if (sessions.Count == 0)
    {
        Console.WriteLine(all ? "No sessions yet." : "No sessions running. (--all shows stopped ones)");
        return 0;
    }

    Console.WriteLine($"{"ID",-18} {"ROLE",-8} {"STATUS",-9} {"CHANNEL",-8} {"IDLE",-6} DIR");
    foreach (var s in sessions)
    {
        var idle = s.GetProperty("idleSeconds") is { ValueKind: JsonValueKind.Number } i ? FormatIdle(i.GetInt32()) : "-";
        var channel = s.GetProperty("channel").GetBoolean() ? "on" : "off";
        Console.WriteLine($"{s.GetProperty("id").GetString(),-18} {s.GetProperty("role").GetString(),-8} {s.GetProperty("status").GetString(),-9} {channel,-8} {idle,-6} {s.GetProperty("projectDir").GetString()}");
    }

    return 0;
}

static async Task<int> Stop(string id, string[] opts)
{
    using var hub = new HubApi();
    await hub.PostAsync($"api/sessions/{id}/stop", new { force = opts.Contains("--force") });
    Console.WriteLine($"Stopped '{id}'.");
    return 0;
}

static async Task<int> Log(string id, string[] opts)
{
    var limit = IntOption(opts, "--limit") ?? 20;
    using var hub = new HubApi();
    var history = (await hub.GetAsync($"api/sessions/{id}/messages?limit={limit}")).EnumerateArray().ToList();
    if (history.Count == 0)
    {
        Console.WriteLine($"No messages for '{id}'.");
    }

    history.ForEach(PrintMessage);
    return 0;
}

static async Task<int> Search(string[] words)
{
    // `sb search <words...> [--session id] [--limit N]`: options may come anywhere.
    var session = StringOption(words, "--session");
    var limit = IntOption(words, "--limit") ?? 10;
    var query = string.Join(' ', words.Where((w, i) =>
        w is not ("--session" or "--limit") && (i == 0 || words[i - 1] is not ("--session" or "--limit"))));

    using var hub = new HubApi();
    var path = $"api/messages/search?q={Uri.EscapeDataString(query)}&limit={limit}" +
               (session is null ? "" : $"&session={Uri.EscapeDataString(session)}");
    var found = (await hub.GetAsync(path)).EnumerateArray().ToList();
    Console.WriteLine(found.Count == 0 ? $"No messages match \"{query}\"." : $"{found.Count} match(es), best first:");
    found.ForEach(PrintMessage);
    return 0;
}

// Sets the web UI login password: asks for it twice without echo and stores only its hash
// in .env. The hub reads it at startup, so restart the hub afterwards.
static int Passwd()
{
    var envFile = Environment.GetEnvironmentVariable("SWITCHBOARD_ENV_FILE")
        ?? throw new CliException("Run this through ./sb, which knows where .env is.");

    var first = ReadSecret("New web UI password: ");
    if (first.Length < 12)
    {
        throw new CliException("Use at least 12 characters. A long passphrase is easier to type on a phone than a short complicated password.");
    }

    if (ReadSecret("Repeat it: ") != first)
    {
        throw new CliException("The two passwords differ. Nothing changed.");
    }

    Console.Error.WriteLine("Hashing (this takes a moment on purpose)...");
    // Single quotes keep bash and Docker Compose from reading anything in the value.
    var line = $"SWITCHBOARD_OWNER_PASSWORD_HASH='{PasswordHash.Create(first)}'";
    var lines = File.ReadAllLines(envFile).Where(l => !l.StartsWith("SWITCHBOARD_OWNER_PASSWORD_HASH=", StringComparison.Ordinal)).ToList();
    lines.Add(line);
    File.WriteAllLines(envFile, lines);
    Console.WriteLine("Saved the password hash in .env. Restart the hub to use it; this also signs out every browser.");
    return 0;
}

static string ReadSecret(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? "";
    }

    // Read key by key with intercept: true, so the password never appears on screen.
    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return text.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
            {
                text.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            text.Append(key.KeyChar);
        }
    }
}

// --- Debug commands: straight to tmux, for looking at or unsticking a session by hand. ---

static async Task<int> Trust(string id, string[] opts)
{
    var timeout = TimeSpan.FromSeconds(IntOption(opts, "--timeout") ?? 30);
    var result = await new DialogConfirmer(TmuxClient.FromEnvironment()).ConfirmAsync(id, ClaudeDialog.FolderTrust, timeout);
    Console.WriteLine(result == ConfirmResult.Confirmed
        ? "Trust dialog was on screen: chose \"Yes, I trust this folder\"."
        : "No trust dialog: Claude Code is already at its prompt. Pressed nothing.");
    return 0;
}

static async Task<int> Keys(string id, string[] keys)
{
    await TmuxClient.FromEnvironment().SendKeysAsync(id, keys);
    return 0;
}

static async Task<int> Wait(string id, string text, string[] opts)
{
    var timeout = TimeSpan.FromSeconds(IntOption(opts, "--timeout") ?? 30);
    if (await TmuxClient.FromEnvironment().WaitForTextAsync(id, text, timeout))
    {
        Console.WriteLine($"Found \"{text}\" on screen.");
        return 0;
    }

    Console.Error.WriteLine($"Timed out after {timeout.TotalSeconds:0}s: \"{text}\" is not on screen.");
    return 1;
}

// --- Helpers ---

static void PrintMessage(JsonElement m)
{
    var from = m.GetProperty("from").GetString() ?? "you";
    var to = m.GetProperty("to").GetString() ?? "you";
    var when = m.GetProperty("createdAt").GetDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    var state = m.GetProperty("deliveredAt").ValueKind == JsonValueKind.Null ? "  [not delivered]" : "";
    Console.WriteLine($"#{m.GetProperty("id").GetInt64()}  {when}  {from} → {to}{state}");
    foreach (var line in (m.GetProperty("body").GetString() ?? "").Split('\n'))
    {
        Console.WriteLine($"    {line}");
    }
}

static void RejectUnknown(string[] opts, string[] valueOptions, string[] flags)
{
    for (var i = 0; i < opts.Length; i++)
    {
        if (valueOptions.Contains(opts[i]))
        {
            i++;
        }
        else if (!flags.Contains(opts[i]))
        {
            throw new ArgumentException($"Unknown option '{opts[i]}'.");
        }
    }
}

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

static string FormatIdle(int seconds) =>
    seconds < 60 ? $"{seconds}s" : seconds < 3600 ? $"{seconds / 60}m" : $"{seconds / 3600}h";

static int Usage()
{
    Console.Error.WriteLine("""
        Usage (session commands go through the hub: start it with scripts/run-hub.sh):
          sb start  <id> <project-dir> [--mode M] [--manager]
                                       start Claude Code with its channel; waits until connected
                                       M: acceptEdits (default), auto, plan, manual, dontAsk,
                                          bypassPermissions. --manager adds the manager tools.
          sb send   <id> <text...>     send a message through the session's channel
          sb send   <id> -             same, message read from stdin
          sb read   <id> [--lines N]   print the session's screen (+ N lines of scrollback)
          sb list   [--all]            list sessions (--all includes stopped ones)
          sb stop   <id> [--force]     /exit, then close the window
          sb log    <id> [--limit N]   messages sent to or by a session (default 20)
          sb search <words...> [--session id] [--limit N]
                                       search all messages (default 10)
          sb passwd                    set the web UI login password (stored hashed in .env)

        Debug (straight to tmux, for a stuck session):
          sb trust <id> [--timeout S]  answer the folder trust dialog, only if it is on screen
          sb keys  <id> <key...>       press raw keys (careful: Up/Down at the prompt walk
                                       through message history)
          sb wait  <id> <text> [--timeout S]  wait until text is on screen (default 30s)

        Session ids: lowercase letters, digits and hyphens. Quote Windows paths: 'C:\Users\me\proj'.
        """);
    return 2;
}

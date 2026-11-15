using System.Globalization;
using Switchboard.Core;
using Switchboard.Core.Tmux;

// `sb` is a small command-line tool for driving sessions by hand. Phase 1 uses it to test
// tmux control. Later phases reuse the same TmuxClient from the hub.

var tmux = TmuxClient.FromEnvironment();

try
{
    return args switch
    {
        ["start", var id, var dir, .. var rest] => await Start(id, dir, rest),
        ["send", var id, .. var words] when words.Length > 0 => await Send(id, words),
        ["keys", var id, .. var keys] when keys.Length > 0 => await Keys(id, keys),
        ["read", var id, .. var opts] => await Read(id, opts),
        ["wait", var id, var text, .. var opts] => await Wait(id, text, opts),
        ["list"] => await List(),
        ["stop", var id, .. var opts] => await Stop(id, opts),
        _ => Usage(),
    };
}
catch (Exception e) when (e is TmuxException or ArgumentException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

async Task<int> Start(string id, string dir, string[] rest)
{
    // Everything after "--" is passed to claude, e.g. `sb start demo ~/proj -- --model sonnet`.
    var claudeArgs = rest is ["--", .. var after] ? after : rest;
    var projectDir = WslPath.ToLinux(dir);

    await tmux.StartAsync(id, projectDir, ["claude", .. claudeArgs]);
    Console.WriteLine($"Started '{id}' in {projectDir}");
    Console.WriteLine($"Watch it live: tmux attach -t {tmux.SessionName}  (Ctrl-b w picks a window, Ctrl-b d detaches)");
    return 0;
}

async Task<int> Send(string id, string[] words)
{
    // `sb send demo -` reads the message from stdin, which is handy for multi-line text.
    var text = words is ["-"] ? await Console.In.ReadToEndAsync() : string.Join(' ', words);
    await tmux.SendTextAsync(id, text.TrimEnd('\n'));
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
    Console.WriteLine($"Stopped '{id}'.");
    return 0;
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
          sb start <id> <project-dir> [-- <claude args>]   start Claude Code in a new tmux window
          sb send  <id> <text...>                          type a message and press Enter
          sb send  <id> -                                  same, message read from stdin
          sb keys  <id> <key...>                           press keys: Enter, Escape, Up, Down, C-c, y
          sb read  <id> [--lines N]                        print the screen (+ N lines of scrollback)
          sb wait  <id> <text> [--timeout S]               wait until text is on screen (default 30s)
          sb list                                          list sessions
          sb stop  <id> [--force]                          /exit, then close the window

        Session ids: lowercase letters, digits and hyphens. Quote Windows paths: 'C:\Users\me\proj'.
        """);
    return 2;
}

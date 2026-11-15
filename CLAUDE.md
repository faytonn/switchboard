# Switchboard

A manager Claude Code session starts and controls other Claude Code sessions in tmux.
The owner talks to any session from a browser or phone. Messages reach sessions through
Claude Code channels, and every message is stored in Postgres.

## Rules

- Commits: never add `Co-Authored-By` lines, "Generated with Claude Code", or any other
  Claude/AI attribution to commit messages, PR bodies, or code comments.
- Work one phase at a time. After each phase, show how to run and test it, then stop and
  wait for the owner.
- Ask before adding a NuGet package that is not already in the plan (`Npgsql`,
  `ModelContextProtocol`).
- Keep it simple: plain HTML/CSS/JS for the web UI, no frontend build step.

## Environment

- The code lives on Windows (`C:\Users\fayton\Desktop\switchboard`), but it builds and runs
  in WSL Ubuntu (`/mnt/c/Users/fayton/Desktop/switchboard`). tmux and the sessions live there.
- Build only from WSL. Building the same folder from Windows and WSL breaks `obj/`, because
  each side writes its own absolute paths into `project.assets.json`.
- The .NET SDK in WSL is user-local: `~/.dotnet/dotnet` (set `DOTNET_ROOT=~/.dotnet`).
- Postgres runs in Docker on port 5433. Port 5432 is taken by a native Windows Postgres 18.

## Starting sessions

- Always pass `--permission-mode` to claude (`ClaudeCommand.Build`), so sessions never inherit
  the owner's own `defaultMode` from settings (it is `auto` in WSL). Default: `acceptEdits`.
  `sb start --mode <mode>` overrides it.
- Never press dialog keys blindly. At the normal prompt, Up/Down walk through message history
  and Enter sends what came up (that is how a stray `Down Enter` once sent `/exit`). Use
  `DialogConfirmer`: it waits until the dialog or the prompt is on screen, checks markers only
  the dialog shows, moves to the option while checking after each key, and presses nothing if
  Claude Code is already at its prompt. `sb trust <id>` does this for the folder trust dialog,
  and the manager must use it instead of raw keys.

## Channels (research preview)

- Custom channel servers need `claude --dangerously-load-development-channels server:<name>`.
  This shows a warning dialog on every launch. Answer it with `DialogConfirmer` and a
  `ClaudeDialog` for the warning, never with raw keys.
- Set `MCP_PROTOCOL_NEGOTIATION=legacy` for the channel server, or Claude Code may not register it
  as a channel.
- MCP stdio servers must write logs to stderr only. stdout is the protocol.
- Docs: https://code.claude.com/docs/en/channels and https://code.claude.com/docs/en/channels-reference

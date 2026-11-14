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

## Channels (research preview)

- Custom channel servers need `claude --dangerously-load-development-channels server:<name>`.
  This shows a warning dialog on every launch. Before auto-confirming it through tmux,
  capture the pane and check that the warning text is actually on screen.
- Set `MCP_PROTOCOL_NEGOTIATION=legacy` for the channel server, or Claude Code may not register it
  as a channel.
- MCP stdio servers must write logs to stderr only. stdout is the protocol.
- Docs: https://code.claude.com/docs/en/channels and https://code.claude.com/docs/en/channels-reference

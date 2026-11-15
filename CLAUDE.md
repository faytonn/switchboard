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

## Hub

- Run it with `scripts/run-hub.sh` (builds the solution, loads `.env`, listens on
  `SWITCHBOARD_HUB_URL`, default `http://127.0.0.1:7700`). It applies migrations on startup.
- The hub owns sessions. `sb` is a hub client (admin token from `.env`) for start, send, read,
  list, stop, log and search, so there is one way to start sessions and deliver messages.
  Only `sb trust|keys|wait` talk to tmux directly, for debugging a stuck session.
- Auth: every `/api` call needs `Authorization: Bearer <token>`. The admin token
  (`SWITCHBOARD_ADMIN_TOKEN`) is the owner. Each session gets a fresh token per start, stored
  only as a SHA-256 hash in `sessions.token_hash`, and revoked on stop. The caller's identity
  comes from the token, never from the request body, so `from_session` can be trusted.
- Roles: owner and manager can start/send/read/list/stop. Only the owner can start or stop a
  manager. Workers have no API rights yet. A session can't send to or stop itself.
- Manager sessions get `--mcp-config ~/.switchboard/sessions/<id>.mcp.json` (mode 600, holds
  the token) and `--allowedTools mcp__switchboard-manager`. Start one with
  `scripts/start-manager.sh [dir]`.
- Never change an applied migration. Add a new numbered file in `Core/Data/Migrations`.

## Channels (research preview)

- Every hub-started session runs `Switchboard.Channel` (MCP server `switchboard-channel`),
  launched with `--dangerously-load-development-channels server:switchboard-channel`. The
  warning it shows is answered by `DialogConfirmer.ConfirmAllAsync` with
  `ClaudeDialog.DevChannels`, which also requires the `Channels:` line to list exactly our
  channel. Never answer it with raw keys.
- Facts verified on Claude Code 2.1.325 (phase 4 test):
  - The server must pin `ProtocolVersion = "2025-11-25"`. Revision 2026-07-28 has no initialize
    handshake, so there is no session to push into and the channel never registers.
    `MCP_PROTOCOL_NEGOTIATION` is not needed.
  - The capability value must be a `JsonElement` (`{}`); an anonymous object makes `initialize`
    fail with a bare `-32603 An error occurred.` Options passed to `SendNotificationAsync`
    need an explicit `TypeInfoResolver`.
  - Servers from `--mcp-config` work as channels. The startup banner "no MCP server configured
    with that name" is wrong; `--debug` logs "Channel notifications registered".
  - A channel event wakes an idle session and starts a turn.
- Delivery: messages are saved first, then the hub's per-connection loop sends every
  undelivered row from Postgres in id order; the channel server acks after pushing, and the ack
  sets `delivered_at`. Offline sessions get their queue on reconnect. At least once: a rare
  duplicate is possible after a dropped connection.
- Replies go out through the `reply` tool (HTTP `POST /api/channel/reply`): to `owner`, or to a
  session that has messaged the caller.
- MCP stdio servers must write logs to stderr only. stdout is the protocol.

## Web UI

- Static files in `src/Switchboard.Hub/wwwroot` (plain HTML/CSS/JS, no build step). Put session
  text on the page with `textContent` only, never `innerHTML`. The CSP allows no inline scripts
  or styles, so don't add `<script>` blocks or `style="..."` attributes.
- Owner login: `./sb passwd` stores a PBKDF2 hash in `.env` (`SWITCHBOARD_OWNER_PASSWORD_HASH`);
  restart the hub after. Cookie `sb_auth`: HttpOnly, SameSite=Strict, 12h sliding, or 30 days
  with "remember me". A password change invalidates all cookies (stamp claim). Login is
  rate-limited to 5 tries a minute per address.
- Cookie-authenticated writes need the `X-Switchboard` header (CSRF guard); bearer calls don't.
- Live updates: `GET /api/events` (SSE): `hello`, `message`, `delivered`, `sessions`, `ping`.
  Events are hints; the page reloads from the API on every (re)connect.
- Phone access: `tailscale serve` on Windows proxies to `127.0.0.1:7700`, which WSL's
  localhost forwarding passes to the hub. The hub stays bound to loopback. Never use
  `tailscale funnel` (that would put it on the public internet).
- Docs: https://code.claude.com/docs/en/channels and https://code.claude.com/docs/en/channels-reference

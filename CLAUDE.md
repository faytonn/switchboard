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

## Session launch options

- Every session starts with `--model`, `--effort` and, unless `connectors` is on,
  `--strict-mcp-config`. Defaults: manager `sonnet`/`medium` (it mostly routes), workers
  `opus`/`medium`. Only the owner can turn on `connectors` (they reach Gmail, Notion, GitHub).
- Benchmark (2026-11-15, send to reply saved, 3 messages): opus/medium 5.3/3.5/4.9s,
  sonnet/medium 2.8/1.5/1.7s, haiku/low 5.6/2.8/2.8s. Hub delivery itself is 0.00-0.03s.
  Connectors on/off made no measurable difference to replies, only to start time (5.1s vs 2.7s).
- Sonnet and Haiku answered short messages in the terminal instead of calling `reply`, despite
  the server instructions. The channel server therefore appends a one-line reminder to every
  pushed message ("Answer with the reply tool, to=..."). Keep it.

## Permission relay

- The channel server also declares `claude/channel/permission`. Claude Code then sends it
  `notifications/claude/channel/permission_request` (`request_id` = 5 letters `[a-km-z]`,
  `tool_name`, `description`, `input_preview` = tool arguments as JSON text). The channel
  server posts it to `POST /api/channel/permission-request`; the hub stores it in
  `permission_requests` and pushes an SSE `permission` event.
- Only the owner answers (`POST /api/sessions/{id}/permissions/{requestId}`, behavior
  allow|deny|dismiss), never the manager. The hub claims the decision with a conditional
  UPDATE (first answer wins), sends `{type:"permission"}` down the WebSocket, and the channel
  server emits `notifications/claude/channel/permission`. If the channel is offline the
  decision is undone and the owner gets 409.
- The terminal prompt stays open in parallel; whichever answer comes first wins, and Claude
  Code drops the other silently. That's why the UI has Dismiss. Stopping or restarting a
  session expires its waiting prompts.
- Safe read-only commands (like `date`) never prompt, so test with something that writes
  outside the project (e.g. `touch /tmp/x`).

## Web UI

- Static files in `src/Switchboard.Hub/wwwroot` (plain HTML/CSS/JS, no build step). Put session
  text on the page with `textContent` only, never `innerHTML`. The CSP allows no inline scripts
  or styles, so don't add `<script>` blocks or `style="..."` attributes.
- Owner login: `./sb passwd` stores a PBKDF2 hash in `.env` (`SWITCHBOARD_OWNER_PASSWORD_HASH`);
  restart the hub after. Cookie `sb_auth`: HttpOnly, SameSite=Strict, 12h sliding, or 30 days
  with "remember me". A password change invalidates all cookies (stamp claim). Login is
  rate-limited to 5 tries a minute per address.
- Cookie-authenticated writes need the `X-Switchboard` header (CSRF guard); bearer calls don't.
- Live updates: `GET /api/events` (SSE): `hello`, `message`, `delivered`, `sessions`,
  `activity`, `permission`, `permission-decided`, `ping`. Events are hints; the page reloads
  from the API on every (re)connect.
- `ActivityMonitor` reads each running session's screen every 2s: `working` when Claude Code's
  footer says "esc to interrupt", `approval` when a "Do you want to…" prompt is open (also
  catches sessions whose channel server can't relay prompts), else `idle`.
- Session tiles: a Lucide icon (or initials) on one of 8 palette colours (`.c0`-`.c7`), stored
  in `sessions.icon` / `sessions.color` (null colour = hash of the id). Icons come from
  `wwwroot/icons/lucide.svg`, a sprite built by `scripts/build-icon-sprite.py` from
  `scripts/icons.txt` (lucide-static 1.73.0, ISC, licence in `wwwroot/icons/`). The hub only
  accepts icon names that are symbols in that sprite. Without an icon on start, `IconGuesser`
  picks one from the folder name and README; a restart never overwrites a stored icon.
  Never set colours with style attributes (CSP).
- Radius: `--r` 6px for bubbles, tiles, cards, inputs, buttons; `--r-sm` 2px for the bubble
  corner nearest its sender.
- Tests against the test hub (7701): call the API with curl, never `./sb`. `./sb` loads `.env`,
  which points at the owner's real hub on 7700.
- UI screenshots: run a test hub on port 7701 against the `switchboard_test` database with the
  `sb-test` tmux session, and drive headless Edge over CDP from Windows node.
- Phone access: `tailscale serve` on Windows proxies to `127.0.0.1:7700`, which WSL's
  localhost forwarding passes to the hub. The hub stays bound to loopback. Never use
  `tailscale funnel` (that would put it on the public internet).
- Docs: https://code.claude.com/docs/en/channels and https://code.claude.com/docs/en/channels-reference

-- 006_session_launch: how each session's Claude Code was started, shown in the UI.
alter table sessions
    add column model text,                                  -- e.g. 'opus', 'sonnet'
    add column effort text check (effort in ('low', 'medium', 'high', 'xhigh', 'max')),
    -- true: the session also loads the owner's claude.ai connectors and plugin MCP servers.
    -- false (the default): only Switchboard's own servers (--strict-mcp-config), which keeps
    -- every turn's context small and fast.
    add column connectors boolean not null default false;

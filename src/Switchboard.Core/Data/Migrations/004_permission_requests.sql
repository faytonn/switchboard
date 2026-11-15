-- 004_permission_requests: tool approvals that sessions ask the owner for (Bash, Write, ...).
-- Claude Code relays its permission prompt through the channel; the owner answers in the UI.

create table permission_requests (
    id            bigserial primary key,
    session_id    text not null references sessions (id),
    -- Claude Code's id for the prompt: five lowercase letters, never "l" (looks like 1/I).
    request_id    text not null check (request_id ~ '^[a-km-z]{5}$'),
    tool_name     text not null,
    description   text not null,
    input_preview text not null,
    created_at    timestamptz not null default now(),
    decided_at    timestamptz,
    -- allow/deny: answered from Switchboard. dismissed: hidden by the owner (e.g. answered in
    -- the terminal). expired: the session stopped while it was waiting.
    decision      text check (decision in ('allow', 'deny', 'dismissed', 'expired')),
    unique (session_id, request_id)
);

-- Only waiting requests are ever looked up, so index only those (see 003 for the idea).
create index permission_requests_pending_idx on permission_requests (session_id) where decided_at is null;

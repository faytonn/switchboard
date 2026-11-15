-- 007_supervision: what the hub needs to restart sessions by itself.

-- 'crashed': the hub's supervisor gave up restarting the session after several failures in a
-- row (so a crash loop can't burn tokens). Only the owner restarts it from there.
alter table sessions drop constraint sessions_status_check;
alter table sessions add constraint sessions_status_check
    check (status in ('starting', 'running', 'stopped', 'crashed'));

alter table sessions
    -- Stored so a restart can use exactly the same settings.
    add column permission_mode text,
    -- When the session's current Claude Code conversation began. A restart with --continue
    -- keeps it; a fresh start resets it. The supervisor starts the manager fresh once this is
    -- a day old, so its context doesn't grow forever.
    add column conversation_started_at timestamptz;

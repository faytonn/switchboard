-- 001_init: sessions, and every message between the owner and sessions.

create table sessions (
    -- Same rule as SessionId.cs, checked again here so bad ids can't sneak in another way.
    id          text primary key check (id ~ '^[a-z0-9][a-z0-9-]{0,31}$'),
    project_dir text not null,
    tmux_target text not null,                                  -- e.g. 'switchboard:api-fix'
    status      text not null check (status in ('starting', 'running', 'stopped')),
    created_at  timestamptz not null default now(),             -- first time this id started
    started_at  timestamptz not null default now(),             -- latest start
    stopped_at  timestamptz
);

create table messages (
    id           bigserial primary key,
    -- null means the owner (the human). Session-to-session rows then still point at real
    -- sessions, so the foreign keys check both ends.
    from_session text references sessions (id),
    to_session   text references sessions (id),
    body         text not null,
    created_at   timestamptz not null default now(),
    -- null until the message reached the recipient. A message to a session that is offline
    -- stays null, and is delivered when the session comes back.
    delivered_at timestamptz,
    -- Full-text search vector, computed by Postgres from body on every insert/update.
    search       tsvector generated always as (to_tsvector('english', body)) stored,
    check (from_session is not null or to_session is not null)
);

-- History of one session = messages it sent or received, newest first.
create index messages_to_idx   on messages (to_session, id);
create index messages_from_idx on messages (from_session, id);
-- GIN indexes every word in the tsvector, so "search @@ query" doesn't scan every row.
create index messages_search_idx on messages using gin (search);

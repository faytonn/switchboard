-- 002_roles_tokens: what a session may do, and the token it uses to call the hub.
-- A new file rather than an edit to 001: 001 has already run on existing databases, and the
-- migrator never runs a file twice, so edits to it would be silently skipped there.

alter table sessions
    -- manager: may start, stop and message other sessions. worker: an ordinary session.
    add column role text not null default 'worker' check (role in ('manager', 'worker')),
    -- SHA-256 of the session's hub token. We store only the hash, so someone who reads the
    -- database can't use the tokens. A new token is issued on every start.
    add column token_hash bytea unique;

-- 005_session_appearance: each session's tile in the UI.
alter table sessions
    -- A Lucide icon name from wwwroot/icons/lucide.svg, e.g. 'ticket'. null = show initials.
    add column icon text check (icon ~ '^[a-z0-9-]{1,40}$'),
    -- One of the UI's 8 palette colours (.c0 ... .c7). null = picked from a hash of the id.
    add column color smallint check (color between 0 and 7);

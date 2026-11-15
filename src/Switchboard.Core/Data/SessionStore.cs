using Npgsql;

namespace Switchboard.Core.Data;

/// <summary>The sessions table: which session ids exist and whether they are running.</summary>
public sealed class SessionStore(NpgsqlDataSource db)
{
    /// <summary>
    /// Records that a session started. Starting an id that ran before reuses its row, so the
    /// new run keeps the old message history.
    /// </summary>
    public async Task MarkStartedAsync(string id, string projectDir, string tmuxTarget, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            insert into sessions (id, project_dir, tmux_target, status)
            values ($1, $2, $3, 'running')
            on conflict (id) do update
               set project_dir = excluded.project_dir,
                   tmux_target = excluded.tmux_target,
                   status      = 'running',
                   started_at  = now(),
                   stopped_at  = null
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = projectDir });
        cmd.Parameters.Add(new() { Value = tmuxTarget });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkStoppedAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "update sessions set status = 'stopped', stopped_at = now() where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

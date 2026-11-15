using Npgsql;
using NpgsqlTypes;

namespace Switchboard.Core.Data;

public static class SessionRoles
{
    public const string Manager = "manager";
    public const string Worker = "worker";
}

public sealed record SessionRow(
    string Id,
    string ProjectDir,
    string TmuxTarget,
    string Status,
    string Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt);

/// <summary>The sessions table: which session ids exist, their role, and whether they run.</summary>
public sealed class SessionStore(NpgsqlDataSource db)
{
    private const string Columns = "id, project_dir, tmux_target, status, role, created_at, started_at, stopped_at";

    /// <summary>
    /// Records a (re)start. Starting an id that ran before reuses its row, so the new run keeps
    /// the old message history. The token hash is replaced, so the old run's token stops working.
    /// </summary>
    public async Task MarkStartingAsync(
        string id, string projectDir, string tmuxTarget, string role, byte[]? tokenHash,
        string status = "starting", CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            insert into sessions (id, project_dir, tmux_target, status, role, token_hash)
            values ($1, $2, $3, $4, $5, $6)
            on conflict (id) do update
               set project_dir = excluded.project_dir,
                   tmux_target = excluded.tmux_target,
                   status      = excluded.status,
                   role        = excluded.role,
                   token_hash  = excluded.token_hash,
                   started_at  = now(),
                   stopped_at  = null
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = projectDir });
        cmd.Parameters.Add(new() { Value = tmuxTarget });
        cmd.Parameters.Add(new() { Value = status });
        cmd.Parameters.Add(new() { Value = role });
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = (object?)tokenHash ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkRunningAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("update sessions set status = 'running' where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Marks a session stopped and revokes its token.</summary>
    public async Task MarkStoppedAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "update sessions set status = 'stopped', stopped_at = now(), token_hash = null where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionRow?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"select {Columns} from sessions where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        return (await ReadAsync(cmd, ct)).SingleOrDefault();
    }

    /// <summary>The session that owns this token hash, if it is not stopped.</summary>
    public async Task<SessionRow?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"select {Columns} from sessions where token_hash = $1 and status <> 'stopped'");
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = tokenHash });
        return (await ReadAsync(cmd, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<SessionRow>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"select {Columns} from sessions order by started_at desc");
        return await ReadAsync(cmd, ct);
    }

    private static async Task<List<SessionRow>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<SessionRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new SessionRow(
                Id: reader.GetString(0),
                ProjectDir: reader.GetString(1),
                TmuxTarget: reader.GetString(2),
                Status: reader.GetString(3),
                Role: reader.GetString(4),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(5),
                StartedAt: reader.GetFieldValue<DateTimeOffset>(6),
                StoppedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return list;
    }
}

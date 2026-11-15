using Npgsql;

namespace Switchboard.Core.Data;

public sealed record PermissionRequest(
    long Id,
    string SessionId,
    string RequestId,
    string ToolName,
    string Description,
    string InputPreview,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? Decision);

/// <summary>The permission_requests table: tool approvals waiting for the owner.</summary>
public sealed class PermissionStore(NpgsqlDataSource db)
{
    private const string Columns =
        "id, session_id, request_id, tool_name, description, input_preview, created_at, decided_at, decision";

    /// <summary>
    /// Saves a new request. Claude Code's 5-letter ids can repeat over a session's lifetime,
    /// so an old decided row with the same id is reset rather than causing a duplicate-key error.
    /// </summary>
    public async Task<PermissionRequest> AddAsync(
        string sessionId, string requestId, string toolName, string description, string inputPreview, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            insert into permission_requests (session_id, request_id, tool_name, description, input_preview)
            values ($1, $2, $3, $4, $5)
            on conflict (session_id, request_id) do update
               set tool_name = excluded.tool_name,
                   description = excluded.description,
                   input_preview = excluded.input_preview,
                   created_at = now(),
                   decided_at = null,
                   decision = null
            returning {Columns}
            """);
        cmd.Parameters.Add(new() { Value = sessionId });
        cmd.Parameters.Add(new() { Value = requestId });
        cmd.Parameters.Add(new() { Value = toolName });
        cmd.Parameters.Add(new() { Value = description });
        cmd.Parameters.Add(new() { Value = inputPreview });
        return (await ReadAsync(cmd, ct)).Single();
    }

    public async Task<IReadOnlyList<PermissionRequest>> PendingAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"select {Columns} from permission_requests where decided_at is null order by id");
        return await ReadAsync(cmd, ct);
    }

    /// <summary>
    /// Records a decision, but only if the request is still waiting. Two phones answering at
    /// once: the database lets exactly one UPDATE through, the other gets null.
    /// </summary>
    public async Task<PermissionRequest?> TryDecideAsync(string sessionId, string requestId, string decision, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            update permission_requests set decided_at = now(), decision = $3
            where session_id = $1 and request_id = $2 and decided_at is null
            returning {Columns}
            """);
        cmd.Parameters.Add(new() { Value = sessionId });
        cmd.Parameters.Add(new() { Value = requestId });
        cmd.Parameters.Add(new() { Value = decision });
        return (await ReadAsync(cmd, ct)).SingleOrDefault();
    }

    /// <summary>Puts a decision back to waiting (the answer couldn't be sent).</summary>
    public async Task UndoDecisionAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("update permission_requests set decided_at = null, decision = null where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A stopped session's prompts can't be answered any more.</summary>
    public async Task ExpireForSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "update permission_requests set decided_at = now(), decision = 'expired' where session_id = $1 and decided_at is null");
        cmd.Parameters.Add(new() { Value = sessionId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<PermissionRequest>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<PermissionRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PermissionRequest(
                Id: reader.GetInt64(0),
                SessionId: reader.GetString(1),
                RequestId: reader.GetString(2),
                ToolName: reader.GetString(3),
                Description: reader.GetString(4),
                InputPreview: reader.GetString(5),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(6),
                DecidedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                Decision: reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return list;
    }
}

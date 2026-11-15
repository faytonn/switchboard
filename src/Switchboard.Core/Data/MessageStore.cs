using Npgsql;
using NpgsqlTypes;

namespace Switchboard.Core.Data;

/// <summary>One message. <see cref="From"/> or <see cref="To"/> is null when it is the owner.</summary>
public sealed record Message(
    long Id,
    string? From,
    string? To,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

/// <summary>The messages table: logging, history and search.</summary>
public sealed class MessageStore(NpgsqlDataSource db)
{
    private const string Columns = "id, from_session, to_session, body, created_at, delivered_at";

    /// <summary>
    /// Saves a message as not yet delivered. Callers save first, deliver, then call
    /// <see cref="MarkDeliveredAsync"/>. If delivery fails, the row still exists and shows it
    /// never arrived, rather than the message vanishing.
    /// </summary>
    public async Task<Message> AddAsync(string? from, string? to, string body, CancellationToken ct = default) =>
        await AddAsync(from, to, body, deliveredNow: false, ct);

    /// <param name="deliveredNow">True when storing the message already is the delivery, as
    /// for messages to the owner, who reads them from the database (the web UI in phase 5).</param>
    public async Task<Message> AddAsync(string? from, string? to, string body, bool deliveredNow, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            insert into messages (from_session, to_session, body, delivered_at)
            values ($1, $2, $3, case when $4 then now() end)
            returning {Columns}
            """);
        cmd.Parameters.Add(NullableText(from));
        cmd.Parameters.Add(NullableText(to));
        cmd.Parameters.Add(new() { Value = body });
        cmd.Parameters.Add(new() { Value = deliveredNow });
        return (await ReadAsync(cmd, ct)).Single();
    }

    /// <summary>
    /// Messages to <paramref name="sessionId"/> that haven't been delivered, oldest first,
    /// skipping ids up to <paramref name="afterId"/> (already sent, waiting for an ack).
    /// </summary>
    public async Task<IReadOnlyList<Message>> UndeliveredAsync(string sessionId, long afterId, int limit, CancellationToken ct = default)
    {
        // Matches the partial index messages_undelivered_idx exactly, so this stays fast.
        await using var cmd = db.CreateCommand($"""
            select {Columns} from messages
            where to_session = $1 and delivered_at is null and id > $2
            order by id
            limit $3
            """);
        cmd.Parameters.Add(new() { Value = sessionId });
        cmd.Parameters.Add(new() { Value = afterId });
        cmd.Parameters.Add(new() { Value = limit });
        return await ReadAsync(cmd, ct);
    }

    /// <summary>Has <paramref name="from"/> ever sent a message to <paramref name="to"/>?</summary>
    public async Task<bool> HasMessagedAsync(string from, string to, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "select exists (select 1 from messages where from_session = $1 and to_session = $2)");
        cmd.Parameters.Add(new() { Value = from });
        cmd.Parameters.Add(new() { Value = to });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Sets delivered_at, unless already set (a resent message keeps its first time).</summary>
    public async Task MarkDeliveredAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "update messages set delivered_at = now() where id = $1 and delivered_at is null");
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The last <paramref name="limit"/> messages a session sent or received, oldest first.</summary>
    public async Task<IReadOnlyList<Message>> HistoryAsync(string sessionId, int limit, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            select {Columns} from messages
            where from_session = $1 or to_session = $1
            order by id desc
            limit $2
            """);
        cmd.Parameters.Add(new() { Value = sessionId });
        cmd.Parameters.Add(new() { Value = limit });
        var newestFirst = await ReadAsync(cmd, ct);
        return newestFirst.AsEnumerable().Reverse().ToList();
    }

    /// <summary>
    /// Finds messages matching <paramref name="query"/>, best matches first. Optionally only
    /// messages sent or received by <paramref name="sessionId"/>.
    /// </summary>
    public async Task<IReadOnlyList<Message>> SearchAsync(string query, string? sessionId, int limit, CancellationToken ct = default)
    {
        // Two ways to match, because each misses things the other finds:
        //  - Full-text search (search @@ q) understands language: "stopping" finds "stopped",
        //    word order doesn't matter, and websearch_to_tsquery accepts "quotes" and -minus.
        //    It is fast thanks to the GIN index.
        //  - ILIKE finds exact fragments that full-text search breaks apart or ignores, such
        //    as "TmuxClient.cs" or "5433". It reads every row, which is fine at the size of a
        //    personal message log.
        await using var cmd = db.CreateCommand($"""
            select {Columns}
            from messages, websearch_to_tsquery('english', $1) as q
            where (search @@ q or body ilike $2)
              and ($3::text is null or from_session = $3 or to_session = $3)
            order by ts_rank(search, q) desc, id desc
            limit $4
            """);
        cmd.Parameters.Add(new() { Value = query });
        cmd.Parameters.Add(new() { Value = $"%{EscapeLike(query)}%" });
        cmd.Parameters.Add(NullableText(sessionId));
        cmd.Parameters.Add(new() { Value = limit });
        return await ReadAsync(cmd, ct);
    }

    // In LIKE patterns % and _ are wildcards. Escape them (and the escape character itself)
    // so a search for "50%" means the text "50%".
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    // A null parameter has no type for Postgres to infer, so we state that it is text.
    private static NpgsqlParameter NullableText(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static async Task<List<Message>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<Message>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Message(
                Id: reader.GetInt64(0),
                From: reader.IsDBNull(1) ? null : reader.GetString(1),
                To: reader.IsDBNull(2) ? null : reader.GetString(2),
                Body: reader.GetString(3),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(4),
                DeliveredAt: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return list;
    }
}

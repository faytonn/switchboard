using Npgsql;

namespace Switchboard.Core.Data;

/// <summary>
/// Applies the SQL files in Data/Migrations in name order, each one once. The table
/// schema_migrations records which ones have run.
/// </summary>
public static class Migrator
{
    // pg_advisory_lock takes any 64-bit number as a lock name. Every Switchboard process uses
    // this one, so two processes starting at the same time can't both run a migration.
    private const long LockKey = 7_000_001;

    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await ExecAsync(conn, "select pg_advisory_lock($1)", ct, LockKey);
        try
        {
            await ExecAsync(conn, """
                create table if not exists schema_migrations (
                    version    text primary key,
                    applied_at timestamptz not null default now()
                )
                """, ct);

            var applied = new HashSet<string>();
            await using (var cmd = new NpgsqlCommand("select version from schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    applied.Add(reader.GetString(0));
                }
            }

            var appliedNow = new List<string>();
            foreach (var (version, sql) in LoadScripts().Where(s => !applied.Contains(s.Version)))
            {
                // One transaction per migration: if any statement fails, none of it sticks,
                // and the version isn't recorded, so the next run tries it again.
                await using var tx = await conn.BeginTransactionAsync(ct);
                await ExecAsync(conn, sql, ct);
                await ExecAsync(conn, "insert into schema_migrations (version) values ($1)", ct, version);
                await tx.CommitAsync(ct);
                appliedNow.Add(version);
            }

            return appliedNow;
        }
        finally
        {
            await ExecAsync(conn, "select pg_advisory_unlock($1)", CancellationToken.None, LockKey);
        }
    }

    private static IEnumerable<(string Version, string Sql)> LoadScripts()
    {
        var assembly = typeof(Migrator).Assembly;
        // Names look like "migrations/001_init.sql" (LogicalName in Switchboard.Core.csproj).
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (Path.GetFileNameWithoutExtension(name), reader.ReadToEnd());
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

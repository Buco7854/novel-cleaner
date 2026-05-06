using System.Data;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Data;

/// <summary>
/// Pragma-based, additive schema upgrades for SQLite. Runs after
/// <c>EnsureCreatedAsync</c> and adds any columns that newer code expects but
/// older databases don't have. Safer than dropping the DB on every model change
/// and lighter-weight than full EF Core migrations.
///
/// Each entry is idempotent: it inspects <c>PRAGMA table_info(...)</c> and only
/// runs <c>ALTER TABLE</c> when the column is missing.
/// </summary>
public static class SchemaMigrator
{
    public static async Task ApplyAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        // Add new columns here as the model grows. Each call is idempotent.
        await EnsureColumnAsync(db, logger, "JobLogs", "Detail",  "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "JobLogs", "GroupId", "TEXT NULL", ct);
    }

    private static async Task EnsureColumnAsync(
        AppDbContext db, ILogger logger, string table, string column, string columnDef, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        bool exists;
        await using (var inspect = conn.CreateCommand())
        {
            inspect.CommandText = $"PRAGMA table_info({table})";
            exists = false;
            await using var reader = await inspect.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(reader.GetOrdinal("name"));
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDef}";
        await alter.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Schema upgrade: added column {Table}.{Column}", table, column);
    }
}

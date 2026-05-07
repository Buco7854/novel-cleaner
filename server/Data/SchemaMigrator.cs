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

        // Review-before-applying feature.
        await EnsureColumnAsync(db, logger, "UserSettings", "ReviewBeforeApplying",
            "INTEGER NOT NULL DEFAULT 1", ct);
        await EnsureColumnAsync(db, logger, "CleanJobs", "ReviewBeforeApplying",
            "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureTableAsync(db, logger, "ChapterReviews", """
            CREATE TABLE IF NOT EXISTS "ChapterReviews" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "JobId" TEXT NOT NULL,
                "DocumentName" TEXT NOT NULL,
                "VisibleText" TEXT NOT NULL,
                "OrderIndex" INTEGER NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                CONSTRAINT "FK_ChapterReviews_CleanJobs_JobId"
                    FOREIGN KEY ("JobId") REFERENCES "CleanJobs"("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ChapterReviews_JobId"
                ON "ChapterReviews"("JobId");
            """, ct);
        await EnsureTableAsync(db, logger, "ReviewProposals", """
            CREATE TABLE IF NOT EXISTS "ReviewProposals" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ChapterReviewId" INTEGER NOT NULL,
                "Text" TEXT NOT NULL,
                "Reason" TEXT NOT NULL,
                "Source" INTEGER NOT NULL,
                "Decision" INTEGER NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                CONSTRAINT "FK_ReviewProposals_ChapterReviews_ChapterReviewId"
                    FOREIGN KEY ("ChapterReviewId") REFERENCES "ChapterReviews"("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ReviewProposals_ChapterReviewId"
                ON "ReviewProposals"("ChapterReviewId");
            """, ct);
    }

    private static async Task EnsureTableAsync(
        AppDbContext db, ILogger logger, string table, string createSql, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await using (var inspect = conn.CreateCommand())
        {
            inspect.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
            var p = inspect.CreateParameter();
            p.ParameterName = "$name";
            p.Value = table;
            inspect.Parameters.Add(p);
            var existed = await inspect.ExecuteScalarAsync(ct) is not null;
            if (existed) return;
        }

        await using var create = conn.CreateCommand();
        create.CommandText = createSql;
        await create.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Schema upgrade: created table {Table}", table);
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

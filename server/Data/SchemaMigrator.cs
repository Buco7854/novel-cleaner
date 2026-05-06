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
        // Ensure tables that were added after the initial schema are present on
        // databases created by older versions (EnsureCreatedAsync won't add them).
        await EnsureTableAsync(db, logger, "AppSettings", """
            CREATE TABLE IF NOT EXISTS "AppSettings" (
                "Id"           INTEGER NOT NULL CONSTRAINT "PK_AppSettings" PRIMARY KEY,
                "ApiKey"       TEXT NULL,
                "BaseUrl"      TEXT NOT NULL DEFAULT '',
                "Model"        TEXT NOT NULL DEFAULT '',
                "MaxWorkers"   INTEGER NOT NULL DEFAULT 3,
                "SystemPrompt" TEXT NULL,
                "DropFolder"   TEXT NULL,
                "UpdatedAt"    INTEGER NOT NULL DEFAULT 0
            )
            """, ct);

        // Add new columns here as the model grows. Each call is idempotent.
        await EnsureColumnAsync(db, logger, "JobLogs", "Detail",  "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "JobLogs", "GroupId", "TEXT NULL", ct);

        await EnsureColumnAsync(db, logger, "CleanJobs", "RerunRequested",
            "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(db, logger, "CleanJobs", "RepoPath",
            "TEXT NULL", ct);
        // Per-page AI run filter (editor's "Run AI on selected pages").
        await EnsureColumnAsync(db, logger, "CleanJobs", "PagesFilterJson",
            "TEXT NULL", ct);
        // User-scoped prompt addition layered on top of the admin's.
        await EnsureColumnAsync(db, logger, "UserSettings", "SystemPrompt",
            "TEXT NULL", ct);
        // Per-novel prompt addition (most specific layer in the stack).
        await EnsureColumnAsync(db, logger, "CleanJobs", "SystemPrompt",
            "TEXT NULL", ct);
        // Admin master-switch for AI features.
        await EnsureColumnAsync(db, logger, "AppSettings", "AiEnabled",
            "INTEGER NOT NULL DEFAULT 1", ct);
        // EPUB metadata, populated at upload time from the OPF and editable
        // from the editor's metadata modal. All nullable — older rows and
        // sparse EPUBs simply fall back to OriginalFileName.
        await EnsureColumnAsync(db, logger, "CleanJobs", "Title",       "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "CleanJobs", "Author",      "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "CleanJobs", "Language",    "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "CleanJobs", "Publisher",   "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "CleanJobs", "Description", "TEXT NULL", ct);
        // OPDS sources used to carry an import-mode default — both the
        // legacy boolean AutoClean and the short-lived DefaultImportMode
        // enum got dropped: the browse page exposes both modes per book
        // so the source itself doesn't need to take a side.
        await DropColumnIfExistsAsync(db, logger, "OpdsSources", "AutoClean", ct);
        await DropColumnIfExistsAsync(db, logger, "OpdsSources", "DefaultImportMode", ct);

        // Pattern-detection mode and its context window were retired — every
        // run now sends each chapter to the LLM in full. Drop the now-unused
        // columns from existing databases so EF/Sqlite don't choke on them
        // when the model no longer references them. Requires SQLite ≥ 3.35.
        await DropColumnIfExistsAsync(db, logger, "CleanJobs",     "ScanAll",       ct);
        await DropColumnIfExistsAsync(db, logger, "CleanJobs",     "PatternsJson",  ct);
        await DropColumnIfExistsAsync(db, logger, "CleanJobs",     "ContextWindow", ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings",  "ScanAll",       ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings",  "PatternsJson",  ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings",  "ContextWindow", ct);
        // Review-before-applying toggle was retired — every AI run now
        // pauses for review unconditionally.
        await DropColumnIfExistsAsync(db, logger, "CleanJobs",     "ReviewBeforeApplying", ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings",  "ReviewBeforeApplying", ct);
        // ChapterReviews / ReviewProposals are intentionally not created on
        // fresh databases — the editor moved off of them onto the per-job
        // git repo. Existing databases keep the tables (orphaned, untouched)
        // so we don't lose any in-flight review state on upgrade.
    }

    private static async Task DropColumnIfExistsAsync(
        AppDbContext db, ILogger logger, string table, string column, CancellationToken ct)
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
        if (!exists) return;

        try
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} DROP COLUMN {column}";
            await alter.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Schema upgrade: dropped column {Table}.{Column}", table, column);
        }
        catch (Exception ex)
        {
            // Older SQLite (< 3.35) doesn't support DROP COLUMN. Leaving the
            // column orphaned is harmless — EF only queries columns it knows
            // about — so we log and move on rather than fail startup.
            logger.LogWarning(ex, "Schema upgrade: could not drop {Table}.{Column} (likely older SQLite); column left orphaned",
                table, column);
        }
    }

    private static async Task EnsureTableAsync(
        AppDbContext db, ILogger logger, string table, string createSql, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = createSql;
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogDebug("Schema upgrade: ensured table {Table}", table);
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

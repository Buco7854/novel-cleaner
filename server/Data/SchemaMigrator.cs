using System.Data;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Data;

/// <summary>
/// Pragma-based, additive schema upgrades for SQLite. Runs after
/// <c>EnsureCreatedAsync</c> and adapts databases created by older code
/// (added columns, renamed tables) to the shape current code expects.
/// Safer than dropping the DB on every model change and lighter-weight than
/// full EF Core migrations.
///
/// Each entry is idempotent: it inspects the current schema before issuing
/// any DDL.
/// </summary>
public static class SchemaMigrator
{
    public static async Task ApplyAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        // 1. Rename legacy "Job" tables to their current "Novel" names. Older
        //    databases were created when the domain was modelled around a
        //    "CleanJob" — the C# types are now "Novel" / "NovelLogEntry", and
        //    EF Core maps them to "Novels" / "NovelLogs" by default. Renaming
        //    in-place preserves every existing row and keeps EnsureCreated
        //    from creating a parallel empty table on the next boot.
        await RenameTableIfNeededAsync(db, logger, "CleanJobs", "Novels", ct);
        await RenameTableIfNeededAsync(db, logger, "JobLogs",   "NovelLogs", ct);
        // The foreign-key column on log rows tracked the same rename.
        await RenameColumnIfNeededAsync(db, logger, "NovelLogs", "JobId", "NovelId", ct);

        // 2. Ensure tables that were added after the initial schema are
        //    present on databases created by older versions (EnsureCreated
        //    won't add them).
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

        // 3. Add new columns here as the model grows. Each call is idempotent.
        await EnsureColumnAsync(db, logger, "NovelLogs", "Detail",  "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "NovelLogs", "GroupId", "TEXT NULL", ct);

        await EnsureColumnAsync(db, logger, "Novels", "RerunRequested",
            "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(db, logger, "Novels", "RepoPath",
            "TEXT NULL", ct);
        // Per-page AI run filter (editor's "Run AI on selected pages").
        await EnsureColumnAsync(db, logger, "Novels", "PagesFilterJson",
            "TEXT NULL", ct);
        // User-scoped prompt addition layered on top of the admin's.
        await EnsureColumnAsync(db, logger, "UserSettings", "SystemPrompt",
            "TEXT NULL", ct);
        // Per-novel prompt addition (most specific layer in the stack).
        await EnsureColumnAsync(db, logger, "Novels", "SystemPrompt",
            "TEXT NULL", ct);
        // Admin master-switch for AI features.
        await EnsureColumnAsync(db, logger, "AppSettings", "AiEnabled",
            "INTEGER NOT NULL DEFAULT 1", ct);
        // EPUB metadata, populated at upload time from the OPF and editable
        // from the editor's metadata modal. All nullable — older rows and
        // sparse EPUBs simply fall back to OriginalFileName.
        await EnsureColumnAsync(db, logger, "Novels", "Title",       "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "Novels", "Author",      "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "Novels", "Language",    "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "Novels", "Publisher",   "TEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "Novels", "Description", "TEXT NULL", ct);
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
        await DropColumnIfExistsAsync(db, logger, "Novels",       "ScanAll",       ct);
        await DropColumnIfExistsAsync(db, logger, "Novels",       "PatternsJson",  ct);
        await DropColumnIfExistsAsync(db, logger, "Novels",       "ContextWindow", ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings", "ScanAll",       ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings", "PatternsJson",  ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings", "ContextWindow", ct);
        // Review-before-applying toggle was retired — every AI run now
        // pauses for review unconditionally.
        await DropColumnIfExistsAsync(db, logger, "Novels",       "ReviewBeforeApplying", ct);
        await DropColumnIfExistsAsync(db, logger, "UserSettings", "ReviewBeforeApplying", ct);
        // ChapterReviews / ReviewProposals are intentionally not created on
        // fresh databases — the editor moved off of them onto the per-novel
        // git repo. Existing databases keep the tables (orphaned, untouched)
        // so we don't lose any in-flight review state on upgrade.
    }

    private static async Task RenameTableIfNeededAsync(
        AppDbContext db, ILogger logger, string oldName, string newName, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        var oldExists = await TableExistsAsync(conn, oldName, ct);
        var newExists = await TableExistsAsync(conn, newName, ct);
        if (!oldExists || newExists) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE \"{oldName}\" RENAME TO \"{newName}\"";
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Schema upgrade: renamed table {Old} -> {New}", oldName, newName);
    }

    private static async Task RenameColumnIfNeededAsync(
        AppDbContext db, ILogger logger, string table, string oldColumn, string newColumn, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, table, ct)) return;
        if (!await ColumnExistsAsync(conn, table, oldColumn, ct)) return;
        if (await ColumnExistsAsync(conn, table, newColumn, ct)) return;

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE \"{table}\" RENAME COLUMN \"{oldColumn}\" TO \"{newColumn}\"";
            await cmd.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Schema upgrade: renamed {Table}.{Old} -> {New}",
                table, oldColumn, newColumn);
        }
        catch (Exception ex)
        {
            // SQLite ≥ 3.25 supports RENAME COLUMN; older builds don't.
            // Surface a warning so operators on stale SQLite know they need
            // to upgrade before the column reference takes effect.
            logger.LogWarning(ex, "Schema upgrade: could not rename {Table}.{Old} -> {New} (likely older SQLite)",
                table, oldColumn, newColumn);
        }
    }

    private static async Task<bool> TableExistsAsync(
        System.Data.Common.DbConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        var p = cmd.CreateParameter();
        p.ParameterName = "$name";
        p.Value = name;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        System.Data.Common.DbConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task DropColumnIfExistsAsync(
        AppDbContext db, ILogger logger, string table, string column, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        if (!await ColumnExistsAsync(conn, table, column, ct)) return;

        try
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\"";
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

        if (await ColumnExistsAsync(conn, table, column, ct)) return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {columnDef}";
        await alter.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Schema upgrade: added column {Table}.{Column}", table, column);
    }
}

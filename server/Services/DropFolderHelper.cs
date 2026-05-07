using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace NovelCleaner.Server.Services;

public sealed record DropResult(string DestinationPath);

public static class DropFolderHelper
{
    /// <summary>
    /// Copies a finished job's output into the configured drop folder, after
    /// confirming a folder is configured AND the job's owner holds the
    /// BookDrop permission. Used by both the worker (post-clean) and the
    /// finalize endpoint (post-review) so they emit identical log lines and
    /// honor the same gate.
    /// </summary>
    public static async Task TryCopyJobOutputAsync(
        CleanJob job,
        AppSettings settings,
        string sourcePath,
        JobLogger logger,
        UserManager<AppUser> users,
        AuthOptions auth,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.DropFolder)) return;

        if (job.User is null
            || !await Permissions.CanUseDropFolderAsync(users, job.User, auth))
        {
            await logger.LogAsync(job.Id, "info",
                "Drop folder skipped: the job's owner does not have the BookDrop permission.", ct);
            return;
        }

        try
        {
            var result = Copy(settings.DropFolder, job.OriginalFileName, sourcePath);
            await logger.LogAsync(job.Id, "info", $"Copied to drop folder: {result.DestinationPath}", ct);
        }
        catch (Exception ex)
        {
            await logger.LogAsync(job.Id, "warn", $"Drop folder copy failed: {ex.Message}", ct);
        }
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into <paramref name="dropFolder"/>, naming the file
    /// "{originalStem}_cleaned{ext}". Suffixes " (N)" if needed; never overwrites.
    /// Returns the final destination path.
    /// Throws on I/O errors so callers can decide how to surface them.
    /// </summary>
    public static DropResult Copy(string dropFolder, string originalFileName, string sourcePath)
    {
        var folder = dropFolder.Trim();
        Directory.CreateDirectory(folder);

        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        var ext = Path.GetExtension(originalFileName);
        if (ext.Length == 0) ext = ".epub";

        var destPath = Path.Combine(folder, $"{stem}_cleaned{ext}");
        var counter = 1;
        while (File.Exists(destPath))
        {
            destPath = Path.Combine(folder, $"{stem}_cleaned ({counter}){ext}");
            counter++;
        }

        File.Copy(sourcePath, destPath, overwrite: false);
        return new DropResult(destPath);
    }
}

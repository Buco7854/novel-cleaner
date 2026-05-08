using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace NovelCleaner.Server.Services;

public sealed record DropResult(string DestinationPath);

/// <summary>
/// Copies cleaned EPUB output into the admin-configured drop folder. Both
/// the auto path (worker post-clean, finalize-after-review) and the manual
/// re-trigger button on the editor route through here so the on-disk
/// behavior and log lines stay identical.
/// </summary>
public static class DropFolderHelper
{
    /// <summary>
    /// Copies a finished novel's output into the configured drop folder,
    /// after confirming a folder is configured AND the novel's owner holds
    /// the drop-folder permission.
    /// </summary>
    public static async Task TryCopyOutputAsync(
        Novel novel,
        AppSettings settings,
        string sourcePath,
        NovelEventLogger logger,
        UserManager<AppUser> users,
        AuthOptions auth,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.DropFolder)) return;

        if (novel.User is null
            || !await Permissions.CanUseDropFolderAsync(users, novel.User, auth))
        {
            await logger.LogAsync(novel.Id, "info",
                "Drop folder skipped: the novel's owner does not have the drop-folder permission.", ct);
            return;
        }

        try
        {
            var result = Copy(settings.DropFolder, novel.OriginalFileName, sourcePath);
            await logger.LogAsync(novel.Id, "info", $"Copied to drop folder: {result.DestinationPath}", ct);
        }
        catch (Exception ex)
        {
            await logger.LogAsync(novel.Id, "warn", $"Drop folder copy failed: {ex.Message}", ct);
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

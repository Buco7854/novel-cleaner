using Tergeo.Server.Configuration;
using Tergeo.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace Tergeo.Server.Services;

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
    /// Copies a finished book's output into the configured drop folder,
    /// after confirming a folder is configured AND the book's owner holds
    /// the drop-folder permission.
    /// </summary>
    public static async Task TryCopyOutputAsync(
        Book book,
        string? dropFolder,
        string sourcePath,
        BookEventLogger logger,
        UserManager<AppUser> users,
        AuthOptions auth,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dropFolder)) return;

        if (book.User is null
            || !await Permissions.CanUseDropFolderAsync(users, book.User, auth))
        {
            await logger.LogAsync(book.Id, "info",
                "Drop folder skipped: the book's owner does not have the drop-folder permission.", ct);
            return;
        }

        try
        {
            var result = Copy(dropFolder, book.OriginalFileName, sourcePath);
            await logger.LogAsync(book.Id, "info", $"Copied to drop folder: {result.DestinationPath}", ct);
        }
        catch (Exception ex)
        {
            await logger.LogAsync(book.Id, "warn", $"Drop folder copy failed: {ex.Message}", ct);
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

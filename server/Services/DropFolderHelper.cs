namespace NovelCleaner.Server.Services;

public sealed record DropResult(string DestinationPath);

public static class DropFolderHelper
{
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

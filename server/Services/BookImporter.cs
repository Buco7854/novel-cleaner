namespace NovelCleaner.Server.Services;

/// <summary>
/// One-stop pipeline for "I have an EPUB on disk, give me an editor repo and
/// the OPF metadata". Used by every entry point that produces a new
/// CleanJob — fresh upload, OPDS import, clone — and by the reset endpoint
/// which re-runs both halves against the same input.
///
/// Centralizing this lets each call site stop duplicating the
/// docs → text → init-repo → log-on-failure dance, AND ensures every entry
/// point reads metadata uniformly (the OPDS and clone paths used to skip it).
/// </summary>
public sealed class BookImporter(BookRepo bookRepo, JobLogger logger)
{
    /// <summary>
    /// Reads the EPUB at <paramref name="inputPath"/>, extracts visible text
    /// per chapter, initializes (or rebuilds) the editor repo for
    /// <paramref name="jobId"/>, and reads the OPF metadata. Repo init errors
    /// are caught and logged — the worker will retry on first run rather than
    /// failing the whole import.
    /// </summary>
    public async Task<(string? RepoPath, EpubMetadata Metadata)> ImportAsync(
        Guid jobId, string inputPath, CancellationToken ct = default)
    {
        string? repoPath = null;
        try
        {
            // Store the chapter's raw HTML in the editor repo, not extracted
            // text. Pretty-printed so the textarea and diff view aren't a
            // single 50-kB line; the export step re-minifies before writing
            // back to the EPUB so the file size doesn't bloat.
            var docs = EpubHandler.ReadHtmlDocuments(inputPath);
            var pages = docs
                .Select(d => (d.Name, EpubHandler.PrettyPrintHtml(d.Content)))
                .ToList();
            repoPath = bookRepo.InitFromPages(jobId, pages);
        }
        catch (Exception ex)
        {
            await logger.LogAsync(jobId, "warn",
                $"Editor repo init deferred: {ex.Message}", ct);
        }

        var metadata = EpubHandler.ReadMetadata(inputPath);
        return (repoPath, metadata);
    }

    /// <summary>
    /// Copies metadata fields from the EPUB onto the job, keeping any field
    /// the user has already populated (so an explicit edit isn't clobbered by
    /// an EPUB that lacks that tag).
    /// </summary>
    public static void ApplyMetadata(Models.CleanJob job, EpubMetadata meta, bool overwrite)
    {
        if (overwrite || job.Title       is null) job.Title       = meta.Title       ?? job.Title;
        if (overwrite || job.Author      is null) job.Author      = meta.Author      ?? job.Author;
        if (overwrite || job.Language    is null) job.Language    = meta.Language    ?? job.Language;
        if (overwrite || job.Publisher   is null) job.Publisher   = meta.Publisher   ?? job.Publisher;
        if (overwrite || job.Description is null) job.Description = meta.Description ?? job.Description;
    }
}

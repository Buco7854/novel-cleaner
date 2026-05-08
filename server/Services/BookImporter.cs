namespace Tergeo.Server.Services;

/// <summary>
/// One-stop pipeline for "I have an EPUB on disk, give me an editor repo and
/// the OPF metadata". Used by every entry point that produces a new
/// <see cref="Models.Book"/> — fresh upload, OPDS import, clone — and by the
/// reset endpoint which re-runs both halves against the same input.
///
/// Centralizing this lets each call site stop duplicating the
/// docs → text → init-repo → log-on-failure dance, AND ensures every entry
/// point reads metadata uniformly (the OPDS and clone paths used to skip it).
/// </summary>
public sealed class BookImporter(BookEditorRepo editorRepo, BookEventLogger logger)
{
    /// <summary>
    /// Reads the EPUB at <paramref name="inputPath"/>, extracts visible text
    /// per chapter, initializes (or rebuilds) the editor repo for
    /// <paramref name="bookId"/>, and reads the OPF metadata. Repo init
    /// errors are caught and logged — the worker will retry on first run
    /// rather than failing the whole import.
    /// </summary>
    public async Task<(string? RepoPath, EpubMetadata Metadata)> ImportAsync(
        Guid bookId, string inputPath, CancellationToken ct = default)
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
            repoPath = editorRepo.InitFromPages(bookId, pages);
        }
        catch (Exception ex)
        {
            await logger.LogAsync(bookId, "warn",
                $"Editor repo init deferred: {ex.Message}", ct);
        }

        var metadata = EpubHandler.ReadMetadata(inputPath);
        return (repoPath, metadata);
    }

    /// <summary>
    /// Copies metadata fields from the EPUB onto the book, keeping any
    /// field the user has already populated (so an explicit edit isn't
    /// clobbered by an EPUB that lacks that tag).
    /// </summary>
    public static void ApplyMetadata(Models.Book book, EpubMetadata meta, bool overwrite)
    {
        if (overwrite || book.Title       is null) book.Title       = meta.Title       ?? book.Title;
        if (overwrite || book.Author      is null) book.Author      = meta.Author      ?? book.Author;
        if (overwrite || book.Language    is null) book.Language    = meta.Language    ?? book.Language;
        if (overwrite || book.Publisher   is null) book.Publisher   = meta.Publisher   ?? book.Publisher;
        if (overwrite || book.Description is null) book.Description = meta.Description ?? book.Description;
    }
}

using System.Text;
using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Status of one page (file) inside a book repo, mirroring git's working-tree
/// view: <c>Clean</c> = no diff vs HEAD, <c>Modified</c> = unstaged changes,
/// <c>Staged</c> = staged but not committed (we don't currently use the
/// index, but we surface it for completeness).
/// </summary>
public enum PageStatus { Clean, Modified, Staged }

public sealed record PageInfo(
    string Path,
    int OrderIndex,
    PageStatus Status);

public sealed record PageContent(
    string Path,
    string Content,
    PageStatus Status,
    string? DiffAgainstHead);

/// <summary>
/// Wraps LibGit2Sharp behind the editor's mental model: one git repository
/// per <see cref="CleanJob"/>, one file per page (visible-text projection of
/// the chapter), commits as edit history. The initial commit holds the
/// untouched extraction so every subsequent diff is "what changed since the
/// raw EPUB". User edits and AI-run output both write to the working tree;
/// accept-hunk = stage+commit, reject-hunk = checkout-the-hunk.
///
/// Author identity is fixed (the app, not the user) — the user is
/// represented by the <c>CleanJob.UserId</c> at the API boundary, not in git
/// metadata.
/// </summary>
public sealed class BookRepo(IOptions<StorageOptions> storage, ILogger<BookRepo> log)
{
    private readonly StorageOptions _storage = storage.Value;
    private static readonly Signature AppSig = new(
        "Novel Cleaner", "novel-cleaner@local", DateTimeOffset.UtcNow);

    /// <summary>Resolves the on-disk path for a job's repo. Stable per job id.</summary>
    public string PathFor(Guid jobId) =>
        Path.Combine(_storage.RepoDirectory, jobId.ToString("N"));

    /// <summary>
    /// Initializes a fresh repo for <paramref name="jobId"/>, writes one
    /// <c>pages/{NNNN}_{safeName}.txt</c> per visible-text page, and seals
    /// the initial commit. Returns the repo path so the caller can persist
    /// it on the <see cref="CleanJob"/>. Idempotent: re-init on an existing
    /// path returns the existing path without rewriting.
    /// </summary>
    public string InitFromPages(Guid jobId, IReadOnlyList<(string DocumentName, string VisibleText)> pages)
    {
        var path = PathFor(jobId);
        if (Directory.Exists(Path.Combine(path, ".git"))) return path;

        Directory.CreateDirectory(path);
        Repository.Init(path);

        using var repo = new Repository(path);
        var pagesDir = Path.Combine(path, "pages");
        Directory.CreateDirectory(pagesDir);

        for (var i = 0; i < pages.Count; i++)
        {
            var fileName = PageFileName(i, pages[i].DocumentName);
            var diskPath = Path.Combine(pagesDir, fileName);
            File.WriteAllText(diskPath, pages[i].VisibleText, Encoding.UTF8);
            // Side-car: which zip entry this page corresponds to. Used at
            // EPUB export time to remap visible-text edits onto the
            // original HTML and rewrite the right archive entry.
            File.WriteAllText(diskPath + ".source",
                pages[i].DocumentName, Encoding.UTF8);
        }
        Commands.Stage(repo, "*");
        repo.Commit("Initial extraction", AppSig, AppSig,
            new CommitOptions { AllowEmptyCommit = true });
        log.LogInformation("Initialized book repo for job {JobId} with {N} page(s)", jobId, pages.Count);
        return path;
    }

    /// <summary>
    /// Lists every page under <c>pages/</c> with its working-tree status.
    /// Cheap — drives the file-tree sidebar.
    /// </summary>
    public IReadOnlyList<PageInfo> ListPages(string repoPath)
    {
        if (!Directory.Exists(Path.Combine(repoPath, ".git"))) return [];
        using var repo = new Repository(repoPath);
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUnaltered = true });

        var rows = new List<PageInfo>();
        foreach (var entry in status)
        {
            // Skip side-car files and anything outside pages/.
            if (!entry.FilePath.StartsWith("pages/", StringComparison.Ordinal)) continue;
            if (entry.FilePath.EndsWith(".source", StringComparison.Ordinal)) continue;

            var s = MapStatus(entry.State);
            var order = ParseOrderIndex(entry.FilePath);
            rows.Add(new PageInfo(entry.FilePath, order, s));
        }
        rows.Sort((a, b) => a.OrderIndex.CompareTo(b.OrderIndex));
        return rows;
    }

    /// <summary>
    /// Reads the working-tree content of <paramref name="relPath"/> and, if
    /// it diverges from HEAD, the unified diff. Both pieces feed the
    /// editor: the contenteditable shows the working-tree text; the diff
    /// drives the hunk-level accept/reject UI.
    /// </summary>
    public PageContent ReadPage(string repoPath, string relPath)
    {
        using var repo = new Repository(repoPath);
        var diskPath = Path.Combine(repoPath, relPath);
        var content = File.Exists(diskPath)
            ? File.ReadAllText(diskPath, Encoding.UTF8)
            : "";

        var entry = repo.RetrieveStatus(relPath);
        var status = MapStatus(entry);

        string? diff = null;
        if (status != PageStatus.Clean)
        {
            // Diff working tree vs HEAD's commit tree (skipping the index).
            var head = repo.Head.Tip?.Tree;
            if (head is not null)
            {
                var compare = repo.Diff.Compare<Patch>(
                    head, DiffTargets.WorkingDirectory, [relPath]);
                diff = compare.Content;
            }
        }
        return new PageContent(relPath, content, status, diff);
    }

    /// <summary>Overwrites the working-tree file. Does NOT commit.</summary>
    public void WritePage(string repoPath, string relPath, string content)
    {
        if (!relPath.StartsWith("pages/", StringComparison.Ordinal)
            || relPath.Contains(".."))
            throw new ArgumentException("relPath must be under pages/ and not escape it.", nameof(relPath));
        var diskPath = Path.Combine(repoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        File.WriteAllText(diskPath, content, Encoding.UTF8);
    }

    /// <summary>
    /// Stages every modified page and seals a commit with
    /// <paramref name="message"/>. No-op when the working tree is clean.
    /// Used by "accept all" and by the user's explicit save.
    /// </summary>
    public bool CommitAll(string repoPath, string message)
    {
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, "*");
        var status = repo.RetrieveStatus();
        if (!status.IsDirty) return false;
        repo.Commit(message, AppSig, AppSig);
        return true;
    }

    /// <summary>Discards working-tree changes for a single page (= reject all on that page).</summary>
    public void DiscardPage(string repoPath, string relPath)
    {
        using var repo = new Repository(repoPath);
        repo.CheckoutPaths(repo.Head.Tip.Sha, [relPath],
            new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
    }

    /// <summary>
    /// Reads a page's content as it stands in the HEAD commit. Used by
    /// finalize, which must export based on the *committed* state of the
    /// book — not whatever is in the working tree.
    /// </summary>
    public string ReadHeadContent(string repoPath, string relPath)
    {
        using var repo = new Repository(repoPath);
        var blob = repo.Head.Tip?[relPath]?.Target as Blob;
        return blob is null ? "" : blob.GetContentText(Encoding.UTF8);
    }

    /// <summary>
    /// Reads the page as it was at the *initial* commit (raw extraction, no
    /// edits). Finalize diffs HEAD vs initial to compute the removal set
    /// that gets applied to the original HTML.
    /// </summary>
    public string ReadInitialContent(string repoPath, string relPath)
    {
        using var repo = new Repository(repoPath);
        var first = repo.Commits.QueryBy(new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
        }).FirstOrDefault();
        var blob = first?[relPath]?.Target as Blob;
        return blob is null ? "" : blob.GetContentText(Encoding.UTF8);
    }

    /// <summary>
    /// Reads the side-car that records which EPUB zip-entry a page came from,
    /// so finalize can write the cleaned bytes back to the right archive
    /// member. Returns null when the side-car is missing (jobs created
    /// before this layout, or a hand-edited repo).
    /// </summary>
    public string? ReadDocName(string repoPath, string relPath)
    {
        var disk = Path.Combine(repoPath, relPath + ".source");
        return File.Exists(disk) ? File.ReadAllText(disk, Encoding.UTF8) : null;
    }

    /// <summary>
    /// Locates the page that mirrors a given EPUB document (by side-car
    /// match) and writes <paramref name="content"/> to its working-tree
    /// file. The worker uses this to land its post-LLM output on the right
    /// page without having to track index↔name mapping itself.
    /// </summary>
    public bool WritePageByDocName(string repoPath, string documentName, string content)
    {
        var pagesDir = Path.Combine(repoPath, "pages");
        if (!Directory.Exists(pagesDir)) return false;
        foreach (var src in Directory.EnumerateFiles(pagesDir, "*.source"))
        {
            var name = File.ReadAllText(src, Encoding.UTF8);
            if (!string.Equals(name, documentName, StringComparison.Ordinal)) continue;
            var pageDisk = src[..^".source".Length];
            File.WriteAllText(pageDisk, content, Encoding.UTF8);
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when HEAD diverges from the initial commit (= the book has been
    /// edited since extraction). Used by the worker to decide whether a
    /// "found nothing" rerun should keep the job in AwaitingReview or settle
    /// back to Completed.
    /// </summary>
    public bool HasEditsAgainstInitial(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var head = repo.Head.Tip;
        var first = repo.Commits.QueryBy(new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
        }).FirstOrDefault();
        if (head is null || first is null) return false;
        if (head.Sha == first.Sha) return false;
        var patch = repo.Diff.Compare<TreeChanges>(first.Tree, head.Tree);
        return patch.Count > 0;
    }

    /// <summary>
    /// Stage + commit using a private signature. Same shape as
    /// <see cref="CommitAll"/> but without the no-op short-circuit, so the
    /// worker can seal a "Pre-AI baseline" commit even when the working
    /// tree hasn't actually diverged yet.
    /// </summary>
    public bool Commit(string repoPath, string message, bool allowEmpty = false)
    {
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, "*");
        var status = repo.RetrieveStatus();
        if (!status.IsDirty && !allowEmpty) return false;
        repo.Commit(message, AppSig, AppSig,
            new CommitOptions { AllowEmptyCommit = allowEmpty });
        return true;
    }

    /// <summary>Maps libgit2's bitfield status to our coarser editor view.</summary>
    private static PageStatus MapStatus(FileStatus s)
    {
        if (s == FileStatus.Unaltered || s == FileStatus.Nonexistent) return PageStatus.Clean;
        if ((s & (FileStatus.NewInIndex | FileStatus.ModifiedInIndex)) != 0) return PageStatus.Staged;
        return PageStatus.Modified;
    }

    /// <summary>
    /// "pages/0042_chapter_-_Beautiful_Monster.xhtml.txt" → 42.
    /// Falls back to <see cref="int.MaxValue"/> for files that don't match
    /// the convention so they sort to the end without crashing.
    /// </summary>
    private static int ParseOrderIndex(string relPath)
    {
        var name = Path.GetFileName(relPath);
        var underscore = name.IndexOf('_');
        if (underscore <= 0) return int.MaxValue;
        return int.TryParse(name[..underscore], out var n) ? n : int.MaxValue;
    }

    private static string PageFileName(int orderIndex, string documentName)
    {
        var stem = Path.GetFileNameWithoutExtension(documentName);
        // Replace path separators and other characters that would break the
        // filesystem layout. Zip entries are slashed; we want flat files.
        var safe = string.Concat(stem.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        return $"{orderIndex:D4}_{safe}.txt";
    }
}

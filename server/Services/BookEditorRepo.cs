using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tergeo.Server.Configuration;
using Tergeo.Server.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace Tergeo.Server.Services;

/// <summary>
/// Status of one page (file) inside a book's editor repo, mirroring git's
/// working-tree view: <c>Clean</c> = no diff vs HEAD, <c>Modified</c> =
/// unstaged changes, <c>Staged</c> = staged but not committed (we don't
/// currently use the index, but we surface it for completeness).
/// </summary>
public enum PageStatus { Clean, Modified, Staged }

public sealed record PageInfo(
    string Path,
    int OrderIndex,
    PageStatus Status,
    /// <summary>
    /// EPUB document name this page mirrors (the side-car contents). Lets
    /// callers map worker log lines — keyed by document name — back to a
    /// page path the editor can open. Null when the side-car is missing
    /// (older repos hand-edited).
    /// </summary>
    string? DocName);

public sealed record PageContent(
    string Path,
    string Content,
    PageStatus Status,
    string? DiffAgainstHead);

/// <summary>One commit in a page's history — surfaced in the editor's
/// version-history panel so the user can preview / restore.</summary>
public sealed record PageRevision(
    string Sha,
    string ShortSha,
    string Message,
    DateTimeOffset Timestamp,
    string AuthorName);

/// <summary>One low-confidence removal proposed by the LLM that's still
/// pending in the working tree (not yet accepted or rejected). Stored in
/// the proposals side-car so the file-tree's cyan dot reflects actual
/// pending review state instead of historical log lines.</summary>
public sealed record SuspiciousProposal(string Removed, string Reason);

/// <summary>Per-page AI-proposal state: which low-confidence removals are
/// still untriaged, plus the count of items the LLM emitted that couldn't
/// be matched verbatim. Driven by <see cref="BookProcessor"/> at run
/// completion and pruned by accept/reject endpoints.</summary>
public sealed record PageProposals(
    IReadOnlyList<SuspiciousProposal> Suspicious,
    int Partial);

/// <summary>
/// Wraps LibGit2Sharp behind the editor's mental model: one git repository
/// per <see cref="Book"/>, one file per page (visible-text projection of
/// the chapter), commits as edit history. The initial commit holds the
/// untouched extraction so every subsequent diff is "what changed since the
/// raw EPUB". User edits and AI-run output both write to the working tree;
/// accept-hunk = stage+commit, reject-hunk = checkout-the-hunk.
///
/// Author identity is fixed (the app, not the user) — the user is
/// represented by the <c>Book.UserId</c> at the API boundary, not in git
/// metadata.
/// </summary>
public sealed class BookEditorRepo(IOptions<StorageOptions> storage, ILogger<BookEditorRepo> log)
{
    private readonly StorageOptions _storage = storage.Value;
    private static readonly Signature AppSig = new(
        "Tergeo", "tergeo@local", DateTimeOffset.UtcNow);

    /// <summary>Resolves the on-disk path for a book's repo. Stable per book id.</summary>
    public string PathFor(Guid bookId) =>
        Path.Combine(_storage.RepoDirectory, bookId.ToString("N"));

    /// <summary>
    /// Initializes a fresh repo for <paramref name="bookId"/>, writes one
    /// <c>pages/{NNNN}_{safeName}.txt</c> per visible-text page, and seals
    /// the initial commit. Returns the repo path so the caller can persist
    /// it on the <see cref="Book"/>. Idempotent: re-init on an existing
    /// path returns the existing path without rewriting.
    /// </summary>
    public string InitFromPages(Guid bookId, IReadOnlyList<(string DocumentName, string VisibleText)> pages)
    {
        var path = PathFor(bookId);
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
        log.LogInformation("Initialized editor repo for book {BookId} with {N} page(s)", bookId, pages.Count);
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
            var docName = ReadDocName(repoPath, entry.FilePath);
            rows.Add(new PageInfo(entry.FilePath, order, s, docName));
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
            // ContextLines=0 means every contiguous +/- block is its own
            // hunk with no surrounding context — the editor's accept/reject
            // controls then act on a single visual change at a time
            // instead of on a whole multi-block hunk.
            var head = repo.Head.Tip?.Tree;
            if (head is not null)
            {
                var compare = repo.Diff.Compare<Patch>(
                    head, DiffTargets.WorkingDirectory, [relPath],
                    null, ZeroContext);
                diff = compare.Content;
            }
        }
        return new PageContent(relPath, content, status, diff);
    }

    private static readonly CompareOptions ZeroContext = new() { ContextLines = 0 };

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

    /// <summary>
    /// Stage and commit just one page. No-op when the page is unchanged
    /// against HEAD. Powers the editor's per-page "Accept all changes on
    /// this page" button — the user can ratify pages one at a time without
    /// also committing other pages' pending edits.
    /// </summary>
    public bool CommitPage(string repoPath, string relPath, string message)
    {
        using var repo = new Repository(repoPath);
        var s = repo.RetrieveStatus(relPath);
        if (s == FileStatus.Unaltered || s == FileStatus.Nonexistent) return false;
        Commands.Stage(repo, relPath);
        repo.Commit(message, AppSig, AppSig);
        return true;
    }

    /// <summary>
    /// Stage and commit several pages in one go. Skips clean pages
    /// silently; returns false when the entire batch was clean (so the
    /// caller can surface "nothing to do"). Powers the file-tree's
    /// "Accept selected pages" action — one git commit covering the
    /// whole batch instead of N tiny commits cluttering history.
    /// </summary>
    public bool CommitMany(string repoPath, IReadOnlyList<string> relPaths, string message)
    {
        using var repo = new Repository(repoPath);
        var any = false;
        foreach (var rp in relPaths)
        {
            var s = repo.RetrieveStatus(rp);
            if (s == FileStatus.Unaltered || s == FileStatus.Nonexistent) continue;
            Commands.Stage(repo, rp);
            any = true;
        }
        if (!any) return false;
        repo.Commit(message, AppSig, AppSig);
        return true;
    }

    /// <summary>
    /// Discard working-tree changes for several pages at once. Each path
    /// is reset to whatever HEAD has for it; missing paths are skipped.
    /// Powers the file-tree's "Reject selected pages" action.
    /// </summary>
    public void DiscardMany(string repoPath, IReadOnlyList<string> relPaths)
    {
        using var repo = new Repository(repoPath);
        var head = repo.Head.Tip;
        if (head is null) return;
        var existing = relPaths
            .Where(p => repo.RetrieveStatus(p) != FileStatus.Nonexistent)
            .ToList();
        if (existing.Count == 0) return;
        repo.CheckoutPaths(head.Sha, existing,
            new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
    }

    /// <summary>Discards working-tree changes for a single page (= reject all on that page).</summary>
    public void DiscardPage(string repoPath, string relPath)
    {
        using var repo = new Repository(repoPath);
        repo.CheckoutPaths(repo.Head.Tip.Sha, [relPath],
            new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
    }

    /// <summary>
    /// Walk the commit graph and pick out every commit that touched
    /// <paramref name="relPath"/>. Returns newest-first — that's how the
    /// editor's history panel renders.
    /// </summary>
    public IReadOnlyList<PageRevision> ListPageHistory(string repoPath, string relPath)
    {
        using var repo = new Repository(repoPath);
        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        };
        var revisions = new List<PageRevision>();
        Blob? prevBlob = null;
        // Walk oldest → newest so we can detect "this commit changed the path"
        // by comparing against the previous commit's blob. Then reverse for
        // the response so callers see newest first.
        foreach (var c in repo.Commits.QueryBy(filter).Reverse())
        {
            var entry = c[relPath];
            var blob = entry?.Target as Blob;
            var changed = !ReferenceEquals(blob, prevBlob)
                && (blob is null || prevBlob is null || blob.Sha != prevBlob.Sha);
            if (changed && blob is not null)
            {
                revisions.Add(new PageRevision(
                    Sha: c.Sha,
                    ShortSha: c.Sha[..7],
                    Message: c.MessageShort,
                    Timestamp: c.Author.When,
                    AuthorName: c.Author.Name));
            }
            prevBlob = blob;
        }
        revisions.Reverse();
        return revisions;
    }

    /// <summary>Reads <paramref name="relPath"/> as it stood at <paramref name="sha"/>.</summary>
    public string? ReadPageAtCommit(string repoPath, string relPath, string sha)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null) return null;
        var blob = commit[relPath]?.Target as Blob;
        return blob?.GetContentText(Encoding.UTF8);
    }

    /// <summary>
    /// Replace the working-tree content of <paramref name="relPath"/> with
    /// what it was at <paramref name="sha"/>. Doesn't commit — surfaces as
    /// a pending diff so the user can review the restore alongside any
    /// other edits before sealing it. Returns false when sha is unknown
    /// or the path didn't exist at that revision.
    /// </summary>
    public bool RestorePageToCommit(string repoPath, string relPath, string sha)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null) return false;
        var blob = commit[relPath]?.Target as Blob;
        if (blob is null) return false;
        var diskPath = Path.Combine(repoPath, relPath);
        File.WriteAllText(diskPath, blob.GetContentText(Encoding.UTF8), Encoding.UTF8);
        return true;
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
    /// member. Returns null when the side-car is missing (books created
    /// before this layout, or a hand-edited repo).
    /// </summary>
    public string? ReadDocName(string repoPath, string relPath)
    {
        var disk = Path.Combine(repoPath, relPath + ".source");
        return File.Exists(disk) ? File.ReadAllText(disk, Encoding.UTF8) : null;
    }

    /// <summary>
    /// Locates the page that mirrors <paramref name="documentName"/> by
    /// scanning the side-car files. Returns the repo-relative page path
    /// (e.g. <c>pages/0042_chapter.txt</c>) or null when no page maps
    /// to that EPUB document.
    /// </summary>
    public string? FindPagePathByDocName(string repoPath, string documentName)
    {
        var pagesDir = Path.Combine(repoPath, "pages");
        if (!Directory.Exists(pagesDir)) return null;
        foreach (var src in Directory.EnumerateFiles(pagesDir, "*.source"))
        {
            var name = File.ReadAllText(src, Encoding.UTF8);
            if (!string.Equals(name, documentName, StringComparison.Ordinal)) continue;
            var pageDisk = src[..^".source".Length];
            // Convert absolute disk path back to repo-relative with forward
            // slashes — every other API in this class speaks that dialect.
            var rel = Path.GetRelativePath(repoPath, pageDisk).Replace('\\', '/');
            return rel;
        }
        return null;
    }

    /// <summary>
    /// Locates the page that mirrors a given EPUB document (by side-car
    /// match) and writes <paramref name="content"/> to its working-tree
    /// file. Returns the repo-relative page path on success (so the caller
    /// can update proposal metadata for that page) or null when no page
    /// maps to the document.
    /// </summary>
    public string? WritePageByDocName(string repoPath, string documentName, string content)
    {
        var rel = FindPagePathByDocName(repoPath, documentName);
        if (rel is null) return null;
        var pageDisk = Path.Combine(repoPath, rel);
        File.WriteAllText(pageDisk, content, Encoding.UTF8);
        return rel;
    }

    /// <summary>
    /// True when the working tree (or index) differs from HEAD — i.e. there
    /// are pending edits the user hasn't accepted yet. Drives the UI's
    /// "awaiting review" indicator: a book with uncommitted page changes
    /// is awaiting review, period, regardless of whether the AI just ran.
    /// </summary>
    public bool HasUncommittedChanges(string repoPath)
    {
        if (!Directory.Exists(Path.Combine(repoPath, ".git"))) return false;
        using var repo = new Repository(repoPath);
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUnaltered = false });
        // RetrieveStatus with IncludeUnaltered=false enumerates only the
        // entries that diverge from HEAD (modified, staged, new, removed,
        // renamed). Empty set = clean tree.
        return status.Any();
    }

    /// <summary>
    /// True when HEAD diverges from the initial commit (= the book has been
    /// edited since extraction). Used by the worker to decide whether a
    /// "found nothing" rerun should keep the book in AwaitingReview or
    /// settle back to Completed.
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

    /// <summary>
    /// Rewrites <paramref name="relPath"/>'s working-tree content to
    /// "HEAD plus every diff hunk except the one at
    /// <paramref name="hunkIndex"/>". The user-facing "reject this hunk"
    /// button — leaves all the OTHER pending changes alone so the user can
    /// triage them one at a time, then commit-all when they're done.
    ///
    /// Returns false when there's no HEAD yet, no diff for the path, or the
    /// index is out of bounds for the current diff.
    /// </summary>
    public bool RejectHunk(string repoPath, string relPath, int hunkIndex)
    {
        using var repo = new Repository(repoPath);
        var head = repo.Head.Tip;
        if (head is null) return false;

        var patch = repo.Diff.Compare<Patch>(head.Tree, DiffTargets.WorkingDirectory, [relPath], null, ZeroContext);
        var entry = patch[relPath];
        if (entry is null) return false;

        var hunks = ParseHunks(entry.Patch);
        if (hunkIndex < 0 || hunkIndex >= hunks.Count) return false;

        var headBlob = head[relPath]?.Target as Blob;
        var headContent = headBlob is null ? "" : headBlob.GetContentText(Encoding.UTF8);

        var keep = new HashSet<int>(
            Enumerable.Range(0, hunks.Count).Where(i => i != hunkIndex));
        var newContent = ApplySelectedHunks(headContent, hunks, keep);

        var diskPath = Path.Combine(repoPath, relPath);
        File.WriteAllText(diskPath, newContent, Encoding.UTF8);
        return true;
    }

    /// <summary>
    /// Stage + commit only the diff hunk at <paramref name="hunkIndex"/>,
    /// leaving every other pending change in the working tree. Implemented
    /// by writing a synthetic blob (HEAD + selected hunk applied) into the
    /// index and sealing a commit on top — libgit2 doesn't expose anything
    /// equivalent to <c>git add -p</c>, so we reach in at the index level.
    /// The working-tree file is untouched: re-running diff(HEAD, WT) after
    /// this returns just the leftover hunks.
    /// </summary>
    public bool AcceptHunk(string repoPath, string relPath, int hunkIndex, string message)
    {
        using var repo = new Repository(repoPath);
        var head = repo.Head.Tip;
        if (head is null) return false;

        var patch = repo.Diff.Compare<Patch>(head.Tree, DiffTargets.WorkingDirectory, [relPath], null, ZeroContext);
        var entry = patch[relPath];
        if (entry is null) return false;

        var hunks = ParseHunks(entry.Patch);
        if (hunkIndex < 0 || hunkIndex >= hunks.Count) return false;

        var headBlob = head[relPath]?.Target as Blob;
        var headContent = headBlob is null ? "" : headBlob.GetContentText(Encoding.UTF8);

        var staged = ApplySelectedHunks(headContent, hunks, [hunkIndex]);

        // Write the staged content as a blob, then build a tree that points
        // at it for the target path while preserving every other entry from
        // HEAD. Then commit pointing at that tree.
        var stagedBytes = Encoding.UTF8.GetBytes(staged);
        using var ms = new MemoryStream(stagedBytes);
        var blob = repo.ObjectDatabase.CreateBlob(ms, relPath);

        var treeDef = TreeDefinition.From(head);
        treeDef.Add(relPath, blob, Mode.NonExecutableFile);
        var newTree = repo.ObjectDatabase.CreateTree(treeDef);

        // Build the commit object and move the current branch ref to it.
        // We deliberately do NOT update repo.Index or the working-tree file:
        // re-running diff(HEAD-new, WT) then surfaces exactly the *unaccepted*
        // hunks, which is the post-condition the editor expects.
        var commit = repo.ObjectDatabase.CreateCommit(
            AppSig, AppSig, message, newTree, [head], prettifyMessage: false);
        var headRef = repo.Refs.Head.ResolveToDirectReference();
        repo.Refs.UpdateTarget(headRef, commit.Sha);
        return true;
    }

    // ---- Proposal side-car -------------------------------------------
    // AI proposals carry per-hunk metadata (suspicious / partial) that the
    // git history alone can't represent — git only tracks "what changed",
    // not "how confident the LLM was about each change". We persist this
    // out-of-band as <repo>/.git/proposals.json: the .git folder is
    // private to libgit2, files we drop in there never get staged or
    // committed, and they survive across server restarts (unlike an
    // in-memory cache).
    //
    // Source-of-truth contract:
    // - BookProcessor writes a fresh entry per page touched by an AI run.
    //   Pages that came out clean get their entry deleted, so a clean
    //   re-run automatically clears stale state.
    // - Accept/reject endpoints call PrunePageProposals, which compares
    //   the post-op diff to the recorded suspicious needles and drops
    //   entries whose deletion is no longer pending (resolved by the
    //   user's action either way).
    // - Whole-page commit / discard endpoints call ClearPageProposals
    //   directly because the page is unambiguously clean afterward.
    //
    // The frontend reads suspicious / partial counts straight off
    // PageEntry; the file-tree's cyan dot follows pending review state
    // by construction instead of being inferred from the log stream.

    private static readonly ConcurrentDictionary<string, object> _proposalsLocks = new();
    private static readonly JsonSerializerOptions ProposalsJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static string ProposalsPath(string repoPath) =>
        Path.Combine(repoPath, ".git", "proposals.json");

    private static object LockFor(string repoPath) =>
        _proposalsLocks.GetOrAdd(repoPath, _ => new object());

    /// <summary>
    /// Reads the entire proposals side-car. Returns an empty dict when the
    /// file is absent or corrupt — proposals are advisory, never load-bearing,
    /// so a parse failure shouldn't crash the editor.
    /// </summary>
    public IReadOnlyDictionary<string, PageProposals> ReadProposals(string repoPath)
    {
        var path = ProposalsPath(repoPath);
        if (!File.Exists(path)) return new Dictionary<string, PageProposals>();
        lock (LockFor(repoPath))
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<Dictionary<string, PageProposals>>(json, ProposalsJsonOpts)
                       ?? new Dictionary<string, PageProposals>();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to read proposals side-car at {Path} — treating as empty", path);
                return new Dictionary<string, PageProposals>();
            }
        }
    }

    /// <summary>
    /// Records (or overwrites) the proposal state for a single page. Empty
    /// entries (no suspicious + no partial) are dropped from the side-car
    /// so a clean run leaves the file as small as possible.
    /// </summary>
    public void SetPageProposals(
        string repoPath, string relPath,
        IReadOnlyList<SuspiciousProposal> suspicious, int partial)
    {
        lock (LockFor(repoPath))
        {
            var data = ReadProposalsLocked(repoPath);
            if (suspicious.Count == 0 && partial == 0)
                data.Remove(relPath);
            else
                data[relPath] = new PageProposals(suspicious, partial);
            WriteProposalsLocked(repoPath, data);
        }
    }

    /// <summary>Drops the proposal entry for a single page. Used after
    /// whole-page commit/discard, where the page is unambiguously clean
    /// post-op so any pending proposals are moot.</summary>
    public void ClearPageProposals(string repoPath, string relPath)
    {
        lock (LockFor(repoPath))
        {
            var data = ReadProposalsLocked(repoPath);
            if (data.Remove(relPath))
                WriteProposalsLocked(repoPath, data);
        }
    }

    /// <summary>Drops proposal entries for multiple pages — batch
    /// counterpart to <see cref="ClearPageProposals"/>, used by
    /// commit-many / discard-many.</summary>
    public void ClearProposals(string repoPath, IEnumerable<string> relPaths)
    {
        lock (LockFor(repoPath))
        {
            var data = ReadProposalsLocked(repoPath);
            var changed = false;
            foreach (var rp in relPaths)
                if (data.Remove(rp)) changed = true;
            if (changed) WriteProposalsLocked(repoPath, data);
        }
    }

    /// <summary>Wipes the entire proposals side-car. Used by
    /// commit-all — every page settles to clean.</summary>
    public void ClearAllProposals(string repoPath)
    {
        lock (LockFor(repoPath))
        {
            var path = ProposalsPath(repoPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Re-evaluates a page's proposals against its current state. Drops
    /// suspicious needles whose deletion is no longer pending in the diff
    /// (the user accepted or rejected them via accept-hunk / reject-hunk
    /// or by editing the page directly). Clears the entry entirely when
    /// the page has settled to clean. Partial count is left alone — it
    /// describes the original AI run and only AI re-runs overwrite it.
    /// </summary>
    public void PrunePageProposals(string repoPath, string relPath)
    {
        lock (LockFor(repoPath))
        {
            var data = ReadProposalsLocked(repoPath);
            if (!data.TryGetValue(relPath, out var entry)) return;

            var page = ReadPage(repoPath, relPath);
            if (page.Status == PageStatus.Clean)
            {
                data.Remove(relPath);
                WriteProposalsLocked(repoPath, data);
                return;
            }

            var deletions = ExtractDeletionsFromDiff(page.DiffAgainstHead);
            var stillPending = entry.Suspicious
                .Where(s => deletions.Contains(s.Removed))
                .ToList();

            var next = new PageProposals(stillPending, entry.Partial);
            if (next.Suspicious.Count == 0 && next.Partial == 0)
                data.Remove(relPath);
            else
                data[relPath] = next;
            WriteProposalsLocked(repoPath, data);
        }
    }

    private Dictionary<string, PageProposals> ReadProposalsLocked(string repoPath)
    {
        var path = ProposalsPath(repoPath);
        if (!File.Exists(path)) return new Dictionary<string, PageProposals>();
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, PageProposals>>(json, ProposalsJsonOpts)
                   ?? new Dictionary<string, PageProposals>();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to read proposals side-car at {Path} — treating as empty", path);
            return new Dictionary<string, PageProposals>();
        }
    }

    private static void WriteProposalsLocked(string repoPath, Dictionary<string, PageProposals> data)
    {
        var path = ProposalsPath(repoPath);
        if (data.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(data, ProposalsJsonOpts);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    /// <summary>
    /// Concatenates every '-' line in <paramref name="diff"/> (skipping the
    /// '---' file header) so the caller can cheaply check needle presence
    /// with a single Contains. Newlines between deleted lines are
    /// preserved so multi-paragraph needles still match.
    /// </summary>
    private static string ExtractDeletionsFromDiff(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return "";
        var sb = new StringBuilder();
        foreach (var line in diff.Split('\n'))
        {
            if (line.Length == 0 || line[0] != '-') continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;
            sb.Append(line, 1, line.Length - 1);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ---- Diff parsing (used by accept-hunk / reject-hunk below) ------

    private static readonly Regex HunkHeader = new(
        @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@",
        RegexOptions.Compiled);

    private sealed record DiffHunk(
        int OldStart, int OldCount, int NewStart, int NewCount, List<string> Lines);

    /// <summary>
    /// Minimal unified-diff parser. Pulls every <c>@@ -a,b +c,d @@</c> hunk
    /// out of <paramref name="diff"/> alongside its body lines (' ', '+',
    /// '-' prefixes). Tolerant of missing counts (treated as 1, per the
    /// unified-diff spec) and of <c>\ No newline</c> markers (skipped).
    /// </summary>
    private static List<DiffHunk> ParseHunks(string diff)
    {
        var hunks = new List<DiffHunk>();
        if (string.IsNullOrEmpty(diff)) return hunks;

        DiffHunk? cur = null;
        foreach (var line in diff.Split('\n'))
        {
            var match = HunkHeader.Match(line);
            if (match.Success)
            {
                cur = new DiffHunk(
                    int.Parse(match.Groups[1].Value),
                    match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1,
                    int.Parse(match.Groups[3].Value),
                    match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1,
                    []);
                hunks.Add(cur);
                continue;
            }
            if (cur is null) continue;
            if (line.StartsWith('\\')) continue; // "\ No newline at end of file"
            if (line.Length == 0) continue;
            var c = line[0];
            if (c == ' ' || c == '+' || c == '-') cur.Lines.Add(line);
        }
        return hunks;
    }

    /// <summary>
    /// Reconstructs file content as "<paramref name="head"/> with the
    /// hunks whose index is in <paramref name="selected"/> applied". Hunks
    /// whose index is not in <paramref name="selected"/> are skipped — the
    /// corresponding HEAD lines flow through unchanged. Inputs are split on
    /// '\n' which matches the format git uses internally.
    /// </summary>
    private static string ApplySelectedHunks(
        string head, List<DiffHunk> hunks, HashSet<int> selected)
    {
        var headLines = head.Split('\n');
        var output = new List<string>();
        var i = 0; // 0-based index into headLines
        var ordered = hunks
            .Select((h, idx) => (Hunk: h, Idx: idx))
            .OrderBy(t => t.Hunk.OldStart)
            .ToList();

        foreach (var (hunk, idx) in ordered)
        {
            // Copy unchanged HEAD lines up to (but not including) this hunk's
            // old block. OldStart is 1-based; subtract 1 to translate.
            var headStart0 = Math.Max(0, hunk.OldStart - 1);
            while (i < headStart0 && i < headLines.Length)
            {
                output.Add(headLines[i]);
                i++;
            }

            if (selected.Contains(idx))
            {
                // Emit the hunk's new state ('+' inserts, ' ' context).
                foreach (var body in hunk.Lines)
                {
                    if (body.Length == 0) continue;
                    var prefix = body[0];
                    if (prefix == ' ' || prefix == '+') output.Add(body[1..]);
                }
                // Skip past the hunk's old block in HEAD.
                i += hunk.OldCount;
            }
            // else: don't advance i — the next iteration's prelude copies
            // these HEAD lines through, preserving the original text.
        }

        // Tail.
        while (i < headLines.Length)
        {
            output.Add(headLines[i]);
            i++;
        }
        return string.Join("\n", output);
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

        // Cap stem length so the final on-disk path stays within Windows'
        // 260-char MAX_PATH even when the repo lives under a deeply-nested
        // user directory. The hash suffix preserves uniqueness when truncation
        // would collapse similarly-prefixed chapter titles into one filename.
        const int MaxStem = 80;
        if (safe.Length > MaxStem)
        {
            var hash = ShortHash(stem);
            safe = safe[..MaxStem] + "_" + hash;
        }
        return $"{orderIndex:D4}_{safe}.txt";
    }

    private static string ShortHash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}

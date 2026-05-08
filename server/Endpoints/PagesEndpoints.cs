using System.Security.Claims;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Tergeo.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tergeo.Server.Endpoints;

/// <summary>
/// File-editor surface — surfaces a book's git-backed page repository.
/// Each route is rooted at <c>/api/books/{bookId}</c> because pages belong
/// to a book. The page <c>path</c> contains '/' so it travels in a query
/// param rather than a route segment.
/// </summary>
public static class PagesEndpoints
{
    public static void MapPagesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/books/{bookId:guid}/pages").RequireAuthorization();

        // Sidebar feed — list every page with a coarse status flag so the UI
        // can put a dot next to modified ones. The proposals side-car is
        // joined in so the file-tree's cyan (suspicious) and partial badges
        // reflect live working-tree state instead of historical log lines.
        group.MapGet("/", async (Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath))
                return Results.Ok(Array.Empty<object>());
            var proposals = editorRepo.ReadProposals(book.RepoPath);
            var pages = editorRepo.ListPages(book.RepoPath)
                .Select(p =>
                {
                    proposals.TryGetValue(p.Path, out var pp);
                    return new
                    {
                        path = p.Path,
                        orderIndex = p.OrderIndex,
                        status = p.Status.ToString(),
                        docName = p.DocName,
                        suspicious = pp?.Suspicious.Count ?? 0,
                        partial = pp?.Partial ?? 0,
                    };
                });
            return Results.Ok(pages);
        });

        // One page's working-tree content + (if dirty) unified diff vs HEAD.
        // The frontend renders content in the contenteditable; the diff
        // hunks drive the accept/reject UI.
        group.MapGet("/page", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            try
            {
                var p = editorRepo.ReadPage(book.RepoPath, path);
                return Results.Ok(new
                {
                    path = p.Path,
                    content = p.Content,
                    status = p.Status.ToString(),
                    diff = p.DiffAgainstHead,
                });
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });

        // Render this page as the HTML it would land in the exported EPUB.
        // Storage is the chapter's raw HTML, so preview = working-tree
        // content + a <base href> rewriting relative asset URLs through
        // the proxy below + a transparent-bg style so the iframe blends
        // into the surrounding app theme.
        group.MapGet("/preview", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path, [FromQuery] string? theme,
            [FromQuery] string? source) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });

            var docName = editorRepo.ReadDocName(book.RepoPath, path);
            if (docName is null)
                return Results.BadRequest(new { error = "no original-doc mapping for page" });

            // Two render modes drive the sub-toggle inside the Preview tab:
            //   working (default): working tree — user sees their pending
            //                      edits + AI proposals rendered with the
            //                      publisher's stylesheet
            //   diff             : a synthetic single-page HTML where
            //                      removed paragraphs are wrapped <del>
            //                      and inserted paragraphs <ins> for a
            //                      side-by-side comparison without leaving
            //                      the iframe (typography is simplified)
            string html;
            if (string.Equals(source, "diff", StringComparison.OrdinalIgnoreCase))
            {
                var initialHtml = editorRepo.ReadInitialContent(book.RepoPath, path);
                var workingTreePath2 = Path.Combine(book.RepoPath, path);
                var currentHtml = File.Exists(workingTreePath2)
                    ? await File.ReadAllTextAsync(workingTreePath2)
                    : editorRepo.ReadHeadContent(book.RepoPath, path);
                var darkMode = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
                html = BuildDiffHtml(initialHtml, currentHtml, darkMode);
            }
            else
            {
                var workingTreePath = Path.Combine(book.RepoPath, path);
                html = File.Exists(workingTreePath)
                    ? await File.ReadAllTextAsync(workingTreePath)
                    : editorRepo.ReadHeadContent(book.RepoPath, path);
                // Strip <script> / event handlers from publisher HTML so the
                // sandboxed iframe doesn't log "blocked script" warnings.
                html = StripScripts(html);
            }
            var previewBytes = System.Text.Encoding.UTF8.GetBytes(html);

            var chapterDir = Path.GetDirectoryName(docName)?.Replace('\\', '/') ?? "";
            var baseHref = $"/api/books/{bookId}/pages/asset/"
                + (string.IsNullOrEmpty(chapterDir) ? "" : chapterDir + "/");
            var dark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
            previewBytes = InjectPreviewHead(previewBytes, baseHref, dark);

            var contentType = path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                ? "application/xhtml+xml"
                : "text/html";
            return Results.Bytes(previewBytes, contentType + "; charset=utf-8");
        });

        // Asset proxy for the preview iframe. Returns a single entry from
        // the input EPUB by archive-relative path. Auth-gated like every
        // other route in the group, traversal-checked in EpubHandler.
        // ".gif"/.webp/font types are mapped to sensible content-types so
        // browsers actually render them inline.
        group.MapGet("/asset/{**assetPath}", async (
            Guid bookId, HttpContext http, AppDbContext db, string assetPath) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (!File.Exists(book.InputStoragePath)) return Results.NotFound();
            var hit = EpubHandler.ReadEntry(book.InputStoragePath, assetPath);
            if (hit is null) return Results.NotFound();
            // Cache aggressively in the iframe — assets don't change between
            // preview reloads. Lets the next switch into Preview render
            // instantly instead of re-fetching the publisher's stylesheet.
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.Bytes(hit.Value.Bytes, hit.Value.ContentType);
        });

        // Save the user's edit to the working tree. No commit — the diff
        // shows up immediately and the user explicitly accepts via /commit.
        group.MapPut("/page", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path, [FromBody] WriteDto dto) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            editorRepo.WritePage(book.RepoPath, path, dto.Content ?? "");
            // The user's edit can either restore an AI-flagged deletion
            // (= reject the suspicious item) or leave it intact. Reprune
            // so the file-tree's badges follow the post-edit diff.
            editorRepo.PrunePageProposals(book.RepoPath, path);
            return Results.NoContent();
        });

        // Stage + commit everything in the working tree. Maps to the user's
        // "accept all changes" action; hunk-level accept lands in a follow-
        // up that diff-parses and stages partial files.
        group.MapPost("/commit", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            BookFinalizer finalizer, [FromBody] CommitDto dto, CancellationToken ct) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            var msg = string.IsNullOrWhiteSpace(dto.Message) ? "User edit" : dto.Message.Trim();
            var committed = editorRepo.CommitAll(book.RepoPath, msg);
            if (committed)
            {
                editorRepo.ClearAllProposals(book.RepoPath);
                await finalizer.FinalizeAsync(bookId, ct);
            }
            return Results.Ok(new { committed });
        });

        // Stage + commit just the named page — page-level "accept" so the
        // user can ratify a single page without rolling in pending edits
        // from other pages.
        group.MapPost("/commit-page", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            BookFinalizer finalizer, [FromQuery] string path, [FromBody] CommitDto dto,
            CancellationToken ct) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            var msg = string.IsNullOrWhiteSpace(dto.Message)
                ? $"Accept page {path}"
                : dto.Message.Trim();
            var committed = editorRepo.CommitPage(book.RepoPath, path, msg);
            if (committed)
            {
                editorRepo.ClearPageProposals(book.RepoPath, path);
                await finalizer.FinalizeAsync(bookId, ct);
            }
            return Results.Ok(new { committed });
        });

        // Batch accept: stage + commit every path in the body as one
        // commit. Powers the file tree's "Accept selected pages" action.
        group.MapPost("/commit-many", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            BookFinalizer finalizer, [FromBody] PathsDto dto, CancellationToken ct) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (dto?.Paths is null || dto.Paths.Count == 0)
                return Results.BadRequest(new { error = "No paths provided." });
            foreach (var p in dto.Paths)
                if (!IsSafeRelPath(p)) return Results.BadRequest(new { error = $"invalid path: {p}" });
            var msg = string.IsNullOrWhiteSpace(dto.Message)
                ? $"Accept {dto.Paths.Count} page(s)"
                : dto.Message.Trim();
            var committed = editorRepo.CommitMany(book.RepoPath, dto.Paths, msg);
            if (committed)
            {
                editorRepo.ClearProposals(book.RepoPath, dto.Paths);
                await finalizer.FinalizeAsync(bookId, ct);
            }
            return Results.Ok(new { committed });
        });

        // Batch reject: throw away every working-tree change on the
        // listed paths. Powers the file tree's "Reject selected pages".
        group.MapPost("/discard-many", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromBody] PathsDto dto) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (dto?.Paths is null || dto.Paths.Count == 0)
                return Results.BadRequest(new { error = "No paths provided." });
            foreach (var p in dto.Paths)
                if (!IsSafeRelPath(p)) return Results.BadRequest(new { error = $"invalid path: {p}" });
            editorRepo.DiscardMany(book.RepoPath, dto.Paths);
            editorRepo.ClearProposals(book.RepoPath, dto.Paths);
            return Results.NoContent();
        });

        // Throw away every working-tree change for one page (= reject all
        // proposals on that page). The committed history is untouched.
        group.MapPost("/discard", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            editorRepo.DiscardPage(book.RepoPath, path);
            editorRepo.ClearPageProposals(book.RepoPath, path);
            return Results.NoContent();
        });

        // Reject one diff hunk on a page = revert that hunk's lines to HEAD
        // while keeping every OTHER pending change. Hunk index is the
        // 0-based position in the unified diff returned by GET /page.
        group.MapPost("/reject-hunk", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path, [FromQuery] int index) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            var ok = editorRepo.RejectHunk(book.RepoPath, path, index);
            if (!ok) return Results.BadRequest(new { error = "hunk index out of range or no diff for path" });
            // The rejected hunk's deletion is no longer pending — re-prune
            // so any suspicious needle that hunk carried disappears from
            // the side-car.
            editorRepo.PrunePageProposals(book.RepoPath, path);
            return Results.NoContent();
        });

        // Accept one diff hunk = stage + commit only that hunk; leave the
        // rest as working-tree changes for further triage. The editor shows
        // the leftover hunks on the next refetch.
        group.MapPost("/accept-hunk", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            BookFinalizer finalizer, [FromQuery] string path, [FromQuery] int index,
            CancellationToken ct) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            var ok = editorRepo.AcceptHunk(book.RepoPath, path, index, $"Accept hunk {index} on {path}");
            if (!ok) return Results.BadRequest(new { error = "hunk index out of range or no diff for path" });
            // The accepted hunk's deletion is now committed — re-prune
            // so the corresponding suspicious needle leaves the side-car
            // (its decision is settled).
            editorRepo.PrunePageProposals(book.RepoPath, path);
            await finalizer.FinalizeAsync(bookId, ct);
            return Results.NoContent();
        });

        // List every commit that touched a given page. Drives the
        // version-history panel in the editor.
        group.MapGet("/history", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.Ok(Array.Empty<object>());
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            var revs = editorRepo.ListPageHistory(book.RepoPath, path)
                .Select(r => new
                {
                    sha = r.Sha,
                    shortSha = r.ShortSha,
                    message = r.Message,
                    timestamp = r.Timestamp,
                    author = r.AuthorName,
                });
            return Results.Ok(revs);
        });

        // Read a page as it was at a specific commit — used by the
        // history panel's preview pane.
        group.MapGet("/at", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path, [FromQuery] string sha) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            if (!IsSafeSha(sha)) return Results.BadRequest(new { error = "invalid sha" });
            var content = editorRepo.ReadPageAtCommit(book.RepoPath, path, sha);
            if (content is null) return Results.NotFound();
            return Results.Ok(new { content });
        });

        // Drop the page's content at <sha> into the working tree without
        // committing — the user reviews the restore as a normal pending
        // diff and can accept/reject it like any other edit.
        group.MapPost("/restore", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] string path, [FromQuery] string sha) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (string.IsNullOrEmpty(book.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            if (!IsSafeSha(sha)) return Results.BadRequest(new { error = "invalid sha" });
            var ok = editorRepo.RestorePageToCommit(book.RepoPath, path, sha);
            if (!ok) return Results.BadRequest(new { error = "unknown sha or path missing at that revision" });
            // The working tree just got rewritten — recompute proposals
            // against the new diff so any badges align with what's
            // actually pending.
            editorRepo.PrunePageProposals(book.RepoPath, path);
            return Results.NoContent();
        });

        // Rebuild the editor repo from the original EPUB — wipes the
        // working tree AND committed history, re-extracts every page.
        // Use when an extraction-pipeline change has shipped (paragraph
        // breaks, spine ordering, …) and existing pages need to pick up
        // the fix. Destructive: any user edits not yet exported to the
        // EPUB are lost.
        group.MapPost("/reset", async (
            Guid bookId, HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            BookImporter importer, BookFinalizer finalizer, BookEventLogger logger,
            CancellationToken ct) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (!File.Exists(book.InputStoragePath))
                return Results.BadRequest(new { error = "Original file is missing." });

            var path = editorRepo.PathFor(bookId);
            if (Directory.Exists(path))
            {
                try { ForceDeleteDirectory(path); }
                catch (Exception ex)
                {
                    return Results.Problem("Could not wipe existing repo: " + ex.Message);
                }
            }

            // Re-extract is a "trust the file on disk again" action, so
            // metadata from the OPF wins outright — the previous skip-if-set
            // guard meant editing metadata once would lock out future resets
            // from picking up a corrected EPUB.
            var (repoPath, meta) = await importer.ImportAsync(bookId, book.InputStoragePath, ct);
            book.RepoPath = repoPath;
            BookImporter.ApplyMetadata(book, meta, overwrite: true);

            await db.SaveChangesAsync(ct);
            // HEAD is now back to "initial = raw extraction with metadata
            // refreshed". Re-bake the cleaned EPUB so the Download button
            // doesn't keep serving the old finalized output.
            await finalizer.FinalizeAsync(bookId, ct);
            var pageCount = repoPath is null ? 0 : editorRepo.ListPages(repoPath).Count;
            await logger.LogAsync(bookId, "info",
                $"Editor repo rebuilt from EPUB — {pageCount} page(s) re-extracted.", ct);
            return Results.Ok(new { pages = pageCount });
        });
    }

    /// <summary>
    /// Builds a self-contained HTML page rendering the paragraph-level diff
    /// between <paramref name="initialHtml"/> and <paramref name="currentHtml"/>.
    /// Visible-text projections are LCS'd; equal paragraphs render plain,
    /// removed paragraphs are wrapped <c>&lt;del&gt;</c> (red strikethrough),
    /// inserted ones <c>&lt;ins&gt;</c> (green). Loses chapter typography in
    /// exchange for a clear "what's changing" view; the Current/Original
    /// modes still preserve full publisher styling.
    /// </summary>
    private static string BuildDiffHtml(string initialHtml, string currentHtml, bool dark)
    {
        var initialText = EpubHandler.ExtractText(System.Text.Encoding.UTF8.GetBytes(initialHtml));
        var currentText = EpubHandler.ExtractText(System.Text.Encoding.UTF8.GetBytes(currentHtml));
        var a = SplitParagraphs(initialText);
        var b = SplitParagraphs(currentText);
        var ops = LcsEditScript(a, b);

        // Dark-mode palette dims the prose background and pumps saturation
        // on ins/del so the highlights stay readable without dazzling.
        // InjectPreviewHead also forces body color in dark mode — emit
        // matching colors here so the page reads as a single design.
        var bodyColor = dark ? "#e7e5e4" : "#1c1917";
        var delBg     = dark ? "#7f1d1d" : "#fee2e2";
        var delFg     = dark ? "#fecaca" : "#991b1b";
        var insBg     = dark ? "#14532d" : "#dcfce7";
        var insFg     = dark ? "#bbf7d0" : "#166534";

        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\" /><style>")
          .Append($"body{{font-family:Georgia,'Times New Roman',serif;line-height:1.7;padding:1.5em;max-width:42em;margin:0 auto;color:{bodyColor};}}")
          .Append("p{margin:0 0 1em;white-space:pre-wrap;}")
          .Append($"del{{background:{delBg};color:{delFg};text-decoration:line-through;text-decoration-thickness:2px;padding:0 .15em;}}")
          .Append($"ins{{background:{insBg};color:{insFg};text-decoration:none;padding:0 .15em;}}")
          .Append("</style></head><body>");

        foreach (var (kind, text) in ops)
        {
            var encoded = System.Net.WebUtility.HtmlEncode(text);
            switch (kind)
            {
                case 'e': sb.Append("<p>").Append(encoded).Append("</p>"); break;
                case '-': sb.Append("<p><del>").Append(encoded).Append("</del></p>"); break;
                case '+': sb.Append("<p><ins>").Append(encoded).Append("</ins></p>"); break;
            }
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string[] SplitParagraphs(string text)
        => text.Split('\n')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

    /// <summary>LCS-backed edit script. Returns ordered ops:
    /// 'e' = equal, '-' = removed (in a, not in b), '+' = inserted (in b, not in a).</summary>
    private static List<(char Kind, string Text)> LcsEditScript(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (var i = 1; i <= m; i++)
            for (var j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var ops = new List<(char, string)>();
        int ii = m, jj = n;
        while (ii > 0 || jj > 0)
        {
            if (ii > 0 && jj > 0 && a[ii - 1] == b[jj - 1])
            {
                ops.Insert(0, ('e', a[ii - 1]));
                ii--; jj--;
            }
            else if (jj > 0 && (ii == 0 || dp[ii, jj - 1] >= dp[ii - 1, jj]))
            {
                ops.Insert(0, ('+', b[jj - 1]));
                jj--;
            }
            else
            {
                ops.Insert(0, ('-', a[ii - 1]));
                ii--;
            }
        }
        return ops;
    }

    /// <summary>
    /// Removes <c>&lt;script&gt;</c> tags, inline <c>on*</c> event handlers,
    /// and <c>javascript:</c>-protocol URLs from the chapter HTML before it
    /// goes into the sandboxed iframe. The sandbox already blocks script
    /// execution at the browser level — stripping here just silences the
    /// per-blocked-script console warning Chrome logs.
    /// </summary>
    private static string StripScripts(string html)
    {
        try
        {
            var parser = new HtmlParser();
            using var doc = parser.ParseDocument(html);
            foreach (var el in doc.QuerySelectorAll("script, noscript").ToArray())
                el.Remove();
            foreach (var el in doc.QuerySelectorAll("*").ToArray())
            {
                var handlers = el.Attributes
                    .Where(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Name)
                    .ToList();
                foreach (var name in handlers) el.RemoveAttribute(name);
                foreach (var attrName in new[] { "href", "src", "xlink:href" })
                {
                    var v = el.GetAttribute(attrName);
                    if (v is not null && v.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        el.RemoveAttribute(attrName);
                }
            }
            using var sw = new StringWriter();
            doc.ToHtml(sw, new PrettyMarkupFormatter());
            return sw.ToString();
        }
        catch
        {
            return html;
        }
    }

    /// <summary>
    /// Inserts a <c>&lt;base href="…"&gt;</c> and a transparent-background
    /// <c>&lt;style&gt;</c> into the chapter HTML so the preview iframe can
    /// (a) reach the publisher's stylesheet/images via the asset proxy and
    /// (b) blend into the surrounding app theme. Done as a string splice
    /// rather than a full DOM round-trip — preserves doctype, processing
    /// instructions, and any quirks AngleSharp might canonicalize away.
    /// </summary>
    private static byte[] InjectPreviewHead(byte[] htmlBytes, string baseHref, bool dark)
    {
        var html = System.Text.Encoding.UTF8.GetString(htmlBytes);
        // Always make body transparent so the iframe's parent shows through.
        // In dark mode override the publisher's hard-coded text color so
        // prose stays legible on the dark app background. !important to
        // outrank chapter stylesheets, which usually set color: black.
        var darkRules = dark
            ? "html{color-scheme:dark;color:#e7e5e4!important;}body{color:#e7e5e4!important;}a{color:#a8a29e!important;}"
            : "";
        var injection =
            $"<base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\" />"
            + $"<style>html,body{{background:transparent!important;}}{darkRules}</style>";

        // Splice immediately after <head>; if there's no head tag, fall back
        // to prepending — most browsers tolerate that for legacy/raw HTML.
        var headOpenIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (headOpenIdx >= 0)
        {
            var closeIdx = html.IndexOf('>', headOpenIdx);
            if (closeIdx > 0)
                return System.Text.Encoding.UTF8.GetBytes(
                    html[..(closeIdx + 1)] + injection + html[(closeIdx + 1)..]);
        }
        return System.Text.Encoding.UTF8.GetBytes(injection + html);
    }

    /// <summary>
    /// Recursively delete a directory, clearing the read-only attribute that
    /// libgit2 sets on pack files (otherwise <see cref="Directory.Delete"/>
    /// throws "Access to the path is denied" on Windows).
    /// </summary>
    private static void ForceDeleteDirectory(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(root, recursive: true);
    }

    private static Guid GetUserId(HttpContext http)
        => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<Book?> GetOwnedBookAsync(AppDbContext db, HttpContext http, Guid id)
    {
        var userId = GetUserId(http);
        var isAdmin = http.User.IsInRole(AppRoles.Admin);
        return isAdmin
            ? await db.Books.FirstOrDefaultAsync(n => n.Id == id)
            : await db.Books.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
    }

    /// <summary>
    /// Fast-fail path-traversal guard. Pages live under <c>pages/</c> and
    /// must not contain <c>..</c>; stronger validation happens inside
    /// <see cref="BookEditorRepo.WritePage"/> too.
    /// </summary>
    private static bool IsSafeRelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("..")) return false;
        return path.StartsWith("pages/", StringComparison.Ordinal);
    }

    /// <summary>Cheap shape check for git SHAs — hex, 7-64 chars.</summary>
    private static bool IsSafeSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || sha.Length is < 7 or > 64) return false;
        foreach (var c in sha)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    public sealed record WriteDto(string? Content);
    public sealed record CommitDto(string? Message);
    public sealed record PathsDto(List<string>? Paths, string? Message);
}

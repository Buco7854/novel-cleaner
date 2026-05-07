namespace NovelCleaner.Server.Models;

public enum ProposalSource { Llm, User }

/// <summary>
/// LLM proposals default to <see cref="Accepted"/> — review is a "veto
/// unwanted removals" UX, not "approve everything", which is unworkable on
/// a long book. User-added proposals also default to Accepted (the user
/// added them deliberately). Re-prompted LLM proposals default to
/// <see cref="Pending"/> so the user explicitly sees what was just added.
/// </summary>
public enum ProposalDecision { Pending, Accepted, Rejected }

/// <summary>
/// One chapter (zip entry) of a job that's awaiting review. Persists the
/// visible-text projection the LLM saw so the UI renders the same view, the
/// finalize step can re-resolve HTML offsets, and the user's text-selection
/// inputs map to ranges the server can interpret.
/// </summary>
public class ChapterReview
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public CleanJob Job { get; set; } = default!;

    /// <summary>Zip entry name, e.g. <c>EPUB/Text/0042_chapter_-_Beautiful_Monster.xhtml</c>.</summary>
    public string DocumentName { get; set; } = "";

    /// <summary>Visible-text projection (post <see cref="Services.EpubHandler.ExtractText"/>).</summary>
    public string VisibleText { get; set; } = "";

    /// <summary>Stable ordering key for the chapter list — the index in the EPUB's reading order.</summary>
    public int OrderIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ReviewProposal> Proposals { get; set; } = [];
}

public class ReviewProposal
{
    public long Id { get; set; }
    public long ChapterReviewId { get; set; }
    public ChapterReview Chapter { get; set; } = default!;

    /// <summary>Verbatim "remove" text — copied from the LLM's items[] or from a user selection.</summary>
    public string Text { get; set; } = "";

    public string Reason { get; set; } = "";

    public ProposalSource Source { get; set; }
    public ProposalDecision Decision { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

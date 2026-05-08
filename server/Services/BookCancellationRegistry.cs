using System.Collections.Concurrent;

namespace Tergeo.Server.Services;

/// <summary>
/// Process-wide registry of <see cref="CancellationTokenSource"/>s for AI runs
/// currently being executed by <see cref="BookProcessor"/>. The cancel
/// endpoint signals these tokens to interrupt LLM calls in-flight; the
/// processor registers/unregisters around each run.
///
/// In-memory only — a server restart kills any running task anyway, so
/// persisting these sources would buy nothing. Race-safe enough: TryRemove +
/// Cancel are both atomic, and double-cancel on a disposed source is a
/// no-op.
/// </summary>
public sealed class BookCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    /// <summary>
    /// Wraps <paramref name="parent"/> with a book-scoped CTS, registers it,
    /// and returns it. Caller must dispose (the Token's lifetime ends with
    /// the run); <see cref="Unregister"/> removes the entry from the map.
    /// </summary>
    public CancellationTokenSource Register(Guid bookId, CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        // If a previous Register slipped through without an Unregister
        // (rare — only on a thrown bare exception that bypassed the using),
        // dispose the orphan so we don't leak.
        _sources.AddOrUpdate(
            bookId,
            cts,
            (_, old) => { try { old.Dispose(); } catch { /* already disposed */ } return cts; });
        return cts;
    }

    /// <summary>Removes the entry. Doesn't dispose — the caller's `using` does.</summary>
    public void Unregister(Guid bookId) => _sources.TryRemove(bookId, out _);

    /// <summary>
    /// Best-effort: signals cancellation if the book is currently registered.
    /// Returns true when a token was found and signaled, false when the book
    /// wasn't running here (e.g. still in the queue).
    /// </summary>
    public bool Cancel(Guid bookId)
    {
        if (!_sources.TryGetValue(bookId, out var cts)) return false;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* race with run completion */ }
        return true;
    }
}

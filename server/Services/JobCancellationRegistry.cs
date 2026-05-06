using System.Collections.Concurrent;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Process-wide registry of <see cref="CancellationTokenSource"/>s for AI runs
/// that are currently being executed by <see cref="JobWorker"/>. The cancel
/// endpoint signals these tokens to interrupt LLM calls in-flight; the worker
/// registers/unregisters around each run.
///
/// In-memory only — a server restart kills any running task anyway, so
/// persisting these sources would buy nothing. Race-safe enough: TryRemove +
/// Cancel are both atomic, and double-cancel on a disposed source is a
/// no-op.
/// </summary>
public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    /// <summary>
    /// Wraps <paramref name="parent"/> with a job-scoped CTS, registers it,
    /// and returns it. Caller must dispose (the Token's lifetime ends with
    /// the run); <see cref="Unregister"/> removes the entry from the map.
    /// </summary>
    public CancellationTokenSource Register(Guid jobId, CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        // If a previous Register slipped through without an Unregister
        // (rare — only on a thrown bare exception that bypassed the using),
        // dispose the orphan so we don't leak.
        _sources.AddOrUpdate(
            jobId,
            cts,
            (_, old) => { try { old.Dispose(); } catch { /* already disposed */ } return cts; });
        return cts;
    }

    /// <summary>Removes the entry. Doesn't dispose — the caller's `using` does.</summary>
    public void Unregister(Guid jobId) => _sources.TryRemove(jobId, out _);

    /// <summary>
    /// Best-effort: signals cancellation if the job is currently registered.
    /// Returns true when a token was found and signaled, false when the job
    /// wasn't running here (e.g. still in the queue).
    /// </summary>
    public bool Cancel(Guid jobId)
    {
        if (!_sources.TryGetValue(jobId, out var cts)) return false;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* race with run completion */ }
        return true;
    }
}

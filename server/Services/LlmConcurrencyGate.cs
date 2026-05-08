namespace Tergeo.Server.Services;

/// <summary>
/// Single global concurrency gate for in-flight LLM calls. Every chapter
/// task across every running book waits on this gate before issuing its
/// LLM request, so the effective ceiling is <c>MaxWorkers</c> requests
/// regardless of how many books happen to be running at once.
///
/// The slot count is initialized at startup from
/// <see cref="ResolvedAppSettings.MaxWorkers"/> and can be live-updated
/// at runtime via <see cref="SetMax"/> — the admin-settings PUT calls
/// that with the new effective value, so DB-driven changes apply
/// immediately without a restart. (Env-pinned values still come from
/// configuration at startup; they don't change at runtime by definition.)
/// </summary>
public sealed class LlmConcurrencyGate
{
    private readonly SemaphoreSlim _sem;
    private readonly object _lock = new();
    private int _currentMax;

    /// <summary>The slot count currently in effect.</summary>
    public int MaxSlots
    {
        get { lock (_lock) return _currentMax; }
    }

    public LlmConcurrencyGate(int maxSlots)
    {
        var slots = Math.Max(1, maxSlots);
        _currentMax = slots;
        _sem = new SemaphoreSlim(slots, int.MaxValue);
    }

    public Task WaitAsync(CancellationToken ct) => _sem.WaitAsync(ct);
    public void Release() => _sem.Release();

    /// <summary>
    /// Live-resize the gate to <paramref name="newMax"/> slots.
    /// - Growing is instant: <see cref="SemaphoreSlim.Release(int)"/> hands
    ///   the extra slots to whatever requesters are currently queued.
    /// - Shrinking is best-effort: returns immediately while a background
    ///   task soaks up the surplus tokens as they become free. Already
    ///   in-flight callers finish their LLM call without interruption,
    ///   so the gate may briefly carry more concurrent calls than the
    ///   new ceiling. Acceptable for an admin-tuning operation.
    /// </summary>
    public void SetMax(int newMax)
    {
        var slots = Math.Max(1, newMax);
        int diff;
        lock (_lock)
        {
            if (slots == _currentMax) return;
            diff = slots - _currentMax;
            _currentMax = slots;
        }

        if (diff > 0)
        {
            _sem.Release(diff);
        }
        else
        {
            // Soak up the surplus on a background task so the caller doesn't
            // block on potentially in-flight LLM calls finishing.
            var toDrain = -diff;
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < toDrain; i++)
                    await _sem.WaitAsync();
            });
        }
    }

    /// <summary>
    /// Synchronous startup-time factory. Created from the resolver so env
    /// overrides win over the DB row exactly the same way every other
    /// AppSettings consumer does.
    /// </summary>
    public static async Task<LlmConcurrencyGate> CreateFromSettingsAsync(
        AppSettingsResolver resolver, CancellationToken ct = default)
    {
        var settings = await resolver.ResolveAsync(ct);
        return new LlmConcurrencyGate(settings.MaxWorkers);
    }
}

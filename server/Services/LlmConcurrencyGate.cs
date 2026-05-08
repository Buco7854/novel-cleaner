using Tergeo.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Tergeo.Server.Services;

/// <summary>
/// Single global concurrency gate for in-flight LLM calls. Every chapter
/// task across every running book waits on this gate before issuing its
/// LLM request, so the effective ceiling is <c>MaxWorkers</c> requests
/// regardless of how many books happen to be running at once.
///
/// The slot count is read once at startup from
/// <see cref="ResolvedAppSettings.MaxWorkers"/>. Changing the setting via
/// the admin UI persists to the DB but does not live-resize the gate —
/// the new ceiling takes effect on the next process restart. This keeps
/// the implementation simple (no shrink-blocking semantics) and matches
/// how the env-pinned override behaves anyway.
/// </summary>
public sealed class LlmConcurrencyGate
{
    private readonly SemaphoreSlim _sem;

    /// <summary>The slot count this gate was initialized with.</summary>
    public int MaxSlots { get; }

    public LlmConcurrencyGate(int maxSlots)
    {
        var slots = Math.Max(1, maxSlots);
        MaxSlots = slots;
        _sem = new SemaphoreSlim(slots, slots);
    }

    public Task WaitAsync(CancellationToken ct) => _sem.WaitAsync(ct);
    public void Release() => _sem.Release();

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

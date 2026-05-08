using System.Threading.Channels;

namespace NovelCleaner.Server.Services;

/// <summary>
/// In-memory queue of novel ids waiting to be picked up by
/// <see cref="NovelProcessor"/>. Endpoints enqueue when the user kicks off
/// an AI run; the processor drains the channel in the background.
/// </summary>
public sealed class NovelProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public ValueTask EnqueueAsync(Guid novelId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(novelId, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

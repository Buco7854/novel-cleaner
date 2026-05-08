using System.Threading.Channels;

namespace Tergeo.Server.Services;

/// <summary>
/// In-memory queue of book ids waiting to be picked up by
/// <see cref="BookProcessor"/>. Endpoints enqueue when the user kicks off
/// an AI run; the processor drains the channel in the background.
/// </summary>
public sealed class BookProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public ValueTask EnqueueAsync(Guid bookId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(bookId, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

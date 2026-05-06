using System.Threading.Channels;

namespace NovelCleaner.Server.Services;

public sealed class JobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WhaleTracker.API.Services;

public interface ITraderDiscoveryJobQueue
{
    bool Enqueue(long runId);
    ValueTask<long> DequeueAsync(CancellationToken cancellationToken);
    void Complete(long runId);
}

public sealed class TraderDiscoveryJobQueue : ITraderDiscoveryJobQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<long, byte> _pending = new();

    public bool Enqueue(long runId)
    {
        if (!_pending.TryAdd(runId, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(runId))
        {
            return true;
        }

        _pending.TryRemove(runId, out _);
        return false;
    }

    public ValueTask<long> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public void Complete(long runId) => _pending.TryRemove(runId, out _);
}

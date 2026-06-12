using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WhaleTracker.API.Services;

public interface ITraderPerformanceJobQueue
{
    bool Enqueue(long scanId);
    ValueTask<long> DequeueAsync(CancellationToken cancellationToken);
    void Complete(long scanId);
}

public sealed class TraderPerformanceJobQueue : ITraderPerformanceJobQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<long, byte> _pending = new();

    public bool Enqueue(long scanId)
    {
        if (!_pending.TryAdd(scanId, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(scanId))
        {
            return true;
        }

        _pending.TryRemove(scanId, out _);
        return false;
    }

    public ValueTask<long> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public void Complete(long scanId) => _pending.TryRemove(scanId, out _);
}

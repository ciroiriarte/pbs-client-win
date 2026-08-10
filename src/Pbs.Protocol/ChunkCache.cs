using Pbs.Abstractions;

namespace Pbs.Protocol;

public sealed class ChunkCache
{
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly long _maxBytes;
    private readonly Dictionary<ChunkDigest, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public ChunkCache(long maxBytes = DefaultMaxBytes)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "cache byte budget must be non-negative");
        _maxBytes = maxBytes;
    }

    public bool TryGet(ChunkDigest digest, out byte[] value)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(digest, out var node))
            {
                value = Array.Empty<byte>();
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Add(ChunkDigest digest, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            if (_entries.TryGetValue(digest, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            if (value.LongLength > _maxBytes)
                return;

            var entry = new Entry(digest, value);
            var node = new LinkedListNode<Entry>(entry);
            _lru.AddFirst(node);
            _entries[digest] = node;
            _bytes += value.LongLength;

            EvictPastBudget();
        }
    }

    public async ValueTask<byte[]> GetOrAddAsync(
        ChunkDigest digest,
        Func<ChunkDigest, CancellationToken, ValueTask<byte[]>> factory,
        CancellationToken ct = default)
    {
        if (TryGet(digest, out var cached))
            return cached;

        var value = await factory(digest, ct);
        Add(digest, value);
        return value;
    }

    private void EvictPastBudget()
    {
        while (_bytes > _maxBytes && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _entries.Remove(last.Value.Digest);
            _bytes -= last.Value.Value.LongLength;
        }
    }

    private sealed record Entry(ChunkDigest Digest, byte[] Value);
}

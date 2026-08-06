using System.Collections.Concurrent;

namespace OokiGrader.Host.Middleware;

public sealed class IdempotencyLockProvider
{
    private readonly ConcurrentDictionary<string, LockEntry> _locks =
        new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(
        string key,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _locks.GetOrAdd(key, _ => new LockEntry());
            Interlocked.Increment(ref entry.ReferenceCount);
            if (_locks.TryGetValue(key, out var current)
                && ReferenceEquals(entry, current))
            {
                try
                {
                    await entry.Gate.WaitAsync(cancellationToken);
                    return new Lease(this, key, entry);
                }
                catch
                {
                    if (Interlocked.Decrement(ref entry.ReferenceCount) == 0)
                    {
                        _locks.TryRemove(
                            new KeyValuePair<string, LockEntry>(key, entry));
                    }

                    throw;
                }
            }

            Interlocked.Decrement(ref entry.ReferenceCount);
        }
    }

    private sealed class Lease(
        IdempotencyLockProvider owner,
        string key,
        LockEntry entry) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            entry.Gate.Release();
            if (Interlocked.Decrement(ref entry.ReferenceCount) == 0)
            {
                owner._locks.TryRemove(
                    new KeyValuePair<string, LockEntry>(key, entry));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class LockEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount;
    }
}

using System.Collections.Concurrent;
using OokiGrader.Application.Abstractions;

namespace OokiGrader.Host.Uploads;

/// <summary>
/// Serializes physical mutation of one content-addressed object with the final
/// database reference check that authorizes that mutation.
/// </summary>
public sealed class ContentObjectLockProvider
{
    private readonly ConcurrentDictionary<string, LockEntry> _locks =
        new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        ContentStorageClass storageClass,
        string sha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var key = $"{storageClass}\n{sha256}";
        while (true)
        {
            var entry = _locks.GetOrAdd(key, static _ => new LockEntry());
            lock (entry.SyncRoot)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.ReferenceCount++;
            }

            try
            {
                await entry.Gate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return new Lease(this, key, entry);
            }
            catch
            {
                ReleaseReference(key, entry);
                throw;
            }
        }
    }

    private void ReleaseReference(string key, LockEntry entry)
    {
        lock (entry.SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                // Retire and remove while holding the same monitor used by an
                // acquirer to adopt this entry. A caller that fetched the old
                // entry before removal will observe Retired and retry against
                // the dictionary instead of creating a second live semaphore.
                entry.Retired = true;
                _locks.TryRemove(
                    new KeyValuePair<string, LockEntry>(key, entry));
            }
        }
    }

    private sealed class Lease(
        ContentObjectLockProvider owner,
        string key,
        LockEntry entry) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            entry.Gate.Release();
            owner.ReleaseReference(key, entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LockEntry
    {
        public object SyncRoot { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount;
        public bool Retired;
    }
}

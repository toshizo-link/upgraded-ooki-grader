using OokiGrader.Application.Abstractions;

namespace OokiGrader.Infrastructure.Persistence;

public sealed class SemaphoreWriteCoordinator : IWriteCoordinator, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }
}

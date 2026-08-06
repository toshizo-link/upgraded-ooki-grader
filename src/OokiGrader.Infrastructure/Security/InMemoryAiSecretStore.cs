using System.Security.Cryptography;
using OokiGrader.Application.Abstractions;

namespace OokiGrader.Infrastructure.Security;

/// <summary>
/// Process-local implementation intended only for tests and non-production
/// development on platforms where Windows DPAPI is unavailable.
/// </summary>
public sealed class InMemoryAiSecretStore : IAiSecretStore, IDisposable
{
    private const string ReferenceScheme = "memory-v1";
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _secrets =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public Task<AiSecretReference> WriteAsync(
        string ownerId,
        long credentialRevision,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var reference = AiSecretStoreValidation.CreateReference(
            ReferenceScheme,
            ownerId,
            credentialRevision);
        var encoded = AiSecretStoreValidation.EncodeSecret(secret.Span);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_secrets.Remove(reference.Value, out var previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            _secrets.Add(reference.Value, encoded);
        }

        return Task.FromResult(reference);
    }

    public Task<AiSecretLease> ReadAsync(
        AiSecretReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var parsed = AiSecretStoreValidation.ParseReference(
            reference,
            ReferenceScheme);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_secrets.TryGetValue(parsed.Value, out var bytes))
            {
                throw new KeyNotFoundException(
                    "The requested AI secret is unavailable.");
            }

            return Task.FromResult(AiSecretLease.CopyFrom(bytes));
        }
    }

    public Task<bool> DeleteAsync(
        AiSecretReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var parsed = AiSecretStoreValidation.ParseReference(
            reference,
            ReferenceScheme);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_secrets.Remove(parsed.Value, out var removed))
            {
                return Task.FromResult(false);
            }

            CryptographicOperations.ZeroMemory(removed);
            return Task.FromResult(true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var secret in _secrets.Values)
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            _secrets.Clear();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

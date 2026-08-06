using System.Security.Cryptography;

namespace OokiGrader.Application.Abstractions;

/// <summary>
/// An opaque locator for a provider credential. The value is safe to persist,
/// but callers must not interpret it as a filesystem path.
/// </summary>
public sealed record AiSecretReference(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Provides temporary access to UTF-8 secret bytes and clears its buffer when
/// disposed. Callers should keep the lease lifetime as short as possible.
/// </summary>
public sealed class AiSecretLease : IDisposable
{
    private byte[]? _bytes;

    private AiSecretLease(byte[] bytes)
    {
        _bytes = bytes;
    }

    public ReadOnlyMemory<byte> Utf8Bytes =>
        _bytes is not null
            ? _bytes
            : throw new ObjectDisposedException(nameof(AiSecretLease));

    public static AiSecretLease CopyFrom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A secret lease cannot be empty.", nameof(bytes));
        }

        return new AiSecretLease(bytes.ToArray());
    }

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>
/// Stores provider credentials outside application metadata. Implementations
/// must return opaque references and must never expose plaintext through logs
/// or exception messages.
/// </summary>
public interface IAiSecretStore
{
    Task<AiSecretReference> WriteAsync(
        string ownerId,
        long credentialRevision,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default);

    Task<AiSecretLease> ReadAsync(
        AiSecretReference reference,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        AiSecretReference reference,
        CancellationToken cancellationToken = default);
}

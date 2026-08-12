using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using OokiGrader.Application.Abstractions;

namespace OokiGrader.Infrastructure.Security;

/// <summary>
/// Persists development-only provider credentials in authenticated envelopes
/// protected by ASP.NET Core Data Protection. The key ring and this envelope
/// root must remain private to the operating-system user running the host.
/// </summary>
public sealed class DataProtectionFileAiSecretStore : IAiSecretStore
{
    private const string ReferenceScheme = "devfile-v1";
    private const string ProtectorPurpose =
        "OokiGrader.AiSecret.DataProtectionFile.devfile-v1";
    private const byte EnvelopeVersion = 1;
    private const int EnvelopeHeaderLength = 13;
    private static readonly byte[] EnvelopeMagic = "OOKIDP01"u8.ToArray();
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _referenceLocks =
        new(StringComparer.Ordinal);

    public DataProtectionFileAiSecretStore(
        DataProtectionFileAiSecretStoreOptions options,
        IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        if (string.IsNullOrWhiteSpace(options.RootPath)
            || !Path.IsPathFullyQualified(options.RootPath))
        {
            throw new ArgumentException(
                "The AI secret root must be an absolute path.",
                nameof(options));
        }

        _rootPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.RootPath));
        var filesystemRoot = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(_rootPath)
            ?? throw new ArgumentException(
                "The AI secret root is invalid.",
                nameof(options)));
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(_rootPath, filesystemRoot, _pathComparison))
        {
            throw new ArgumentException(
                "The AI secret store cannot use a filesystem root.",
                nameof(options));
        }

        _rootPrefix = _rootPath + Path.DirectorySeparatorChar;
        _dataProtectionProvider = dataProtectionProvider;

        Directory.CreateDirectory(_rootPath);
        EnsureSafeExistingPath(_rootPath);
        RestrictDirectoryToOwner(_rootPath);
    }

    public async Task<AiSecretReference> WriteAsync(
        string ownerId,
        long credentialRevision,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = AiSecretStoreValidation.CreateReference(
            ReferenceScheme,
            ownerId,
            credentialRevision);
        var plaintext = AiSecretStoreValidation.EncodeSecret(secret.Span);
        byte[]? protectedBytes = null;
        byte[]? envelope = null;

        try
        {
            protectedBytes = CreateProtector(reference.Value).Protect(plaintext);
            envelope = BuildEnvelope(protectedBytes);
            await WriteAtomicallyAsync(reference, envelope, cancellationToken)
                .ConfigureAwait(false);
            return reference;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (envelope is not null)
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
    }

    public async Task<AiSecretLease> ReadAsync(
        AiSecretReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsLegacyMemoryReference(reference))
        {
            throw new KeyNotFoundException(
                "The requested AI secret is unavailable.");
        }

        var parsed = AiSecretStoreValidation.ParseReference(
            reference,
            ReferenceScheme);
        var gate = _referenceLocks.GetOrAdd(
            parsed.Value,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        byte[]? envelope = null;
        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        try
        {
            var path = ResolvePath(parsed);
            var directory = Path.GetDirectoryName(path)!;
            EnsureSafeExistingPath(directory);
            if (!Directory.Exists(directory))
            {
                throw new KeyNotFoundException(
                    "The requested AI secret is unavailable.");
            }

            RestrictDirectoryToOwner(directory);
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException(
                    "The requested AI secret is unavailable.");
            }

            RejectReparsePoint(path);
            RestrictFileToOwner(path);
            var length = new FileInfo(path).Length;
            if (length is < EnvelopeHeaderLength
                or > AiSecretStoreValidation.MaximumEnvelopeBytes)
            {
                throw new InvalidDataException(
                    "The protected AI secret envelope has an invalid length.");
            }

            envelope = GC.AllocateUninitializedArray<byte>(checked((int)length));
            await using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.ReadExactlyAsync(envelope, cancellationToken)
                    .ConfigureAwait(false);
            }

            protectedBytes = ParseEnvelope(envelope);
            plaintext = CreateProtector(parsed.Value).Unprotect(protectedBytes);
            AiSecretStoreValidation.ValidateDecodedSecret(plaintext);
            return AiSecretLease.CopyFrom(plaintext);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (envelope is not null)
            {
                CryptographicOperations.ZeroMemory(envelope);
            }

            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        AiSecretReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsLegacyMemoryReference(reference))
        {
            return false;
        }

        var parsed = AiSecretStoreValidation.ParseReference(
            reference,
            ReferenceScheme);
        var gate = _referenceLocks.GetOrAdd(
            parsed.Value,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ResolvePath(parsed);
            var directory = Path.GetDirectoryName(path)!;
            EnsureSafeExistingPath(directory);
            if (!Directory.Exists(directory))
            {
                return false;
            }

            RestrictDirectoryToOwner(directory);
            if (!File.Exists(path))
            {
                return false;
            }

            RejectReparsePoint(path);
            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteAtomicallyAsync(
        AiSecretReference reference,
        ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken)
    {
        var parsed = AiSecretStoreValidation.ParseReference(
            reference,
            ReferenceScheme);
        var gate = _referenceLocks.GetOrAdd(
            parsed.Value,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? temporaryPath = null;
        try
        {
            var destinationPath = ResolvePath(parsed);
            var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
            Directory.CreateDirectory(destinationDirectory);
            EnsureSafeExistingPath(destinationDirectory);
            RestrictDirectoryToOwner(destinationDirectory);

            temporaryPath = Path.Combine(
                destinationDirectory,
                $".{parsed.CredentialRevision:D20}.{Guid.NewGuid():N}.tmp");
            EnsureUnderRoot(temporaryPath);

            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = OwnerFileMode;
            }

            await using (var stream = new FileStream(temporaryPath, streamOptions))
            {
                await stream.WriteAsync(envelope, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            RestrictFileToOwner(temporaryPath);
            if (File.Exists(destinationPath))
            {
                RejectReparsePoint(destinationPath);
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }

            temporaryPath = null;
            RestrictFileToOwner(destinationPath);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporary(temporaryPath);
            }

            gate.Release();
        }
    }

    private IDataProtector CreateProtector(string reference) =>
        _dataProtectionProvider
            .CreateProtector(ProtectorPurpose)
            .CreateProtector(reference);

    private static bool IsLegacyMemoryReference(AiSecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Value?.StartsWith("memory-v1/", StringComparison.Ordinal)
            != true)
        {
            return false;
        }

        _ = AiSecretStoreValidation.ParseReference(reference, "memory-v1");
        return true;
    }

    private string ResolvePath(ParsedAiSecretReference reference)
    {
        var path = Path.GetFullPath(Path.Combine(
            _rootPath,
            reference.OwnerId,
            $"{reference.CredentialRevision:D20}.secret"));
        EnsureUnderRoot(path);
        return path;
    }

    private void EnsureUnderRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_rootPrefix, _pathComparison))
        {
            throw new InvalidOperationException(
                "The AI secret path escaped its managed root.");
        }
    }

    private void EnsureSafeExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, _rootPath, _pathComparison))
        {
            EnsureUnderRoot(fullPath);
        }

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            RejectReparsePoint(fullPath);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "AI secret storage does not permit reparse points or symbolic links.");
        }
    }

    private static void RestrictDirectoryToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerDirectoryMode);
        }
    }

    private static void RestrictFileToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerFileMode);
        }
    }

    private static byte[] BuildEnvelope(ReadOnlySpan<byte> protectedBytes)
    {
        if (protectedBytes.IsEmpty
            || protectedBytes.Length >
            AiSecretStoreValidation.MaximumEnvelopeBytes - EnvelopeHeaderLength)
        {
            throw new CryptographicException(
                "Data Protection returned an invalid protected secret length.");
        }

        var envelope = GC.AllocateUninitializedArray<byte>(
            checked(EnvelopeHeaderLength + protectedBytes.Length));
        EnvelopeMagic.CopyTo(envelope, 0);
        envelope[EnvelopeMagic.Length] = EnvelopeVersion;
        BinaryPrimitives.WriteInt32LittleEndian(
            envelope.AsSpan(EnvelopeMagic.Length + 1, sizeof(int)),
            protectedBytes.Length);
        protectedBytes.CopyTo(envelope.AsSpan(EnvelopeHeaderLength));
        return envelope;
    }

    private static byte[] ParseEnvelope(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < EnvelopeHeaderLength
            || !CryptographicOperations.FixedTimeEquals(
                envelope[..EnvelopeMagic.Length],
                EnvelopeMagic)
            || envelope[EnvelopeMagic.Length] != EnvelopeVersion)
        {
            throw new InvalidDataException(
                "The protected AI secret envelope header is invalid.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            envelope.Slice(EnvelopeMagic.Length + 1, sizeof(int)));
        if (payloadLength <= 0
            || payloadLength != envelope.Length - EnvelopeHeaderLength)
        {
            throw new InvalidDataException(
                "The protected AI secret envelope payload is invalid.");
        }

        return envelope[EnvelopeHeaderLength..].ToArray();
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

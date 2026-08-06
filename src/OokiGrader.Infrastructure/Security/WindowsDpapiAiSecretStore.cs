using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OokiGrader.Application.Abstractions;

namespace OokiGrader.Infrastructure.Security;

/// <summary>
/// Persists provider credentials as Windows DPAPI CurrentUser envelopes.
/// Production must run this store as the dedicated Windows service identity
/// and protect the configured root with service/admin-only ACLs.
/// </summary>
public sealed class WindowsDpapiAiSecretStore : IAiSecretStore
{
    private const string ReferenceScheme = "dpapi-v1";
    private const byte EnvelopeVersion = 1;
    private const int EnvelopeHeaderLength = 13;
    private static readonly byte[] EnvelopeMagic = "OOKIAI01"u8.ToArray();

    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;
    private readonly IAiSecretProtector _protector;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _referenceLocks =
        new(StringComparer.Ordinal);

    public WindowsDpapiAiSecretStore(WindowsDpapiAiSecretStoreOptions options)
        : this(options, CreateWindowsProtector())
    {
    }

    internal WindowsDpapiAiSecretStore(
        WindowsDpapiAiSecretStoreOptions options,
        IAiSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);

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
        _protector = protector;

        Directory.CreateDirectory(_rootPath);
        EnsureSafeExistingPath(_rootPath);
    }

    public async Task<AiSecretReference> WriteAsync(
        string ownerId,
        long credentialRevision,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        var reference = AiSecretStoreValidation.CreateReference(
            ReferenceScheme,
            ownerId,
            credentialRevision);
        var plaintext = AiSecretStoreValidation.EncodeSecret(secret.Span);
        byte[]? protectedBytes = null;
        byte[]? envelope = null;

        try
        {
            var entropy = ComputeEntropy(reference.Value);
            try
            {
                protectedBytes = _protector.Protect(plaintext, entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }

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
            EnsureSafeExistingPath(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException(
                    "The requested AI secret is unavailable.");
            }

            RejectReparsePoint(path);
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
            var entropy = ComputeEntropy(parsed.Value);
            try
            {
                plaintext = _protector.Unprotect(protectedBytes, entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }

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
            EnsureSafeExistingPath(Path.GetDirectoryName(path)!);
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

            temporaryPath = Path.Combine(
                destinationDirectory,
                $".{parsed.CredentialRevision:D20}.{Guid.NewGuid():N}.tmp");
            EnsureUnderRoot(temporaryPath);

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(envelope, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

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

    private static byte[] ComputeEntropy(string reference)
    {
        var prefix = "OokiGrader.AiSecret.dpapi-v1\0"u8;
        var referenceBytes = Encoding.UTF8.GetBytes(reference);
        var input = GC.AllocateUninitializedArray<byte>(
            checked(prefix.Length + referenceBytes.Length));
        try
        {
            prefix.CopyTo(input);
            referenceBytes.CopyTo(input.AsSpan(prefix.Length));
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(referenceBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static byte[] BuildEnvelope(ReadOnlySpan<byte> protectedBytes)
    {
        if (protectedBytes.IsEmpty
            || protectedBytes.Length >
            AiSecretStoreValidation.MaximumEnvelopeBytes - EnvelopeHeaderLength)
        {
            throw new CryptographicException(
                "Windows DPAPI returned an invalid protected secret length.");
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

    private static WindowsCurrentUserDpapiSecretProtector CreateWindowsProtector()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows DPAPI AI secret storage is available only on Windows.");
        }

        return new WindowsCurrentUserDpapiSecretProtector();
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

internal interface IAiSecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);

    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes, ReadOnlySpan<byte> entropy);
}

internal sealed class WindowsCurrentUserDpapiSecretProtector : IAiSecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
    {
        EnsureWindows();
        var input = NativeDataBlob.Allocate(plaintext);
        var optionalEntropy = NativeDataBlob.Allocate(entropy);
        NativeDataBlob output = default;
        try
        {
            if (!CryptProtectData(
                    ref input,
                    null,
                    ref optionalEntropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw DpapiFailure();
            }

            return output.CopyToManaged();
        }
        finally
        {
            input.FreeHGlobalAndClear();
            optionalEntropy.FreeHGlobalAndClear();
            output.FreeLocalAndClear();
        }
    }

    public byte[] Unprotect(
        ReadOnlySpan<byte> protectedBytes,
        ReadOnlySpan<byte> entropy)
    {
        EnsureWindows();
        var input = NativeDataBlob.Allocate(protectedBytes);
        var optionalEntropy = NativeDataBlob.Allocate(entropy);
        NativeDataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    out description,
                    ref optionalEntropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw DpapiFailure();
            }

            return output.CopyToManaged();
        }
        finally
        {
            input.FreeHGlobalAndClear();
            optionalEntropy.FreeHGlobalAndClear();
            output.FreeLocalAndClear();
            if (description != IntPtr.Zero)
            {
                _ = LocalFree(description);
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows DPAPI is available only on Windows.");
        }
    }

    private static CryptographicException DpapiFailure()
    {
        var error = Marshal.GetLastPInvokeError();
        return new CryptographicException(
            $"Windows DPAPI failed with operating-system error {error}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDataBlob
    {
        public int ByteCount;
        public IntPtr Data;

        public static NativeDataBlob Allocate(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return default;
            }

            var managed = bytes.ToArray();
            try
            {
                var pointer = Marshal.AllocHGlobal(managed.Length);
                Marshal.Copy(managed, 0, pointer, managed.Length);
                return new NativeDataBlob
                {
                    ByteCount = managed.Length,
                    Data = pointer,
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(managed);
            }
        }

        public readonly byte[] CopyToManaged()
        {
            if (ByteCount <= 0 || Data == IntPtr.Zero)
            {
                throw new CryptographicException(
                    "Windows DPAPI returned an empty result.");
            }

            var bytes = GC.AllocateUninitializedArray<byte>(ByteCount);
            Marshal.Copy(Data, bytes, 0, ByteCount);
            return bytes;
        }

        public void FreeHGlobalAndClear()
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            ClearUnmanaged(Data, ByteCount);
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
            ByteCount = 0;
        }

        public void FreeLocalAndClear()
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            ClearUnmanaged(Data, ByteCount);
            _ = LocalFree(Data);
            Data = IntPtr.Zero;
            ByteCount = 0;
        }

        private static void ClearUnmanaged(IntPtr pointer, int byteCount)
        {
            for (var index = 0; index < byteCount; index++)
            {
                Marshal.WriteByte(pointer, index, 0);
            }
        }
    }

#pragma warning disable SYSLIB1054 // DPAPI is exposed by Win32 and has no managed BCL API.
    [DllImport(
        "Crypt32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref NativeDataBlob dataIn,
        string? dataDescription,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out NativeDataBlob dataOut);

    [DllImport(
        "Crypt32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref NativeDataBlob dataIn,
        out IntPtr dataDescription,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out NativeDataBlob dataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
}

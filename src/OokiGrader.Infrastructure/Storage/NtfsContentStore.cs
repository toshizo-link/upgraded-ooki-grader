using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OokiGrader.Application.Abstractions;

namespace OokiGrader.Infrastructure.Storage;

public sealed partial class NtfsContentStore : IContentStore
{
    private const int BufferSize = 128 * 1024;
    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly string _incomingPath;
    private readonly StringComparison _pathComparison;

    public NtfsContentStore(ContentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.RootPath) ||
            !Path.IsPathFullyQualified(options.RootPath))
        {
            throw new ArgumentException(
                "The content store root must be an absolute path.",
                nameof(options));
        }

        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        if (_rootPath.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetPathRoot(_rootPath)!),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The content store may not use a filesystem root directly.",
                nameof(options));
        }

        _rootPrefix = _rootPath + Path.DirectorySeparatorChar;
        _incomingPath = Path.Combine(_rootPath, "incoming", "objects");
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        Directory.CreateDirectory(_rootPath);
        RejectReparsePoint(_rootPath);
        Directory.CreateDirectory(_incomingPath);
        EnsureSafeExistingPath(_incomingPath);
    }

    public async Task<ContentWriteResult> PutAsync(
        Stream source,
        ContentStorageClass storageClass,
        string verifiedExtension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        var extension = NormalizeExtension(verifiedExtension);
        var temporaryPath = Path.Combine(_incomingPath, $"{Guid.NewGuid():N}.part");
        EnsureUnderRoot(temporaryPath);

        string sha256;
        long bytes = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    incrementalHash.AppendData(buffer, 0, read);
                    await temporary.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                    bytes = checked(bytes + read);
                }

                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
            }

            sha256 = Convert.ToHexString(incrementalHash.GetHashAndReset())
                .ToLowerInvariant();
        }
        catch
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        var locator = new ContentObjectLocator(storageClass, sha256, bytes, extension);
        var relativePath = BuildRelativePath(locator);
        var destinationPath = ResolvePath(locator);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        EnsureSafeExistingPath(destinationDirectory);

        var deduplicated = false;
        try
        {
            if (File.Exists(destinationPath))
            {
                VerifyExistingObject(destinationPath, bytes);
                TryDeleteTemporary(temporaryPath);
                deduplicated = true;
            }
            else
            {
                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    VerifyExistingObject(destinationPath, bytes);
                    TryDeleteTemporary(temporaryPath);
                    deduplicated = true;
                }
            }
        }
        catch
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }

        return new ContentWriteResult(locator, relativePath, deduplicated);
    }

    public Task<Stream> OpenReadAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(locator);
        EnsureSafeExistingPath(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The requested content object is unavailable.");
        }

        RejectReparsePoint(path);
        VerifyExistingObject(path, locator.Bytes);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(locator);
        EnsureSafeExistingPath(Path.GetDirectoryName(path)!);
        return Task.FromResult(File.Exists(path) && new FileInfo(path).Length == locator.Bytes);
    }

    public Task DeleteAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(locator);
        EnsureSafeExistingPath(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            RejectReparsePoint(path);
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(ContentObjectLocator locator)
    {
        ValidateLocator(locator);
        var path = Path.GetFullPath(Path.Combine(
            _rootPath,
            BuildRelativePath(locator).Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderRoot(path);
        return path;
    }

    private static string BuildRelativePath(ContentObjectLocator locator)
    {
        var classFolder = locator.StorageClass switch
        {
            ContentStorageClass.ManagedScanOriginal => "scan/original",
            ContentStorageClass.ManagedScanDerived => "scan/derived",
            ContentStorageClass.TemplateSource => "template/source",
            ContentStorageClass.TemplateDerived => "template/derived",
            ContentStorageClass.ResultReport => "report",
            ContentStorageClass.AiDiagnostic => "diagnostic",
            ContentStorageClass.Temporary => "temporary",
            _ => throw new ArgumentOutOfRangeException(
                nameof(locator),
                "Unknown storage class.")
        };

        return $"{classFolder}/{locator.Sha256[..2]}/{locator.Sha256[2..4]}/" +
            $"{locator.Sha256}.{locator.Extension}";
    }

    private static void ValidateLocator(ContentObjectLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        if (!Sha256Pattern().IsMatch(locator.Sha256))
        {
            throw new ArgumentException(
                "The object hash must be 64 lowercase hexadecimal characters.",
                nameof(locator));
        }

        if (locator.Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(locator),
                "The object byte count cannot be negative.");
        }

        _ = NormalizeExtension(locator.Extension);
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.Trim().TrimStart('.').ToLowerInvariant();

        if (normalized.Length > 24 || !ExtensionPattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "The verified extension contains unsupported characters.",
                nameof(extension));
        }

        return normalized;
    }

    private void EnsureUnderRoot(string fullPath)
    {
        if (!fullPath.StartsWith(_rootPrefix, _pathComparison))
        {
            throw new UnauthorizedAccessException(
                "The resolved content path is outside the configured root.");
        }
    }

    private void EnsureSafeExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureUnderRoot(fullPath);

        var current = new DirectoryInfo(fullPath);
        while (current is not null &&
               (current.FullName.Equals(_rootPath, _pathComparison) ||
                current.FullName.StartsWith(_rootPrefix, _pathComparison)))
        {
            if (current.Exists)
            {
                RejectReparsePoint(current.FullName);
            }

            if (current.FullName.Equals(_rootPath, _pathComparison))
            {
                break;
            }

            current = current.Parent;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "Content store paths may not traverse reparse points or symbolic links.");
        }
    }

    private static void VerifyExistingObject(string path, long expectedBytes)
    {
        RejectReparsePoint(path);
        if (new FileInfo(path).Length != expectedBytes)
        {
            throw new IOException(
                "An existing object has the expected hash path but a different byte count.");
        }
    }

    private static void TryDeleteTemporary(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch
        {
            // Startup temporary-file reconciliation handles an undeletable abandoned part.
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(
        "^[a-z0-9]+(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();
}

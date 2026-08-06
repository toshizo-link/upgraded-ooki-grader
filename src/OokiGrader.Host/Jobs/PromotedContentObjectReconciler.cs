using System.Buffers;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed record PromotedContentObjectReconciliationResult(
    int QuarantinedObjectCount,
    int RestoredObjectCount,
    int DeletedQuarantinedObjectCount,
    int FailureCount)
{
    public static PromotedContentObjectReconciliationResult Empty { get; } =
        new(0, 0, 0, 0);
}

public sealed record PromotedContentObjectCandidate(
    string AbsolutePath,
    string RelativePath,
    string StorageClass,
    string NamedSha256,
    string Extension,
    long Bytes,
    DateTimeOffset LastWriteAt);

public sealed record QuarantinedContentObjectCandidate(
    string AbsolutePath,
    string SourceRelativePath,
    string StorageClass,
    string NamedSha256,
    string Extension,
    long Bytes,
    DateTimeOffset QuarantinedAt);

public interface IPromotedContentObjectFileSystem
{
    IReadOnlyList<PromotedContentObjectCandidate> DiscoverPromotedObjects(
        string objectStoreRoot,
        DateTimeOffset cutoff,
        int maximumEntries,
        int maximumCandidates);

    IReadOnlyList<QuarantinedContentObjectCandidate> DiscoverQuarantinedObjects(
        string quarantineRoot,
        int maximumEntries,
        int maximumCandidates);

    Task<string> ComputeSha256Async(
        string root,
        string absolutePath,
        CancellationToken cancellationToken);

    Task<QuarantinedContentObjectCandidate> QuarantineAsync(
        string objectStoreRoot,
        string quarantineRoot,
        PromotedContentObjectCandidate candidate,
        string actualSha256,
        DateTimeOffset quarantinedAt,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        string objectStoreRoot,
        string quarantineRoot,
        QuarantinedContentObjectCandidate candidate,
        CancellationToken cancellationToken);

    void DeleteQuarantined(
        string quarantineRoot,
        QuarantinedContentObjectCandidate candidate);
}

public sealed class NtfsPromotedContentObjectFileSystem :
    IPromotedContentObjectFileSystem
{
    private const int BufferSize = 128 * 1024;
    private const string QuarantineSuffix = ".orphan";

    private static readonly IReadOnlyList<StorageClassDirectory> StorageDirectories =
    [
        new(ContentStorageClass.ManagedScanOriginal, "scan/original"),
        new(ContentStorageClass.ManagedScanDerived, "scan/derived"),
        new(ContentStorageClass.TemplateSource, "template/source"),
        new(ContentStorageClass.TemplateDerived, "template/derived"),
        new(ContentStorageClass.ResultReport, "report"),
        new(ContentStorageClass.AiDiagnostic, "diagnostic"),
        new(ContentStorageClass.Temporary, "temporary"),
    ];

    public IReadOnlyList<PromotedContentObjectCandidate> DiscoverPromotedObjects(
        string objectStoreRoot,
        DateTimeOffset cutoff,
        int maximumEntries,
        int maximumCandidates)
    {
        ValidateLimits(maximumEntries, maximumCandidates);
        var root = NormalizeSafeRoot(objectStoreRoot);
        return Discover(
                root,
                suffix: string.Empty,
                cutoff,
                maximumEntries,
                maximumCandidates)
            .Select(candidate => new PromotedContentObjectCandidate(
                candidate.AbsolutePath,
                candidate.RelativePath,
                candidate.StorageClass,
                candidate.NamedSha256,
                candidate.Extension,
                candidate.Bytes,
                candidate.LastWriteAt))
            .ToArray();
    }

    public IReadOnlyList<QuarantinedContentObjectCandidate>
        DiscoverQuarantinedObjects(
            string quarantineRoot,
            int maximumEntries,
            int maximumCandidates)
    {
        ValidateLimits(maximumEntries, maximumCandidates);
        var root = NormalizeSafeRoot(quarantineRoot);
        return Discover(
                root,
                QuarantineSuffix,
                cutoff: null,
                maximumEntries,
                maximumCandidates)
            .Select(candidate => new QuarantinedContentObjectCandidate(
                candidate.AbsolutePath,
                candidate.RelativePath,
                candidate.StorageClass,
                candidate.NamedSha256,
                candidate.Extension,
                candidate.Bytes,
                candidate.LastWriteAt))
            .ToArray();
    }

    public async Task<string> ComputeSha256Async(
        string root,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        var safeRoot = NormalizeSafeRoot(root);
        var path = EnsureAbsolutePathUnderRoot(safeRoot, absolutePath);
        EnsureSafeDirectoryChain(Path.GetDirectoryName(path)!, safeRoot);
        RejectReparsePoint(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public async Task<QuarantinedContentObjectCandidate> QuarantineAsync(
        string objectStoreRoot,
        string quarantineRoot,
        PromotedContentObjectCandidate candidate,
        string actualSha256,
        DateTimeOffset quarantinedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateSha256(actualSha256);
        var sourceRoot = NormalizeSafeRoot(objectStoreRoot);
        var targetRoot = NormalizeSafeRoot(quarantineRoot, create: true);
        var sourcePath = ResolveExpectedPath(sourceRoot, candidate.RelativePath);
        if (!string.Equals(
                sourcePath,
                Path.GetFullPath(candidate.AbsolutePath),
                PathComparison()))
        {
            throw new InvalidOperationException(
                "The promoted object no longer has its canonical path.");
        }

        EnsureExpectedFile(sourcePath, candidate.Bytes);
        EnsureSafeDirectoryChain(
            Path.GetDirectoryName(sourcePath)!,
            sourceRoot);
        var currentHash = await ComputeSha256Async(
                sourceRoot,
                sourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentHash, actualSha256, StringComparison.Ordinal))
        {
            throw new IOException(
                "The promoted object changed while it was being reconciled.");
        }

        var targetRelativePath = candidate.RelativePath + QuarantineSuffix;
        var targetPath = ResolveExpectedPath(targetRoot, targetRelativePath);
        await CopyDurablyIfNeededAsync(
                sourcePath,
                targetPath,
                targetRoot,
                candidate.Bytes,
                actualSha256,
                cancellationToken)
            .ConfigureAwait(false);
        File.Delete(sourcePath);
        File.SetLastWriteTimeUtc(targetPath, quarantinedAt.UtcDateTime);

        return new QuarantinedContentObjectCandidate(
            targetPath,
            candidate.RelativePath,
            candidate.StorageClass,
            candidate.NamedSha256,
            candidate.Extension,
            candidate.Bytes,
            quarantinedAt);
    }

    public async Task RestoreAsync(
        string objectStoreRoot,
        string quarantineRoot,
        QuarantinedContentObjectCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var targetRoot = NormalizeSafeRoot(objectStoreRoot, create: true);
        var sourceRoot = NormalizeSafeRoot(quarantineRoot);
        var sourcePath = ResolveExpectedPath(
            sourceRoot,
            candidate.SourceRelativePath + QuarantineSuffix);
        if (!string.Equals(
                sourcePath,
                Path.GetFullPath(candidate.AbsolutePath),
                PathComparison()))
        {
            throw new InvalidOperationException(
                "The quarantined object no longer has its canonical path.");
        }

        EnsureSafeDirectoryChain(
            Path.GetDirectoryName(sourcePath)!,
            sourceRoot);
        EnsureExpectedFile(sourcePath, candidate.Bytes);
        var actualSha256 = await ComputeSha256Async(
                sourceRoot,
                sourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                actualSha256,
                candidate.NamedSha256,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "A quarantined object cannot be restored because its hash is invalid.");
        }

        var targetPath = ResolveExpectedPath(
            targetRoot,
            candidate.SourceRelativePath);
        await CopyDurablyIfNeededAsync(
                sourcePath,
                targetPath,
                targetRoot,
                candidate.Bytes,
                actualSha256,
                cancellationToken)
            .ConfigureAwait(false);
        File.Delete(sourcePath);
    }

    public void DeleteQuarantined(
        string quarantineRoot,
        QuarantinedContentObjectCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var root = NormalizeSafeRoot(quarantineRoot);
        var expected = ResolveExpectedPath(
            root,
            candidate.SourceRelativePath + QuarantineSuffix);
        if (!string.Equals(
                expected,
                Path.GetFullPath(candidate.AbsolutePath),
                PathComparison()))
        {
            throw new InvalidOperationException(
                "The quarantined object no longer has its canonical path.");
        }

        if (!File.Exists(expected))
        {
            return;
        }

        EnsureSafeDirectoryChain(Path.GetDirectoryName(expected)!, root);
        EnsureExpectedFile(expected, candidate.Bytes);
        File.Delete(expected);
    }

    private static List<DiscoveredObject> Discover(
        string root,
        string suffix,
        DateTimeOffset? cutoff,
        int maximumEntries,
        int maximumCandidates)
    {
        var discovered = new List<DiscoveredObject>(maximumCandidates);
        var inspectedEntries = 0;
        foreach (var storage in StorageDirectories)
        {
            if (discovered.Count >= maximumCandidates
                || inspectedEntries >= maximumEntries)
            {
                break;
            }

            var classRoot = ResolveExpectedPath(root, storage.RelativeDirectory);
            if (!Directory.Exists(classRoot))
            {
                continue;
            }

            EnsureSafeDirectoryChain(classRoot, root);
            foreach (var firstShard in EnumerateBoundedDirectories(
                         classRoot,
                         maximumEntries,
                         ref inspectedEntries))
            {
                if (discovered.Count >= maximumCandidates
                    || inspectedEntries >= maximumEntries)
                {
                    break;
                }

                var firstName = Path.GetFileName(firstShard);
                if (!IsTwoLowerHex(firstName))
                {
                    continue;
                }

                RejectReparsePoint(firstShard);
                foreach (var secondShard in EnumerateBoundedDirectories(
                             firstShard,
                             maximumEntries,
                             ref inspectedEntries))
                {
                    if (discovered.Count >= maximumCandidates
                        || inspectedEntries >= maximumEntries)
                    {
                        break;
                    }

                    var secondName = Path.GetFileName(secondShard);
                    if (!IsTwoLowerHex(secondName))
                    {
                        continue;
                    }

                    RejectReparsePoint(secondShard);
                    foreach (var file in EnumerateBoundedFiles(
                                 secondShard,
                                 maximumEntries,
                                 ref inspectedEntries))
                    {
                        if (discovered.Count >= maximumCandidates)
                        {
                            break;
                        }

                        var fileName = Path.GetFileName(file);
                        if (!TryParseObjectFileName(
                                fileName,
                                suffix,
                                out var namedSha256,
                                out var extension)
                            || !string.Equals(
                                firstName,
                                namedSha256[..2],
                                StringComparison.Ordinal)
                            || !string.Equals(
                                secondName,
                                namedSha256[2..4],
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        RejectReparsePoint(file);
                        var information = new FileInfo(file);
                        var lastWriteAt = new DateTimeOffset(
                            information.LastWriteTimeUtc,
                            TimeSpan.Zero);
                        if (cutoff is not null && lastWriteAt > cutoff.Value)
                        {
                            continue;
                        }

                        var relativeWithoutSuffix = Path.GetRelativePath(root, file)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        if (suffix.Length > 0)
                        {
                            relativeWithoutSuffix =
                                relativeWithoutSuffix[..^suffix.Length];
                        }

                        discovered.Add(new DiscoveredObject(
                            Path.GetFullPath(file),
                            relativeWithoutSuffix,
                            storage.StorageClass.ToString(),
                            namedSha256,
                            extension,
                            information.Length,
                            lastWriteAt));
                    }
                }
            }
        }

        return discovered;
    }

    private static string[] EnumerateBoundedDirectories(
        string path,
        int maximumEntries,
        ref int inspectedEntries)
    {
        var remaining = maximumEntries - inspectedEntries;
        if (remaining <= 0)
        {
            return [];
        }

        var entries = Directory
            .EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
            .Take(remaining)
            .Order(StringComparer.Ordinal)
            .ToArray();
        inspectedEntries += entries.Length;
        return entries;
    }

    private static string[] EnumerateBoundedFiles(
        string path,
        int maximumEntries,
        ref int inspectedEntries)
    {
        var remaining = maximumEntries - inspectedEntries;
        if (remaining <= 0)
        {
            return [];
        }

        var entries = Directory
            .EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Take(remaining)
            .Order(StringComparer.Ordinal)
            .ToArray();
        inspectedEntries += entries.Length;
        return entries;
    }

    private static async Task CopyDurablyIfNeededAsync(
        string sourcePath,
        string targetPath,
        string targetRoot,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDirectory);
        EnsureSafeDirectoryChain(targetDirectory, targetRoot);

        if (File.Exists(targetPath))
        {
            EnsureExpectedFile(targetPath, expectedBytes);
            var targetHash = await ComputeFileSha256Async(
                    targetPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    targetHash,
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "The reconciliation destination contains different content.");
            }

            return;
        }

        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.part";
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             BufferSize,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan))
            await using (var target = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(
                        target,
                        BufferSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }

            EnsureExpectedFile(temporaryPath, expectedBytes);
            var temporaryHash = await ComputeFileSha256Async(
                    temporaryPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    temporaryHash,
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "The reconciled content copy failed hash verification.");
            }

            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                EnsureExpectedFile(targetPath, expectedBytes);
                var targetHash = await ComputeFileSha256Async(
                        targetPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        targetHash,
                        expectedSha256,
                        StringComparison.Ordinal))
                {
                    throw;
                }
            }
        }
        finally
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
                // The regular temporary-file cleanup handles an abandoned copy.
            }
        }
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeSafeRoot(string root, bool create = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException(
                "Reconciliation roots must be absolute paths.",
                nameof(root));
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var filesystemRoot = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(normalized)!);
        if (string.Equals(normalized, filesystemRoot, PathComparison()))
        {
            throw new InvalidOperationException(
                "A reconciliation root cannot be a filesystem root.");
        }

        if (create)
        {
            Directory.CreateDirectory(normalized);
        }

        if (Directory.Exists(normalized))
        {
            RejectReparsePoint(normalized);
        }

        return normalized;
    }

    private static string ResolveExpectedPath(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException(
                "Content object paths must remain relative.");
        }

        var normalizedRelative = relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, PathComparison()))
        {
            throw new UnauthorizedAccessException(
                "The content object path escaped its configured root.");
        }

        return resolved;
    }

    private static string EnsureAbsolutePathUnderRoot(
        string root,
        string absolutePath)
    {
        var resolved = Path.GetFullPath(absolutePath);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, PathComparison()))
        {
            throw new UnauthorizedAccessException(
                "The content object path escaped its configured root.");
        }

        return resolved;
    }

    private static void EnsureSafeDirectoryChain(string path, string root)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists)
            {
                RejectReparsePoint(current.FullName);
            }

            if (string.Equals(
                    current.FullName,
                    root,
                    PathComparison()))
            {
                return;
            }

            current = current.Parent;
        }

        throw new UnauthorizedAccessException(
            "The reconciliation directory escaped its configured root.");
    }

    private static void EnsureExpectedFile(string path, long expectedBytes)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "A content object disappeared during reconciliation.");
        }

        RejectReparsePoint(path);
        if (new FileInfo(path).Length != expectedBytes)
        {
            throw new IOException(
                "A content object changed size during reconciliation.");
        }
    }

    private static bool TryParseObjectFileName(
        string fileName,
        string suffix,
        out string sha256,
        out string extension)
    {
        sha256 = string.Empty;
        extension = string.Empty;
        if (suffix.Length > 0
            && !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var canonicalName = suffix.Length == 0
            ? fileName
            : fileName[..^suffix.Length];
        if (canonicalName.Length < 66 || canonicalName[64] != '.')
        {
            return false;
        }

        sha256 = canonicalName[..64];
        extension = canonicalName[65..];
        return IsLowerHex(sha256)
            && IsCanonicalExtension(extension);
    }

    private static bool IsCanonicalExtension(string extension)
    {
        if (extension.Length is < 1 or > 24
            || !IsLowerAlphaNumeric(extension[0])
            || !IsLowerAlphaNumeric(extension[^1]))
        {
            return false;
        }

        var previousSeparator = false;
        foreach (var character in extension)
        {
            var separator = character is '.' or '-';
            if (!IsLowerAlphaNumeric(character) && !separator)
            {
                return false;
            }

            if (separator && previousSeparator)
            {
                return false;
            }

            previousSeparator = separator;
        }

        return true;
    }

    private static bool IsLowerAlphaNumeric(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= '0' and <= '9';
    }

    private static bool IsTwoLowerHex(string value)
    {
        return value.Length == 2 && IsLowerHex(value);
    }

    private static bool IsLowerHex(string value)
    {
        return value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }

    private static void ValidateSha256(string sha256)
    {
        if (sha256.Length != 64 || !IsLowerHex(sha256))
        {
            throw new ArgumentException(
                "A SHA-256 value must be lowercase hexadecimal.",
                nameof(sha256));
        }
    }

    private static void ValidateLimits(int maximumEntries, int maximumCandidates)
    {
        if (maximumEntries <= 0 || maximumCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                "Reconciliation limits must be positive.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "Content reconciliation does not traverse reparse points.");
        }
    }

    private static StringComparison PathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private sealed record StorageClassDirectory(
        ContentStorageClass StorageClass,
        string RelativeDirectory);

    private sealed record DiscoveredObject(
        string AbsolutePath,
        string RelativePath,
        string StorageClass,
        string NamedSha256,
        string Extension,
        long Bytes,
        DateTimeOffset LastWriteAt);
}

public sealed class PromotedContentObjectReconciler
{
    private const int MaximumFilesystemEntriesPerPass = 5_000;
    private const int MaximumObjectsPerPass = 200;
    private const long MaximumObjectBytes = 512L * 1024L * 1024L;
    private const long MaximumHashedBytesPerPass = 1024L * 1024L * 1024L;
    private static readonly TimeSpan DefaultPromotionGrace = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultQuarantineRetention = TimeSpan.FromDays(7);

    private readonly IDbContextFactory<OokiGraderDbContext> _dbContextFactory;
    private readonly IWriteCoordinator _writeCoordinator;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly IPromotedContentObjectFileSystem _fileSystem;

    public PromotedContentObjectReconciler(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        IPromotedContentObjectFileSystem? fileSystem = null)
    {
        _dbContextFactory = dbContextFactory;
        _writeCoordinator = writeCoordinator;
        _configuration = configuration;
        _environment = environment;
        _timeProvider = timeProvider;
        _fileSystem = fileSystem ?? new NtfsPromotedContentObjectFileSystem();
    }

    public async Task<PromotedContentObjectReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var objectStoreRoot = ResolveObjectStoreRoot();
        var quarantineRoot = ResolveQuarantineRoot();
        var quarantined = 0;
        var restored = 0;
        var deleted = 0;
        var failures = 0;

        var quarantineCandidates = _fileSystem.DiscoverQuarantinedObjects(
            quarantineRoot,
            MaximumFilesystemEntriesPerPass,
            MaximumObjectsPerPass);
        foreach (var candidate in quarantineCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await IsCommittedAsync(candidate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    await _fileSystem.RestoreAsync(
                            objectStoreRoot,
                            quarantineRoot,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await AddAuditAsync(
                            "storage.promoted_object_restored",
                            candidate.NamedSha256,
                            "late_database_commit",
                            candidate.SourceRelativePath,
                            candidate.StorageClass,
                            candidate.Bytes,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                    restored++;
                    continue;
                }

                if (candidate.QuarantinedAt
                    > now - ResolveQuarantineRetention())
                {
                    continue;
                }

                if (await IsCommittedAsync(candidate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                _fileSystem.DeleteQuarantined(quarantineRoot, candidate);
                await AddAuditAsync(
                        "storage.quarantined_promoted_object_deleted",
                        candidate.NamedSha256,
                        "orphan_cleanup",
                        candidate.SourceRelativePath,
                        candidate.StorageClass,
                        candidate.Bytes,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                deleted++;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                failures++;
            }
        }

        var candidates = _fileSystem.DiscoverPromotedObjects(
            objectStoreRoot,
            now - ResolvePromotionGrace(),
            MaximumFilesystemEntriesPerPass,
            MaximumObjectsPerPass);
        var committed = await LoadCommittedKeysAsync(
                candidates,
                cancellationToken)
            .ConfigureAwait(false);
        long hashedBytes = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (committed.Contains(candidate))
            {
                continue;
            }

            if (candidate.Bytes > MaximumObjectBytes
                || candidate.Bytes > MaximumHashedBytesPerPass - hashedBytes)
            {
                continue;
            }

            try
            {
                var actualSha256 = await _fileSystem.ComputeSha256Async(
                        objectStoreRoot,
                        candidate.AbsolutePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                hashedBytes = checked(hashedBytes + candidate.Bytes);
                if (await IsCommittedAsync(candidate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                await _fileSystem.QuarantineAsync(
                        objectStoreRoot,
                        quarantineRoot,
                        candidate,
                        actualSha256,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                await AddAuditAsync(
                        "storage.unreferenced_promoted_object_quarantined",
                        candidate.NamedSha256,
                        string.Equals(
                            actualSha256,
                            candidate.NamedSha256,
                            StringComparison.Ordinal)
                            ? "orphan_cleanup"
                            : "orphan_hash_mismatch",
                        candidate.RelativePath,
                        candidate.StorageClass,
                        candidate.Bytes,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                quarantined++;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                failures++;
            }
        }

        return new PromotedContentObjectReconciliationResult(
            quarantined,
            restored,
            deleted,
            failures);
    }

    private async Task<CommittedObjectKeys> LoadCommittedKeysAsync(
        IReadOnlyCollection<PromotedContentObjectCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return CommittedObjectKeys.Empty;
        }

        var relativePaths = candidates
            .Select(candidate => candidate.RelativePath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hashes = candidates
            .Select(candidate => candidate.NamedSha256)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await using var db = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await db.FileObjects
            .AsNoTracking()
            .Where(file => file.State != "deleted"
                && (relativePaths.Contains(file.RelativeObjectPath)
                    || hashes.Contains(file.Sha256)))
            .Select(file => new
            {
                file.RelativeObjectPath,
                file.StorageClass,
                file.Sha256,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new CommittedObjectKeys(
            rows.Select(row => row.RelativeObjectPath),
            rows.Select(row => SemanticKey(row.StorageClass, row.Sha256)));
    }

    private async Task<bool> IsCommittedAsync(
        PromotedContentObjectCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.FileObjects
            .AsNoTracking()
            .AnyAsync(
                file => file.State != "deleted"
                    && (file.RelativeObjectPath == candidate.RelativePath
                        || (file.StorageClass == candidate.StorageClass
                            && file.Sha256 == candidate.NamedSha256)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsCommittedAsync(
        QuarantinedContentObjectCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.FileObjects
            .AsNoTracking()
            .AnyAsync(
                file => file.State != "deleted"
                    && file.RelativeObjectPath == candidate.SourceRelativePath
                    && file.StorageClass == candidate.StorageClass
                    && file.Sha256 == candidate.NamedSha256,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task AddAuditAsync(
        string eventType,
        string objectId,
        string reasonCode,
        string relativePath,
        string storageClass,
        long bytes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                EventType = eventType,
                ObjectType = "content_object",
                ObjectId = objectId,
                Outcome = "succeeded",
                ReasonCode = reasonCode,
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    relativePath,
                    storageClass,
                    bytes,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private string ResolveObjectStoreRoot()
    {
        var configuredDataRoot = _configuration["Data:Root"] ?? ".data";
        var dataRoot = ResolvePath(configuredDataRoot);
        var configuredObjectStore = _configuration["Data:ObjectStore"];
        return string.IsNullOrWhiteSpace(configuredObjectStore)
            ? Path.Combine(dataRoot, "objects")
            : ResolvePath(configuredObjectStore);
    }

    private string ResolveQuarantineRoot()
    {
        var configuredDataRoot = _configuration["Data:Root"] ?? ".data";
        var dataRoot = ResolvePath(configuredDataRoot);
        var configured = _configuration["Data:Quarantine"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(dataRoot, "quarantine", "promoted-objects")
            : Path.Combine(ResolvePath(configured), "promoted-objects");
    }

    private string ResolvePath(string configured)
    {
        return Path.TrimEndingDirectorySeparator(
            Path.IsPathFullyQualified(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(configured, _environment.ContentRootPath));
    }

    private TimeSpan ResolvePromotionGrace()
    {
        var hours = _configuration.GetValue(
            "Uploads:PromotedObjectGraceHours",
            (int)DefaultPromotionGrace.TotalHours);
        return TimeSpan.FromHours(Math.Clamp(hours, 1, 7 * 24));
    }

    private TimeSpan ResolveQuarantineRetention()
    {
        var days = _configuration.GetValue(
            "Uploads:PromotedObjectQuarantineDays",
            (int)DefaultQuarantineRetention.TotalDays);
        return TimeSpan.FromDays(Math.Clamp(days, 1, 30));
    }

    private static string SemanticKey(string storageClass, string sha256)
    {
        return $"{storageClass}\u001f{sha256}";
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or CryptographicException
            or InvalidOperationException;
    }

    private sealed class CommittedObjectKeys
    {
        private readonly HashSet<string> _relativePaths;
        private readonly HashSet<string> _semanticKeys;

        public CommittedObjectKeys(
            IEnumerable<string> relativePaths,
            IEnumerable<string> semanticKeys)
        {
            _relativePaths = new HashSet<string>(
                relativePaths,
                StringComparer.Ordinal);
            _semanticKeys = new HashSet<string>(
                semanticKeys,
                StringComparer.Ordinal);
        }

        public static CommittedObjectKeys Empty { get; } =
            new([], []);

        public bool Contains(PromotedContentObjectCandidate candidate)
        {
            return _relativePaths.Contains(candidate.RelativePath)
                || _semanticKeys.Contains(
                    SemanticKey(candidate.StorageClass, candidate.NamedSha256));
        }
    }
}

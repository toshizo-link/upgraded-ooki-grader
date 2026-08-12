using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.Host.Reports;

internal sealed class BulkTranscriptArchiveWriter : IAsyncDisposable
{
    private static readonly DateTimeOffset DeterministicEntryTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> WindowsReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly HashSet<string> _entryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PdfArtifact> _pdfArtifacts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StudentFolder> _studentFolders =
        new(StringComparer.Ordinal);
    private readonly List<ManifestRow> _manifest = [];
    private bool _completed;
    private bool _archiveDisposed;
    private bool _streamDisposed;

    public BulkTranscriptArchiveWriter(string path, long maximumBytes)
    {
        _path = path;
        _maximumBytes = maximumBytes;
        _stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _archive = new ZipArchive(
            _stream,
            ZipArchiveMode.Create,
            leaveOpen: true,
            Encoding.UTF8);
    }

    public async Task AddAsync(
        FrozenBulkResultSource frozen,
        ResultReportSource source,
        ResultPdfRenderResult rendered,
        CancellationToken cancellationToken)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The archive is already complete.");
        }

        if (!string.Equals(
                rendered.RendererVersion,
                ResultPdfRenderer.CurrentRendererVersion,
                StringComparison.Ordinal)
            || rendered.PdfBytes.Length < 5
            || !rendered.PdfBytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)
            || !string.Equals(
                rendered.Sha256,
                Convert.ToHexString(SHA256.HashData(rendered.PdfBytes))
                    .ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("A rendered result PDF is invalid.");
        }

        if (!_studentFolders.TryGetValue(frozen.StudentId, out var folder))
        {
            var folderIndex = _studentFolders.Count + 1;
            var identity = string.IsNullOrWhiteSpace(
                    source.Document.StudentNumber)
                ? source.Document.StudentDisplayName
                : $"{source.Document.StudentNumber}_{source.Document.StudentDisplayName}";
            folder = new StudentFolder(
                $"{folderIndex:D4}_{SanitizeSegment(identity, 80)}",
                0);
            _studentFolders.Add(frozen.StudentId, folder);
        }

        folder = folder with { ResultCount = folder.ResultCount + 1 };
        _studentFolders[frozen.StudentId] = folder;
        var fileName =
            $"{folder.ResultCount:D3}_{source.Document.TestDate:yyyy-MM-dd}_" +
            $"{SanitizeSegment(source.Document.TestTitle, 80)}_結果.pdf";
        var entryName = $"{folder.Path}/{fileName}";
        EnsureSafeUniqueEntryName(entryName);
        _pdfArtifacts.Add(
            entryName,
            new PdfArtifact(rendered.Sha256, rendered.PdfBytes.LongLength));
        var entry = _archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        entry.LastWriteTime = DeterministicEntryTimestamp;
        await using (var target = entry.Open())
        {
            await target.WriteAsync(rendered.PdfBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        _manifest.Add(new ManifestRow(
            source.Document.StudentNumber,
            source.Document.StudentDisplayName,
            source.Document.TestDate,
            source.Document.TestTitle,
            source.Document.EarnedPointsMilli,
            source.Document.PossiblePointsMilli,
            frozen.SubmissionId,
            frozen.ResultSourceRevision,
            frozen.TemplateVersionNumber,
            frozen.SourceHash,
            entryName));
        EnforceStreamingSizeBound();
    }

    public async Task<long> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The archive is already complete.");
        }

        const string manifestName = "manifest.csv";
        EnsureSafeUniqueEntryName(manifestName);
        var entry = _archive.CreateEntry(
            manifestName,
            CompressionLevel.NoCompression);
        entry.LastWriteTime = DeterministicEntryTimestamp;
        await using (var target = entry.Open())
        await using (var writer = new StreamWriter(
            target,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 16 * 1024,
            leaveOpen: false))
        {
            writer.NewLine = "\n";
            await writer.WriteLineAsync(
                "生徒番号,生徒名,実施日,テスト名,得点,満点,得点率,答案ID,結果改訂,採点基準版,結果ハッシュ,状態,PDFファイル")
                .ConfigureAwait(false);
            foreach (var row in _manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var percentage = row.PossiblePointsMilli <= 0
                    ? string.Empty
                    : ((decimal)row.EarnedPointsMilli
                        / row.PossiblePointsMilli * 100m)
                        .ToString("0.0", CultureInfo.InvariantCulture);
                var cells = new[]
                {
                    row.StudentNumber,
                    row.StudentName,
                    row.TestDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    row.TestTitle,
                    FormatPoints(row.EarnedPointsMilli),
                    FormatPoints(row.PossiblePointsMilli),
                    percentage,
                    row.SubmissionId,
                    row.ResultSourceRevision.ToString(CultureInfo.InvariantCulture),
                    row.TemplateVersionNumber.ToString(CultureInfo.InvariantCulture),
                    row.SourceHash,
                    "finalized",
                    row.EntryName,
                };
                await writer.WriteLineAsync(string.Join(',', cells.Select(CsvCell)))
                    .ConfigureAwait(false);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _archive.Dispose();
        _archiveDisposed = true;
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
        var length = _stream.Length;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _streamDisposed = true;
        if (length <= 0 || length > _maximumBytes)
        {
            throw new InvalidDataException(
                "The bulk result archive exceeded its verified size limit.");
        }

        VerifyArchive(_path, _entryNames, _pdfArtifacts);
        _completed = true;
        return length;
    }

    public async ValueTask DisposeAsync()
    {
        Exception? disposalFailure = null;
        if (!_archiveDisposed)
        {
            try
            {
                _archive.Dispose();
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
            }
            finally
            {
                _archiveDisposed = true;
            }
        }

        if (!_streamDisposed)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (disposalFailure is not null)
            {
                // Preserve the first disposal failure while still attempting
                // to release both handles.
                _ = exception;
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
            }
            finally
            {
                _streamDisposed = true;
            }
        }

        if (!_completed && disposalFailure is not null)
        {
            throw disposalFailure;
        }
    }

    private void EnforceStreamingSizeBound()
    {
        if (_stream.Position > _maximumBytes)
        {
            throw new InvalidDataException(
                "The bulk result archive exceeded its verified size limit.");
        }
    }

    private void EnsureSafeUniqueEntryName(string entryName)
    {
        if (entryName.Length is 0 or > 240
            || entryName.StartsWith('/')
            || entryName.Contains('\\')
            || entryName.Contains('\0')
            || entryName.Split('/').Any(segment =>
                segment is "" or "." or "..")
            || !_entryNames.Add(entryName))
        {
            throw new InvalidDataException("An unsafe ZIP entry name was rejected.");
        }
    }

    private static string SanitizeSegment(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(character switch
            {
                '<' or '>' or ':' or '"' or '/' or '\\' or '|'
                    or '?' or '*' => '_',
                _ when char.IsControl(character) => '_',
                _ => character,
            });
        }

        var safe = builder.ToString().Replace("..", "_", StringComparison.Ordinal)
            .Trim(' ', '.');
        if (safe.Length == 0)
        {
            safe = "名称なし";
        }

        if (WindowsReservedNames.Contains(safe))
        {
            safe = $"_{safe}";
        }

        if (safe.Length > maximumLength)
        {
            safe = safe[..maximumLength].TrimEnd(' ', '.');
            if (safe.Length > 0 && char.IsHighSurrogate(safe[^1]))
            {
                safe = safe[..^1];
            }
        }

        return safe.Length == 0 ? "名称なし" : safe;
    }

    private static string CsvCell(string? value)
    {
        var safe = (value ?? string.Empty)
            .Replace('\0', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        var probeStart = 0;
        while (probeStart < safe.Length
            && safe[probeStart] is ' ' or '\t')
        {
            probeStart++;
        }

        var formulaProbe = safe.AsSpan(probeStart);
        if (formulaProbe.Length > 0
            && formulaProbe[0] is '=' or '+' or '-' or '@')
        {
            safe = $"'{safe}";
        }

        return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string FormatPoints(long milliPoints) =>
        (milliPoints / 1_000m).ToString("0.###", CultureInfo.InvariantCulture);

    private static void VerifyArchive(
        string path,
        IReadOnlySet<string> expectedNames,
        IReadOnlyDictionary<string, PdfArtifact> expectedPdfs)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            Encoding.UTF8);
        var actualNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith('/')
                || entry.FullName.Contains('\\')
                || entry.FullName.Split('/').Any(segment =>
                    segment is "" or "." or "..")
                || !actualNames.Add(entry.FullName))
            {
                throw new InvalidDataException(
                    "The completed ZIP contained an unsafe entry.");
            }
        }

        if (!actualNames.SetEquals(expectedNames)
            || actualNames.Count(name => name.EndsWith(
                ".pdf",
                StringComparison.OrdinalIgnoreCase)) != expectedPdfs.Count
            || !actualNames.Contains("manifest.csv"))
        {
            throw new InvalidDataException(
                "The completed ZIP manifest did not match its source snapshot.");
        }

        foreach (var (entryName, expected) in expectedPdfs)
        {
            var entry = archive.GetEntry(entryName)
                ?? throw new InvalidDataException(
                    "A completed result PDF entry was missing.");
            if (entry.Length != expected.Bytes || entry.Length <= 5)
            {
                throw new InvalidDataException(
                    "A completed result PDF entry had an invalid length.");
            }

            using var source = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var header = new byte[5];
            var headerLength = 0;
            var total = 0L;
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (headerLength < header.Length)
                {
                    var copyLength = Math.Min(
                        header.Length - headerLength,
                        read);
                    buffer.AsSpan(0, copyLength).CopyTo(
                        header.AsSpan(headerLength));
                    headerLength += copyLength;
                }

                total = checked(total + read);
                if (total > expected.Bytes)
                {
                    throw new InvalidDataException(
                        "A completed result PDF entry exceeded its source length.");
                }

                hash.AppendData(buffer, 0, read);
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            if (headerLength != header.Length
                || !header.AsSpan().SequenceEqual("%PDF-"u8)
                || total != expected.Bytes
                || !string.Equals(sha256, expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A completed result PDF entry did not match its renderer hash.");
            }
        }

        var manifest = archive.GetEntry("manifest.csv")
            ?? throw new InvalidDataException("The ZIP manifest was missing.");
        using (var source = manifest.Open())
        {
            var bom = new byte[3];
            try
            {
                source.ReadExactly(bom);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException(
                    "The ZIP manifest was truncated.",
                    exception);
            }

            if (!bom.AsSpan().SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                throw new InvalidDataException(
                    "The ZIP manifest was not verified UTF-8.");
            }

            var buffer = new byte[16 * 1024];
            while (source.Read(buffer, 0, buffer.Length) > 0)
            {
                // Reading to EOF forces the ZIP implementation to validate
                // the complete entry rather than only its central directory.
            }
        }
    }

    private sealed record StudentFolder(string Path, int ResultCount);

    private sealed record PdfArtifact(string Sha256, long Bytes);

    private sealed record ManifestRow(
        string? StudentNumber,
        string StudentName,
        DateOnly TestDate,
        string TestTitle,
        long EarnedPointsMilli,
        long PossiblePointsMilli,
        string SubmissionId,
        long ResultSourceRevision,
        int TemplateVersionNumber,
        string SourceHash,
        string EntryName);
}

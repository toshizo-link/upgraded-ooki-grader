using System.Security.Cryptography;
using SkiaSharp;

namespace OokiGrader.Preprocessing;

public static class PreprocessedDocumentEncoder
{
    public static IReadOnlyList<ImageArtifact> ToVerticalPngTiles(
        PreprocessedPage page,
        int tileCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (tileCount is < 2 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileCount),
                "The vertical tile count must be between 2 and 16.");
        }

        using var source = SKBitmap.Decode(page.NormalizedPng.Bytes)
            ?? throw new PreprocessingException(
                "normalized_page_invalid",
                $"Normalized page {page.PageNumber} could not be decoded.");
        if (source.Width != page.Width
            || source.Height != page.Height
            || source.Height < tileCount)
        {
            throw new PreprocessingException(
                "normalized_page_invalid",
                $"Normalized page {page.PageNumber} dimensions changed or are too small.");
        }

        var artifacts = new List<ImageArtifact>(tileCount);
        try
        {
            using var sourceImage = SKImage.FromBitmap(source);
            for (var index = 0; index < tileCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var top = checked((int)((long)source.Height * index
                    / tileCount));
                var bottom = checked((int)((long)source.Height * (index + 1)
                    / tileCount));
                var tileHeight = bottom - top;
                using var tile = new SKBitmap(
                    new SKImageInfo(
                        source.Width,
                        tileHeight,
                        SKColorType.Bgra8888,
                        SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(tile))
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawImage(
                        sourceImage,
                        new SKRect(0, top, source.Width, bottom),
                        new SKRect(0, 0, source.Width, tileHeight),
                        new SKSamplingOptions(SKFilterMode.Nearest),
                        paint: null);
                }

                using var image = SKImage.FromBitmap(tile);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new PreprocessingException(
                        "png_encode_failed",
                        $"Page {page.PageNumber} detail view {index + 1} could not be encoded.");
                var bytes = encoded.ToArray();
                if (bytes.Length < 8
                    || !bytes.AsSpan(0, 8).SequenceEqual(
                        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    throw new PreprocessingException(
                        "png_encode_failed",
                        $"Page {page.PageNumber} detail view {index + 1} was not a PNG.");
                }

                artifacts.Add(new ImageArtifact(
                    "image/png",
                    "png",
                    source.Width,
                    tileHeight,
                    bytes,
                    Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant()));
            }

            return artifacts;
        }
        catch
        {
            foreach (var artifact in artifacts)
            {
                CryptographicOperations.ZeroMemory(artifact.Bytes);
            }

            throw;
        }
    }

    public static byte[] ToPdf(
        IReadOnlyList<PreprocessedPage> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException(
                "At least one preprocessed page is required.",
                nameof(pages));
        }

        using var output = new MemoryStream();
        using var writer = CreatePdfWriter(output);
        foreach (var page in pages.OrderBy(item => item.PageNumber))
        {
            writer.AppendPage(page, cancellationToken);
        }

        writer.Complete(cancellationToken);
        var bytes = output.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new PreprocessingException(
                "pdf_encode_failed",
                "The normalized PDF output was invalid.");
        }

        return bytes;
    }

    /// <summary>
    /// Creates an incremental PDF writer. Callers can preprocess, append, and
    /// release one page at a time while the bounded PDF output is spooled to a
    /// seekable file stream.
    /// </summary>
    public static PreprocessedPdfWriter CreatePdfWriter(
        Stream output,
        long maximumOutputBytes = PreprocessingOptions.DefaultMaxInputBytes) =>
        new(output, maximumOutputBytes);
}

public sealed class PreprocessedPdfWriter : IDisposable
{
    private readonly MaximumLengthWriteStream _output;
    private readonly SKDocument _document;
    private bool _completed;
    private bool _disposed;
    private int _pageCount;
    private int? _lastPageNumber;

    internal PreprocessedPdfWriter(Stream output, long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException(
                "The PDF output stream must be writable.",
                nameof(output));
        }

        if (maximumOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutputBytes),
                "The PDF output byte limit must be positive.");
        }

        if (output.CanSeek && (output.Position != 0 || output.Length != 0))
        {
            throw new ArgumentException(
                "The PDF output stream must be empty and positioned at zero.",
                nameof(output));
        }

        _output = new MaximumLengthWriteStream(output, maximumOutputBytes);
        _document = SKDocument.CreatePdf(_output)
            ?? throw new PreprocessingException(
                "pdf_encode_failed",
                "A normalized PDF document could not be created.");
    }

    public int PageCount => _pageCount;

    public long OutputBytes => _output.FurthestPosition;

    public void AppendPage(
        PreprocessedPage page,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(page);
        if (_completed)
        {
            throw new InvalidOperationException("The PDF writer is already complete.");
        }

        if (_lastPageNumber is { } lastPageNumber
            && page.PageNumber <= lastPageNumber)
        {
            throw new ArgumentException(
                "PDF pages must be appended in strictly increasing page-number order.",
                nameof(page));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = SKBitmap.Decode(page.NormalizedPng.Bytes)
            ?? throw new PreprocessingException(
                "normalized_page_invalid",
                $"Normalized page {page.PageNumber} could not be decoded.");
        if (bitmap.Width != page.Width
            || bitmap.Height != page.Height
            || page.DpiX <= 0
            || page.DpiY <= 0)
        {
            throw new PreprocessingException(
                "normalized_page_invalid",
                $"Normalized page {page.PageNumber} dimensions or DPI changed.");
        }

        using var image = SKImage.FromBitmap(bitmap);
        var pageWidthPoints = checked(page.Width * 72f / page.DpiX);
        var pageHeightPoints = checked(page.Height * 72f / page.DpiY);
        if (!float.IsFinite(pageWidthPoints)
            || !float.IsFinite(pageHeightPoints)
            || pageWidthPoints <= 0
            || pageHeightPoints <= 0)
        {
            throw new PreprocessingException(
                "normalized_page_invalid",
                $"Normalized page {page.PageNumber} has invalid physical dimensions.");
        }

        var canvas = _document.BeginPage(pageWidthPoints, pageHeightPoints)
            ?? throw new PreprocessingException(
                "pdf_encode_failed",
                $"Normalized page {page.PageNumber} could not be added.");
        canvas.Clear(SKColors.White);
        canvas.DrawImage(
            image,
            new SKRect(0, 0, pageWidthPoints, pageHeightPoints),
            new SKSamplingOptions(SKFilterMode.Linear),
            paint: null);
        _document.EndPage();
        _pageCount = checked(_pageCount + 1);
        _lastPageNumber = page.PageNumber;
    }

    public void Complete(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        if (_pageCount == 0)
        {
            throw new PreprocessingException(
                "pdf_encode_failed",
                "A normalized PDF must contain at least one page.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _document.Close();
        _output.Flush();
        if (_output.FurthestPosition < 5 || !_output.HasPdfHeader)
        {
            throw new PreprocessingException(
                "pdf_encode_failed",
                "The normalized PDF output was invalid.");
        }

        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _document.Dispose();
        _output.Dispose();
        _disposed = true;
    }

    private sealed class MaximumLengthWriteStream(
        Stream inner,
        long maximumLength) : Stream
    {
        private static ReadOnlySpan<byte> PdfHeader => "%PDF-"u8;
        private readonly byte[] _prefix = new byte[5];
        private int _prefixBytes;
        private long _position = inner.CanSeek ? inner.Position : 0;

        public long FurthestPosition { get; private set; } =
            inner.CanSeek ? inner.Length : 0;

        public bool HasPdfHeader => _prefixBytes == _prefix.Length
            && _prefix.AsSpan().SequenceEqual(PdfHeader);

        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.CanSeek
            ? inner.Length
            : FurthestPosition;
        public override long Position
        {
            get => inner.CanSeek ? inner.Position : _position;
            set
            {
                if (!inner.CanSeek)
                {
                    throw new NotSupportedException();
                }

                if (value < 0 || value > maximumLength)
                {
                    throw OutputLimitExceeded();
                }

                inner.Position = value;
                _position = value;
            }
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!inner.CanSeek)
            {
                throw new NotSupportedException();
            }

            var position = inner.Seek(offset, origin);
            if (position < 0 || position > maximumLength)
            {
                throw OutputLimitExceeded();
            }

            _position = position;
            return position;
        }

        public override void SetLength(long value)
        {
            if (!inner.CanSeek)
            {
                throw new NotSupportedException();
            }

            if (value < 0 || value > maximumLength)
            {
                throw OutputLimitExceeded();
            }

            inner.SetLength(value);
            FurthestPosition = Math.Max(FurthestPosition, value);
            if (_position > value)
            {
                _position = value;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            PrepareWrite(buffer);
            inner.Write(buffer);
            CompleteWrite(buffer.Length);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            PrepareWrite(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            CompleteWrite(buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Flush();
            }

            base.Dispose(disposing);
        }

        private void PrepareWrite(ReadOnlySpan<byte> buffer)
        {
            var end = checked(Position + buffer.Length);
            if (end > maximumLength)
            {
                throw OutputLimitExceeded();
            }

            if (Position < _prefix.Length)
            {
                var prefixOffset = checked((int)Position);
                var length = Math.Min(
                    buffer.Length,
                    _prefix.Length - prefixOffset);
                buffer[..length].CopyTo(_prefix.AsSpan(prefixOffset));
                _prefixBytes = Math.Max(_prefixBytes, prefixOffset + length);
            }
        }

        private void CompleteWrite(int count)
        {
            _position = checked(_position + count);
            FurthestPosition = Math.Max(FurthestPosition, _position);
        }

        private static PreprocessingException OutputLimitExceeded() =>
            new(
                "pdf_output_byte_limit",
                "The normalized PDF exceeds the bounded output byte limit.");
    }
}

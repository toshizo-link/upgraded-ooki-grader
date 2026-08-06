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
        using var document = SKDocument.CreatePdf(output)
            ?? throw new PreprocessingException(
                "pdf_encode_failed",
                "A normalized PDF document could not be created.");
        foreach (var page in pages.OrderBy(item => item.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = SKBitmap.Decode(page.NormalizedPng.Bytes)
                ?? throw new PreprocessingException(
                    "normalized_page_invalid",
                    $"Normalized page {page.PageNumber} could not be decoded.");
            if (bitmap.Width != page.Width || bitmap.Height != page.Height)
            {
                throw new PreprocessingException(
                    "normalized_page_invalid",
                    $"Normalized page {page.PageNumber} dimensions changed.");
            }

            using var image = SKImage.FromBitmap(bitmap);
            var canvas = document.BeginPage(page.Width, page.Height)
                ?? throw new PreprocessingException(
                    "pdf_encode_failed",
                    $"Normalized page {page.PageNumber} could not be added.");
            canvas.Clear(SKColors.White);
            canvas.DrawImage(
                image,
                new SKRect(0, 0, page.Width, page.Height),
                new SKSamplingOptions(SKFilterMode.Linear),
                paint: null);
            document.EndPage();
        }

        document.Close();
        var bytes = output.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new PreprocessingException(
                "pdf_encode_failed",
                "The normalized PDF output was invalid.");
        }

        return bytes;
    }
}

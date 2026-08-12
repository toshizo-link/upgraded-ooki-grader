using SkiaSharp;

namespace OokiGrader.Preprocessing.Tests;

public sealed class PdfPageRangeExtractorTests
{
    [Theory]
    [InlineData(90, 2, 3)]
    [InlineData(180, 3, 2)]
    [InlineData(270, 2, 3)]
    public async Task QuarterTurnIsExplicitAndDoesNotChangeDeskew(
        int degrees,
        int expectedWidth,
        int expectedHeight)
    {
        var service = CreateService();
        var input = CreatePng(3, 2, canvas =>
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawPoint(0, 0, paint);
        });
        var processed = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/png", "page.png"));
        var page = Assert.Single(processed.Pages) with { DeskewAngle = 1.25 };

        var rotated = PageQuarterTurnRotator.Rotate(
            page,
            degrees,
            "gemini",
            0.98);

        Assert.Equal(expectedWidth, rotated.Width);
        Assert.Equal(expectedHeight, rotated.Height);
        Assert.Equal(degrees, rotated.AppliedRotationDegrees);
        Assert.Equal(1.25, rotated.DeskewAngle);
        Assert.Equal("gemini", rotated.OrientationSource);
        Assert.Equal(0.98, rotated.OrientationConfidence);
        Assert.Equal(Fingerprinting.Sha256(rotated.NormalizedPng.Bytes),
            rotated.NormalizedPng.Sha256);
    }

    [Fact]
    public async Task ExtractsInclusivePageRangeInOrderAndIsDeterministic()
    {
        var service = CreateService();
        var sourcePages = new[] { SKColors.Red, SKColors.Green, SKColors.Blue }
            .Select((color, index) => CreateProcessedPage(service, color, index + 1))
            .ToArray();
        var pages = await Task.WhenAll(sourcePages);
        var sourcePdf = PreprocessedDocumentEncoder.ToPdf(pages);
        var extractor = new PdfPageRangeExtractor(service);

        var first = await extractor.ExtractAsync(
            new MemoryStream(sourcePdf),
            "source.pdf",
            2,
            3,
            new Dictionary<int, int> { [3] = 180 });
        var second = await extractor.ExtractAsync(
            new MemoryStream(sourcePdf),
            "source.pdf",
            2,
            3,
            new Dictionary<int, int> { [3] = 180 });

        Assert.Equal(2, first.PageCount);
        Assert.Equal(2, first.FirstPage);
        Assert.Equal(3, first.LastPage);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal([0, 180], first.AppliedRotations.Select(item => item.ClockwiseDegrees));

        var output = await service.ProcessAsync(
            new MemoryStream(first.Bytes),
            new PreprocessingInput("application/pdf", "derived.pdf"));
        Assert.Equal(2, output.Pages.Count);
    }

    [Fact]
    public async Task ExtractsAnEarlyRangeWithoutTreatingItAsADocumentPageLimit()
    {
        var service = CreateService();
        var sourcePages = Enumerable.Range(1, 6)
            .Select(index => CreateProcessedPage(
                service,
                index % 2 == 0 ? SKColors.Orange : SKColors.Purple,
                index))
            .ToArray();
        var pages = await Task.WhenAll(sourcePages);
        var sourcePdf = PreprocessedDocumentEncoder.ToPdf(pages);
        var extractor = new PdfPageRangeExtractor(service);

        var result = await extractor.ExtractAsync(
            new MemoryStream(sourcePdf),
            "source.pdf",
            1,
            1,
            new Dictionary<int, int>());

        Assert.Equal(1, result.PageCount);
        Assert.Equal(1, result.FirstPage);
        Assert.Equal(1, result.LastPage);
        Assert.Single(result.AppliedRotations);
    }

    [Fact]
    public async Task RejectsRotationOutsideSelectedRangeBeforeReadingSource()
    {
        var extractor = new PdfPageRangeExtractor(CreateService());
        await Assert.ThrowsAsync<ArgumentException>(() => extractor.ExtractAsync(
            Stream.Null,
            "source.pdf",
            2,
            3,
            new Dictionary<int, int> { [1] = 90 }));
    }

    private static PreprocessingService CreateService() => new(
        new PreprocessingOptions
        {
            PdfDpi = 72,
            ImageDpi = 72,
            MaxPixelsPerPage = 100_000,
            MaxTotalPixels = 400_000,
            MaxNormalizedArtifactBytes = 4 * 1024 * 1024,
        });

    private static async Task<PreprocessedPage> CreateProcessedPage(
        PreprocessingService service,
        SKColor color,
        int pageNumber)
    {
        var png = CreatePng(40, 30, canvas => canvas.Clear(color));
        var result = await service.ProcessAsync(
            new MemoryStream(png),
            new PreprocessingInput("image/png", $"page-{pageNumber}.png"));
        return Assert.Single(result.Pages) with { PageNumber = pageNumber };
    }

    private static byte[] CreatePng(
        int width,
        int height,
        Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

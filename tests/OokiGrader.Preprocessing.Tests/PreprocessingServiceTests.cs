using SkiaSharp;

namespace OokiGrader.Preprocessing.Tests;

public sealed class PreprocessingServiceTests
{
    [Fact]
    public async Task NormalizesPngCreatesThumbnailAndStableManifest()
    {
        var input = CreatePng(120, 80, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(new SKRect(20, 15, 100, 65), paint);
        });
        var options = new PreprocessingOptions
        {
            ThumbnailMaxDimension = 64,
            MaxPixelsPerPage = 100_000,
            MaxTotalPixels = 100_000,
        };
        var service = new PreprocessingService(options);

        var first = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/png"));
        var second = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/png"));

        var page = Assert.Single(first.Pages);
        Assert.Equal(120, page.Width);
        Assert.Equal(80, page.Height);
        Assert.Equal(64, Math.Max(
            page.ThumbnailPng.Width,
            page.ThumbnailPng.Height));
        Assert.Equal("image/png", page.NormalizedPng.MimeType);
        Assert.Equal(64, page.NormalizedPng.Sha256.Length);
        Assert.Equal(first.ManifestSha256, second.ManifestSha256);
        Assert.Equal(
            page.Fingerprint.ExactSha256,
            second.Pages[0].Fingerprint.ExactSha256);
    }

    [Fact]
    public async Task NormalizesSinglePageTiffToPng()
    {
        var input = Convert.FromBase64String(
            "SUkqABYAAACAAAAP+CP+BQWDQIAQEBEAAAEDAAEAAAACAAAAAQEDAAEAAAAC"
            + "AAAAAgEDAAIAAAAQABAAAwEDAAEAAAAFAAAABgEDAAEAAAABAAAACgEDAAEA"
            + "AAABAAAAEQEEAAEAAAAIAAAAEgEDAAEAAAABAAAAFQEDAAEAAAACAAAAFgED"
            + "AAEAAAACAAAAFwEEAAEAAAAOAAAAHAEDAAEAAAABAAAAKQEDAAIAAAAAAAEA"
            + "PQEDAAEAAAACAAAAPgEFAAIAAAAYAQAAPwEFAAYAAADoAAAAUgEDAAEAAAAC"
            + "AAAAAAAAAIXrUQAAAIAAw/WoAAAAAALNzEwAAAAAAc3MTAAAAIAAzcxMAAAA"
            + "AAKPwvUAAAAAEDcaoAAAAAACK4cKAAAAIAA=");
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxPixelsPerPage = 100,
            MaxTotalPixels = 100,
        });

        var result = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/tiff", "fixture.tiff"));

        var page = Assert.Single(result.Pages);
        Assert.Equal("image/tiff", result.VerifiedMimeType);
        Assert.Equal("image/png", page.NormalizedPng.MimeType);
        Assert.Equal(2, page.Width);
        Assert.Equal(2, page.Height);
        Assert.NotEmpty(page.NormalizedPng.Bytes);
        Assert.Equal(
            Fingerprinting.Sha256(page.NormalizedPng.Bytes),
            page.NormalizedPng.Sha256);
    }

    [Fact]
    public async Task RejectsTiffExpansionBeforeRasterAllocation()
    {
        var input = Convert.FromBase64String(
            "SUkqABYAAACAAAAP+CP+BQWDQIAQEBEAAAEDAAEAAAACAAAAAQEDAAEAAAAC"
            + "AAAAAgEDAAIAAAAQABAAAwEDAAEAAAAFAAAABgEDAAEAAAABAAAACgEDAAEA"
            + "AAABAAAAEQEEAAEAAAAIAAAAEgEDAAEAAAABAAAAFQEDAAEAAAACAAAAFgED"
            + "AAEAAAACAAAAFwEEAAEAAAAOAAAAHAEDAAEAAAABAAAAKQEDAAIAAAAAAAEA"
            + "PQEDAAEAAAACAAAAPgEFAAIAAAAYAQAAPwEFAAYAAADoAAAAUgEDAAEAAAAC"
            + "AAAAAAAAAIXrUQAAAIAAw/WoAAAAAALNzEwAAAAAAc3MTAAAAIAAzcxMAAAA"
            + "AAKPwvUAAAAAEDcaoAAAAAACK4cKAAAAIAA=");
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxTiffPixelsPerPage = 3,
            MaxTiffTotalPixels = 3,
        });

        var exception = await Assert.ThrowsAsync<PreprocessingException>(() =>
            service.ProcessAsync(
                new MemoryStream(input),
                new PreprocessingInput("image/tiff", "expanded.tiff")));

        Assert.Equal("page_pixel_limit", exception.Code);
    }

    [Fact]
    public async Task PreservesMultiPageTiffWhenEncodingProviderPdf()
    {
        var input = Convert.FromBase64String(
            "SUkqABYAAACAAAAP+CP+BQWDQIAQEBIA/gAEAAEAAAACAAAAAAEDAAEAAAAC"
            + "AAAAAQEDAAEAAAACAAAAAgEDAAIAAAAQABAAAwEDAAEAAAAFAAAABgEDAAEA"
            + "AAABAAAACgEDAAEAAAABAAAAEQEEAAEAAAAIAAAAEgEDAAEAAAABAAAAFQED"
            + "AAEAAAACAAAAFgEDAAEAAAACAAAAFwEEAAEAAAAOAAAAHAEDAAEAAAABAAAA"
            + "KQEDAAIAAAAAAAIAPQEDAAEAAAACAAAAPgEFAAIAAAAkAQAAPwEFAAYAAAD0"
            + "AAAAUgEDAAEAAAACAAAAQgEAAIXrUQAAAIAAw/WoAAAAAALNzEwAAAAAAc3M"
            + "TAAAAIAAzcxMAAAAAAKPwvUAAAAAEDcaoAAAAAACK4cKAAAAIACAP+BP8AQU"
            + "AQN/gGDQEBIA/gAEAAEAAAACAAAAAAEDAAEAAAACAAAAAQEDAAEAAAACAAAA"
            + "AgEDAAIAAAAQABAAAwEDAAEAAAAFAAAABgEDAAEAAAABAAAACgEDAAEAAAAB"
            + "AAAAEQEEAAEAAAA0AQAAEgEDAAEAAAABAAAAFQEDAAEAAAACAAAAFgEDAAEA"
            + "AAACAAAAFwEEAAEAAAAOAAAAHAEDAAEAAAABAAAAKQEDAAIAAAABAAIAPQED"
            + "AAEAAAACAAAAPgEFAAIAAABQAgAAPwEFAAYAAAAgAgAAUgEDAAEAAAACAAAA"
            + "AAAAAIXrUQAAAIAAw/WoAAAAAALNzEwAAAAAAc3MTAAAAIAAzcxMAAAAAAKP"
            + "wvUAAAAAEDcaoAAAAAACK4cKAAAAIAA=");
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxPixelsPerPage = 1_000_000,
            MaxTotalPixels = 2_000_000,
        });

        var tiff = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/tiff", "two-pages.tiff"));
        var pdfBytes = PreprocessedDocumentEncoder.ToPdf(
            tiff.Pages
                .Select(page => page with { DpiX = 72, DpiY = 72 })
                .ToArray());
        var pdf = await new PreprocessingService().ProcessAsync(
            new MemoryStream(pdfBytes),
            new PreprocessingInput("application/pdf", "two-pages.pdf"));

        Assert.Equal(2, tiff.Pages.Count);
        Assert.Equal(2, pdf.Pages.Count);
        Assert.True(pdfBytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8));
        Assert.NotEqual(
            tiff.Pages[0].NormalizedPng.Sha256,
            tiff.Pages[1].NormalizedPng.Sha256);
        Assert.NotEqual(
            pdf.Pages[0].NormalizedPng.Sha256,
            pdf.Pages[1].NormalizedPng.Sha256);
        var directOrderDistance =
            PageDistance(tiff.Pages[0], pdf.Pages[0])
            + PageDistance(tiff.Pages[1], pdf.Pages[1]);
        var reversedOrderDistance =
            PageDistance(tiff.Pages[0], pdf.Pages[1])
            + PageDistance(tiff.Pages[1], pdf.Pages[0]);
        Assert.True(
            directOrderDistance < reversedOrderDistance,
            $"Direct distance {directOrderDistance}; "
            + $"reversed distance {reversedOrderDistance}.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PdfEncoderPreservesA4PhysicalSizeAcrossMultiplePages(
        int pageCount)
    {
        // A4 at 150 DPI. The encoder must convert pixels to PDF points;
        // treating these pixel dimensions as points would rasterize each page
        // above the configured safety limit on the second pass.
        var input = CreatePng(1_240, 1_754, canvas =>
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 8,
                IsAntialias = false,
            };
            canvas.DrawRect(new SKRect(40, 40, 1_200, 1_714), paint);
        });
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxPages = 10,
            MaxPixelsPerPage = 12_000_000,
            MaxTotalPixels = 48_000_000,
        });
        var source = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/png", "a4-150dpi.png"));
        var first = Assert.Single(source.Pages) with
        {
            DpiX = 150,
            DpiY = 150,
        };
        var pages = Enumerable.Range(1, pageCount)
            .Select(pageNumber => first with { PageNumber = pageNumber })
            .ToArray();

        var pdf = PreprocessedDocumentEncoder.ToPdf(pages);
        var roundTrip = await service.ProcessAsync(
            new MemoryStream(pdf),
            new PreprocessingInput(
                "application/pdf",
                $"a4-{pageCount}-pages.pdf"));

        Assert.Equal(pageCount, roundTrip.Pages.Count);
        Assert.All(roundTrip.Pages, page =>
        {
            Assert.InRange(page.Width, 2_475, 2_485);
            Assert.InRange(page.Height, 3_503, 3_513);
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(50)]
    public async Task IncrementalPdfWriterAndSequentialRasterizerSupportBoundedLongDocuments(
        int pageCount)
    {
        const int width = 320;
        const int height = 450;
        var sourcePng = CreatePng(width, height, canvas =>
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 3,
                Style = SKPaintStyle.Stroke,
            };
            canvas.DrawRect(new SKRect(15, 20, 305, 430), paint);
            canvas.DrawLine(25, 70, 295, 70, paint);
            canvas.DrawCircle(170, 230, 55, paint);
        });
        var maximumTotalPixels = checked(500_000L * pageCount);
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxPages = 50,
            MaxPixelsPerPage = 500_000,
            MaxTotalPixels = maximumTotalPixels,
        });
        var source = await service.ProcessAsync(
            new MemoryStream(sourcePng),
            new PreprocessingInput("image/png", "fixture.png"));
        var page = Assert.Single(source.Pages);
        using var spool = new MemoryStream();
        using (var writer = PreprocessedDocumentEncoder.CreatePdfWriter(spool))
        {
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                writer.AppendPage(page with { PageNumber = pageNumber });
            }

            writer.Complete();
            Assert.Equal(pageCount, writer.PageCount);
            Assert.InRange(
                writer.OutputBytes,
                1,
                PreprocessingOptions.DefaultMaxInputBytes);
        }

        spool.Position = 0;
        var result = await service.ProcessAsync(
            spool,
            new PreprocessingInput(
                "application/pdf",
                $"synthetic-{pageCount}.pdf",
                MaximumPages: pageCount,
                MaximumNormalizedArtifactBytes:
                    PreprocessingOptions.DefaultMaxNormalizedArtifactBytes,
                MaximumTotalPixels: maximumTotalPixels));

        Assert.Equal(pageCount, result.Pages.Count);
        Assert.Equal(
            Enumerable.Range(1, pageCount),
            result.Pages.Select(item => item.PageNumber));
        Assert.Equal(pageCount - 1, result.RepeatedPages.Count);
        Assert.All(result.RepeatedPages, repeated =>
        {
            Assert.Equal(1, repeated.FirstPageNumber);
            Assert.InRange(repeated.DuplicatePageNumber, 2, pageCount);
        });
        Assert.All(result.Pages, processed =>
        {
            Assert.Equal(width, processed.Width);
            Assert.InRange(processed.Height, height - 1, height + 1);
        });
        Assert.InRange(
            result.Pages.Sum(processed =>
                processed.NormalizedPng.Bytes.LongLength
                + processed.ThumbnailPng.Bytes.LongLength),
            1,
            PreprocessingOptions.DefaultMaxNormalizedArtifactBytes);
        Assert.Equal(
            512L * 1024 * 1024,
            new PreprocessingOptions().MaxNormalizedArtifactBytes);
    }

    [Fact]
    public async Task VerticalPngTilesCoverUnevenPageWithoutOverlap()
    {
        var input = CreatePng(8, 11, canvas =>
        {
            using var paint = new SKPaint();
            for (var y = 0; y < 11; y++)
            {
                paint.Color = new SKColor((byte)(10 + 20 * y), 30, 40);
                canvas.DrawRect(new SKRect(0, y, 8, y + 1), paint);
            }
        });
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxPixelsPerPage = 1_000,
            MaxTotalPixels = 1_000,
        });
        var result = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/png"));
        var page = Assert.Single(result.Pages);

        var tiles = PreprocessedDocumentEncoder.ToVerticalPngTiles(page, 4);

        Assert.Equal([2, 3, 3, 3], tiles.Select(tile => tile.Height));
        Assert.Equal(page.Height, tiles.Sum(tile => tile.Height));
        using var full = SKBitmap.Decode(page.NormalizedPng.Bytes);
        Assert.NotNull(full);
        var sourceY = 0;
        foreach (var tile in tiles)
        {
            Assert.Equal("image/png", tile.MimeType);
            Assert.Equal(page.Width, tile.Width);
            Assert.True(tile.Bytes.AsSpan(0, 8).SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Equal(Fingerprinting.Sha256(tile.Bytes), tile.Sha256);
            using var decoded = SKBitmap.Decode(tile.Bytes);
            Assert.NotNull(decoded);
            for (var tileY = 0; tileY < decoded.Height; tileY++)
            {
                Assert.Equal(
                    full.GetPixel(3, sourceY),
                    decoded.GetPixel(3, tileY));
                sourceY++;
            }
        }

        Assert.Equal(page.Height, sourceY);
    }

    [Fact]
    public async Task RejectsTiffPageCountBeforeDecodingFrames()
    {
        var input = Convert.FromBase64String(
            "SUkqABYAAACAAAAP+CP+BQWDQIAQEBIA/gAEAAEAAAACAAAAAAEDAAEAAAAC"
            + "AAAAAQEDAAEAAAACAAAAAgEDAAIAAAAQABAAAwEDAAEAAAAFAAAABgEDAAEA"
            + "AAABAAAACgEDAAEAAAABAAAAEQEEAAEAAAAIAAAAEgEDAAEAAAABAAAAFQED"
            + "AAEAAAACAAAAFgEDAAEAAAACAAAAFwEEAAEAAAAOAAAAHAEDAAEAAAABAAAA"
            + "KQEDAAIAAAAAAAIAPQEDAAEAAAACAAAAPgEFAAIAAAAkAQAAPwEFAAYAAAD0"
            + "AAAAUgEDAAEAAAACAAAAQgEAAIXrUQAAAIAAw/WoAAAAAALNzEwAAAAAAc3M"
            + "TAAAAIAAzcxMAAAAAAKPwvUAAAAAEDcaoAAAAAACK4cKAAAAIACAP+BP8AQU"
            + "AQN/gGDQEBIA/gAEAAEAAAACAAAAAAEDAAEAAAACAAAAAQEDAAEAAAACAAAA"
            + "AgEDAAIAAAAQABAAAwEDAAEAAAAFAAAABgEDAAEAAAABAAAACgEDAAEAAAAB"
            + "AAAAEQEEAAEAAAA0AQAAEgEDAAEAAAABAAAAFQEDAAEAAAACAAAAFgEDAAEA"
            + "AAACAAAAFwEEAAEAAAAOAAAAHAEDAAEAAAABAAAAKQEDAAIAAAABAAIAPQED"
            + "AAEAAAACAAAAPgEFAAIAAABQAgAAPwEFAAYAAAAgAgAAUgEDAAEAAAACAAAA"
            + "AAAAAIXrUQAAAIAAw/WoAAAAAALNzEwAAAAAAc3MTAAAAIAAzcxMAAAAAAKP"
            + "wvUAAAAAEDcaoAAAAAACK4cKAAAAIAA=");
        var service = new PreprocessingService();

        var exception = await Assert.ThrowsAsync<PreprocessingException>(() =>
            service.ProcessAsync(
                new MemoryStream(input),
                new PreprocessingInput(
                    "image/tiff",
                    "two-pages.tiff",
                    MaximumPages: 1)));

        Assert.Equal("page_count_limit", exception.Code);
    }

    [Fact]
    public async Task RejectsMalformedTiffAsPermanentPreprocessingError()
    {
        byte[] input = [0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0];
        var service = new PreprocessingService();

        var exception = await Assert.ThrowsAsync<PreprocessingException>(() =>
            service.ProcessAsync(
                new MemoryStream(input),
                new PreprocessingInput("image/tiff", "malformed.tiff")));

        Assert.Equal("image_invalid", exception.Code);
    }

    [Fact]
    public async Task RejectsNormalizedArtifactsBeyondCallerLimit()
    {
        var input = CreatePng(20, 20, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawCircle(10, 10, 8, paint);
        });
        var service = new PreprocessingService();

        var exception = await Assert.ThrowsAsync<PreprocessingException>(() =>
            service.ProcessAsync(
                new MemoryStream(input),
                new PreprocessingInput(
                    "image/png",
                    "bounded.png",
                    MaximumNormalizedArtifactBytes: 1)));

        Assert.Equal("normalized_artifact_byte_limit", exception.Code);
    }

    [Fact]
    public async Task CropUsesMillionthsAndOptionalMargin()
    {
        var input = CreatePng(100, 50, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(25, 10, 75, 40), paint);
        });
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxPixelsPerPage = 10_000,
            MaxTotalPixels = 10_000,
        });
        var result = await service.ProcessAsync(
            new MemoryStream(input),
            new PreprocessingInput("image/png"));

        var crop = service.Crop(
            result.Pages[0],
            new MillionthsRegion(250_000, 200_000, 500_000, 600_000),
            marginMillionths: 10_000);

        Assert.Equal(52, crop.Width);
        Assert.Equal(32, crop.Height);
        using var decoded = SKBitmap.Decode(crop.Bytes);
        Assert.NotNull(decoded);
        var center = decoded.GetPixel(decoded.Width / 2, decoded.Height / 2);
        Assert.True(center.Red > 240);
        Assert.True(center.Green < 20);
    }

    [Fact]
    public async Task QualityDistinguishesBlankFromHighContrastPage()
    {
        var blank = CreatePng(128, 128, _ => { });
        var checker = CreatePng(128, 128, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            for (var y = 0; y < 128; y += 8)
            {
                for (var x = 0; x < 128; x += 8)
                {
                    if (((x + y) / 8) % 2 == 0)
                    {
                        canvas.DrawRect(new SKRect(x, y, x + 8, y + 8), paint);
                    }
                }
            }
        });
        var service = new PreprocessingService(new PreprocessingOptions
        {
            MaxPixelsPerPage = 20_000,
            MaxTotalPixels = 20_000,
        });

        var blankResult = await service.ProcessAsync(
            new MemoryStream(blank),
            new PreprocessingInput("image/png"));
        var checkerResult = await service.ProcessAsync(
            new MemoryStream(checker),
            new PreprocessingInput("image/png"));

        Assert.True(blankResult.Pages[0].Quality.IsProbablyBlank);
        Assert.False(checkerResult.Pages[0].Quality.IsProbablyBlank);
        Assert.True(
            checkerResult.Pages[0].Quality.LaplacianVariance
            > blankResult.Pages[0].Quality.LaplacianVariance);
    }

    [Fact]
    public async Task RejectsInputAndPixelLimitsBeforeArtifactCreation()
    {
        var input = CreatePng(20, 20, _ => { });
        var byteLimited = new PreprocessingService(new PreprocessingOptions
        {
            MaxInputBytes = 16,
            MaxPixelsPerPage = 1_000,
            MaxTotalPixels = 1_000,
        });
        var byteError = await Assert.ThrowsAsync<PreprocessingException>(() =>
            byteLimited.ProcessAsync(
                new MemoryStream(input),
                new PreprocessingInput("image/png")));
        Assert.Equal("input_byte_limit", byteError.Code);

        var pixelLimited = new PreprocessingService(new PreprocessingOptions
        {
            MaxPixelsPerPage = 300,
            MaxTotalPixels = 300,
        });
        var pixelError = await Assert.ThrowsAsync<PreprocessingException>(() =>
            pixelLimited.ProcessAsync(
                new MemoryStream(input),
                new PreprocessingInput("image/png")));
        Assert.Equal("page_pixel_limit", pixelError.Code);
    }

    [Fact]
    public async Task RejectsSignatureMismatch()
    {
        var service = new PreprocessingService();
        var exception = await Assert.ThrowsAsync<PreprocessingException>(() =>
            service.ProcessAsync(
                new MemoryStream([1, 2, 3, 4]),
                new PreprocessingInput("image/png")));

        Assert.Equal("signature_mismatch", exception.Code);
    }

    [Fact]
    public async Task AlignsIdenticalStructuralPageDeterministically()
    {
        var referenceBytes = CreateFormPng(240, 160);
        var service = CreateAlignmentService();
        var reference = await ProcessSinglePageAsync(
            service,
            referenceBytes);
        var candidate = await ProcessSinglePageAsync(
            service,
            referenceBytes);

        var first = service.AlignToReference(candidate, reference);
        var second = service.AlignToReference(candidate, reference);

        Assert.Equal("aligned", first.State);
        Assert.Equal(10_000, first.ScoreBasisPoints);
        Assert.Equal(0, first.RotationDegrees);
        Assert.Equal(0, first.OffsetXMillionths);
        Assert.Equal(0, first.OffsetYMillionths);
        Assert.Equal(
            reference.NormalizedPng.Sha256,
            first.ReferenceSha256);
        Assert.Equal(
            first.Page.NormalizedPng.Sha256,
            second.Page.NormalizedPng.Sha256);
    }

    [Fact]
    public async Task AlignsBoundedTranslationBeforeCreatingCrops()
    {
        var service = CreateAlignmentService();
        var reference = await ProcessSinglePageAsync(
            service,
            CreateFormPng(240, 160));
        var candidate = await ProcessSinglePageAsync(
            service,
            CreateFormPng(240, 160, offsetX: 16, offsetY: -8));

        var alignment = service.AlignToReference(candidate, reference);

        Assert.Equal("aligned", alignment.State);
        Assert.InRange(alignment.ScoreBasisPoints!.Value, 6_500, 10_000);
        Assert.InRange(alignment.OffsetXMillionths, 40_000, 90_000);
        Assert.InRange(alignment.OffsetYMillionths, -90_000, -20_000);
        Assert.Equal(reference.Width, alignment.Page.Width);
        Assert.Equal(reference.Height, alignment.Page.Height);
    }

    [Fact]
    public async Task CorrectsRightAngleRotation()
    {
        var source = CreateFormPng(240, 160);
        var service = CreateAlignmentService();
        var reference = await ProcessSinglePageAsync(service, source);
        var candidate = await ProcessSinglePageAsync(
            service,
            RotatePngClockwise(source));

        var alignment = service.AlignToReference(candidate, reference);

        Assert.Equal("aligned", alignment.State);
        Assert.Equal(270, alignment.RotationDegrees);
        Assert.InRange(alignment.ScoreBasisPoints!.Value, 6_500, 10_000);
        Assert.Equal(reference.Width, alignment.Page.Width);
        Assert.Equal(reference.Height, alignment.Page.Height);
    }

    [Fact]
    public async Task FailsClosedWhenReferenceHasNoStructuralAnchors()
    {
        var service = CreateAlignmentService();
        var reference = await ProcessSinglePageAsync(
            service,
            CreatePng(240, 160, _ => { }));
        var candidate = await ProcessSinglePageAsync(
            service,
            CreateFormPng(240, 160));

        var alignment = service.AlignToReference(candidate, reference);

        Assert.Equal("failed", alignment.State);
        Assert.Equal(0, alignment.ScoreBasisPoints);
        Assert.Equal(
            candidate.NormalizedPng.Sha256,
            alignment.Page.NormalizedPng.Sha256);
    }

    private static PreprocessingService CreateAlignmentService() =>
        new(new PreprocessingOptions
        {
            MaxPixelsPerPage = 100_000,
            MaxTotalPixels = 100_000,
            ThumbnailMaxDimension = 64,
        });

    private static async Task<PreprocessedPage> ProcessSinglePageAsync(
        PreprocessingService service,
        byte[] bytes)
    {
        var result = await service.ProcessAsync(
            new MemoryStream(bytes),
            new PreprocessingInput("image/png"));
        return Assert.Single(result.Pages);
    }

    private static byte[] CreateFormPng(
        int width,
        int height,
        int offsetX = 0,
        int offsetY = 0)
    {
        return CreatePng(width, height, canvas =>
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 3,
                Style = SKPaintStyle.Stroke,
            };
            canvas.DrawRect(
                new SKRect(
                    18 + offsetX,
                    14 + offsetY,
                    221 + offsetX,
                    145 + offsetY),
                paint);
            canvas.DrawLine(
                37 + offsetX,
                31 + offsetY,
                201 + offsetX,
                31 + offsetY,
                paint);
            canvas.DrawLine(
                53 + offsetX,
                50 + offsetY,
                53 + offsetX,
                130 + offsetY,
                paint);
            canvas.DrawLine(
                72 + offsetX,
                75 + offsetY,
                190 + offsetX,
                119 + offsetY,
                paint);
            canvas.DrawCircle(
                174 + offsetX,
                67 + offsetY,
                17,
                paint);
        });
    }

    private static byte[] RotatePngClockwise(byte[] bytes)
    {
        using var source = SKBitmap.Decode(bytes);
        Assert.NotNull(source);
        using var rotated = new SKBitmap(new SKImageInfo(
            source.Height,
            source.Width,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque));
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(rotated.Width, 0);
        canvas.RotateDegrees(90);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(
            image,
            new SKRect(0, 0, source.Width, source.Height),
            new SKSamplingOptions(
                SKFilterMode.Nearest,
                SKMipmapMode.None));
        using var output = SKImage.FromBitmap(rotated);
        using var encoded = output.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] CreatePng(
        int width,
        int height,
        Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        draw(canvas);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static long PageDistance(
        PreprocessedPage left,
        PreprocessedPage right)
    {
        using var leftBitmap = SKBitmap.Decode(left.NormalizedPng.Bytes)
            ?? throw new InvalidDataException(
                "Left fixture PNG could not be decoded.");
        using var rightBitmap = SKBitmap.Decode(right.NormalizedPng.Bytes)
            ?? throw new InvalidDataException(
                "Right fixture PNG could not be decoded.");
        const int gridSize = 16;
        long distance = 0;
        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var leftPixel = leftBitmap.GetPixel(
                    Math.Min(
                        leftBitmap.Width - 1,
                        ((2 * x + 1) * leftBitmap.Width)
                        / (2 * gridSize)),
                    Math.Min(
                        leftBitmap.Height - 1,
                        ((2 * y + 1) * leftBitmap.Height)
                        / (2 * gridSize)));
                var rightPixel = rightBitmap.GetPixel(
                    Math.Min(
                        rightBitmap.Width - 1,
                        ((2 * x + 1) * rightBitmap.Width)
                        / (2 * gridSize)),
                    Math.Min(
                        rightBitmap.Height - 1,
                        ((2 * y + 1) * rightBitmap.Height)
                        / (2 * gridSize)));
                var red = leftPixel.Red - rightPixel.Red;
                var green = leftPixel.Green - rightPixel.Green;
                var blue = leftPixel.Blue - rightPixel.Blue;
                distance += (red * red) + (green * green) + (blue * blue);
            }
        }

        return distance;
    }
}

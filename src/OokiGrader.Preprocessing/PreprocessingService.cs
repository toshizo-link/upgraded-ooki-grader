using System.Buffers;
using BitMiracle.LibTiff.Classic;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

namespace OokiGrader.Preprocessing;

public sealed class PreprocessingService : IPreprocessingService
{
    private const int StreamBufferSize = 128 * 1024;
    private static readonly SemaphoreSlim PdfRasterGate = new(1, 1);
    private readonly PreprocessingOptions _options;

    public PreprocessingService(PreprocessingOptions? options = null)
    {
        _options = options ?? new PreprocessingOptions();
        _options.Validate();
    }

    public async Task<PreprocessingResult> ProcessAsync(
        Stream source,
        PreprocessingInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(input);
        if (!source.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(source));
        }

        var mime = NormalizeMimeType(input.VerifiedMimeType);
        var bytes = await ReadBoundedAsync(
            source,
            _options.MaxInputBytes,
            cancellationToken).ConfigureAwait(false);
        ValidateSignature(bytes, mime);
        var inputSha256 = Fingerprinting.Sha256(bytes);

        IReadOnlyList<PreprocessedPage> pages;
        if (mime == "image/tiff")
        {
            pages = DecodeTiff(
                bytes,
                input,
                cancellationToken);
        }
        else
        {
            IReadOnlyList<SKBitmap> decoded = mime switch
            {
                "application/pdf" => await RasterizePdfAsync(
                        bytes,
                        EffectiveMaximumPages(input),
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => DecodeImage(
                    bytes,
                    EffectiveMaximumPages(input),
                    cancellationToken),
            };
            try
            {
                var processedPages = new List<PreprocessedPage>(decoded.Count);
                long totalPixels = 0;
                long totalArtifactBytes = 0;
                for (var index = 0; index < decoded.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageBitmap = decoded[index];
                    ValidateDimensions(
                        pageBitmap.Width,
                        pageBitmap.Height,
                        ref totalPixels);
                    var page = CreatePage(
                        pageBitmap,
                        index + 1,
                        mime == "application/pdf"
                            ? _options.PdfDpi
                            : _options.ImageDpi);
                    ValidateArtifactBytes(
                        page,
                        EffectiveMaximumArtifactBytes(input),
                        ref totalArtifactBytes);
                    processedPages.Add(page);
                }

                pages = processedPages;
            }
            finally
            {
                foreach (var bitmap in decoded)
                {
                    bitmap.Dispose();
                }
            }
        }

        var repeated = Fingerprinting.FindRepeatedPages(
            pages,
            _options.PerceptualRepeatHammingThreshold);
        var manifest = ManifestHasher.Compute(
            _options.PipelineVersion,
            inputSha256,
            mime,
            pages,
            repeated);
        return new PreprocessingResult(
            _options.PipelineVersion,
            inputSha256,
            mime,
            pages,
            repeated,
            manifest);
    }

    public ImageArtifact Crop(
        PreprocessedPage page,
        MillionthsRegion region,
        int marginMillionths = 0)
    {
        ArgumentNullException.ThrowIfNull(page);
        using var source = SKBitmap.Decode(page.NormalizedPng.Bytes)
            ?? throw new PreprocessingException(
                "normalized_page_invalid",
                "The normalized page could not be decoded.");
        var bounds = RegionMapper.ToPixels(
            region,
            source.Width,
            source.Height,
            marginMillionths);
        using var cropped = NewOpaqueBitmap(bounds.Width, bounds.Height);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.Clear(SKColors.White);
            using var sourceImage = SKImage.FromBitmap(source);
            canvas.DrawImage(
                sourceImage,
                new SKRect(
                    bounds.X,
                    bounds.Y,
                    bounds.X + bounds.Width,
                    bounds.Y + bounds.Height),
                new SKRect(0, 0, bounds.Width, bounds.Height),
                new SKSamplingOptions(SKFilterMode.Linear));
        }

        return EncodePng(cropped);
    }

    public PageAlignmentResult AlignToReference(
        PreprocessedPage page,
        PreprocessedPage reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        using var candidate = SKBitmap.Decode(page.NormalizedPng.Bytes)
            ?? throw new PreprocessingException(
                "normalized_page_invalid",
                "The normalized submission page could not be decoded.");
        using var referenceBitmap = SKBitmap.Decode(reference.NormalizedPng.Bytes)
            ?? throw new PreprocessingException(
                "alignment_reference_invalid",
                "The template alignment page could not be decoded.");
        if (candidate.Width <= 0
            || candidate.Height <= 0
            || referenceBitmap.Width <= 0
            || referenceBitmap.Height <= 0)
        {
            throw new PreprocessingException(
                "alignment_page_invalid",
                "A page used for alignment has invalid dimensions.");
        }

        var (gridWidth, gridHeight) = AlignmentGridDimensions(
            referenceBitmap.Width,
            referenceBitmap.Height);
        var referenceMask = BuildStructuralMask(
            referenceBitmap,
            gridWidth,
            gridHeight,
            cancellationToken);
        var minimumAnchors = Math.Max(
            24,
            checked((gridWidth * gridHeight) / 500));
        if (referenceMask.AnchorCount < minimumAnchors)
        {
            return FailedAlignment(page, reference, scoreBasisPoints: 0);
        }

        var (candidateGridWidth, candidateGridHeight) =
            AlignmentGridDimensions(candidate.Width, candidate.Height);
        var candidateBaseMask = BuildStructuralMask(
            candidate,
            candidateGridWidth,
            candidateGridHeight,
            cancellationToken);
        if (candidateBaseMask.AnchorCount < minimumAnchors)
        {
            return FailedAlignment(page, reference, scoreBasisPoints: 0);
        }

        AlignmentCandidate? best = null;
        foreach (var rotationDegrees in new[] { 0, 90, 180, 270 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateMask = RotateAndResizeMask(
                candidateBaseMask,
                rotationDegrees,
                gridWidth,
                gridHeight);
            if (candidateMask.AnchorCount < minimumAnchors)
            {
                continue;
            }

            var match = FindBestTranslation(
                referenceMask,
                candidateMask,
                rotationDegrees,
                cancellationToken);
            if (best is null || IsBetter(match, best))
            {
                best = match;
            }
        }

        if (best is null)
        {
            return FailedAlignment(page, reference, scoreBasisPoints: 0);
        }

        var scoreBasisPoints = (int)Math.Round(
            Math.Clamp(best.Score, 0d, 1d) * 10_000,
            MidpointRounding.AwayFromZero);
        if (scoreBasisPoints < _options.AlignmentWarningThresholdBasisPoints)
        {
            return FailedAlignment(page, reference, scoreBasisPoints);
        }

        using var oriented = Rotate(candidate, best.RotationDegrees);
        var offsetXPixels = (int)Math.Round(
            best.OffsetX * referenceBitmap.Width / (double)gridWidth,
            MidpointRounding.AwayFromZero);
        var offsetYPixels = (int)Math.Round(
            best.OffsetY * referenceBitmap.Height / (double)gridHeight,
            MidpointRounding.AwayFromZero);
        using var aligned = NewOpaqueBitmap(
            referenceBitmap.Width,
            referenceBitmap.Height);
        using (var canvas = new SKCanvas(aligned))
        {
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(oriented);
            canvas.DrawImage(
                image,
                new SKRect(
                    -offsetXPixels,
                    -offsetYPixels,
                    referenceBitmap.Width - offsetXPixels,
                    referenceBitmap.Height - offsetYPixels),
                new SKSamplingOptions(
                    SKFilterMode.Linear,
                    SKMipmapMode.None));
        }

        var alignedPage = CreatePage(
            aligned,
            page.PageNumber,
            page.DpiX);
        var state = scoreBasisPoints
            >= _options.AlignmentAcceptedThresholdBasisPoints
                ? "aligned"
                : "warning";
        return new PageAlignmentResult(
            alignedPage,
            state,
            scoreBasisPoints,
            best.RotationDegrees,
            ToMillionths(best.OffsetX, gridWidth),
            ToMillionths(best.OffsetY, gridHeight),
            reference.NormalizedPng.Sha256);
    }

    private async Task<IReadOnlyList<SKBitmap>> RasterizePdfAsync(
        byte[] pdfBytes,
        int maximumPages,
        CancellationToken cancellationToken)
    {
        await PdfRasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!OperatingSystem.IsWindows()
                && !OperatingSystem.IsLinux()
                && !OperatingSystem.IsMacOS())
            {
                throw new PreprocessingException(
                    "pdf_platform_unsupported",
                    "PDF rasterization is supported only on Windows, Linux, and macOS.");
            }

            cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CA1416 // PDFtoImage supports Windows, Linux, and macOS; the runtime guard above rejects every other platform.
            int pageCount;
            try
            {
                pageCount = Conversion.GetPageCount(pdfBytes, null);
            }
            catch (PdfPasswordProtectedException exception)
            {
                throw new PreprocessingException(
                    "pdf_encrypted",
                    "Encrypted or password-protected PDFs are not accepted.",
                    exception);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && exception is not PreprocessingException)
            {
                throw new PreprocessingException(
                    "pdf_invalid",
                    "The PDF could not be opened.",
                    exception);
            }

            if (pageCount is <= 0 || pageCount > maximumPages)
            {
                throw new PreprocessingException(
                    "page_count_limit",
                    $"The document page count must be between 1 and {maximumPages}.");
            }

            var pageSizes = Conversion.GetPageSizes(pdfBytes, null);
            long projectedTotalPixels = 0;
            foreach (var size in pageSizes)
            {
                var width = checked((int)Math.Ceiling(size.Width * _options.PdfDpi / 72d));
                var height = checked((int)Math.Ceiling(size.Height * _options.PdfDpi / 72d));
                ValidateDimensions(width, height, ref projectedTotalPixels);
            }

            var renderOptions = new RenderOptions
            {
                Dpi = _options.PdfDpi,
                WithAnnotations = true,
                WithFormFill = true,
                WithAspectRatio = true,
                UseTiling = true,
                Grayscale = false,
                BackgroundColor = SKColors.White,
            };
            var pages = new List<SKBitmap>(pageCount);
            try
            {
                for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bitmap = Conversion.ToImage(
                        pdfBytes,
                        new Index(pageIndex),
                        null,
                        renderOptions);
                    if (bitmap is null)
                    {
                        throw new PreprocessingException(
                            "pdf_page_invalid",
                            $"PDF page {pageIndex + 1} could not be rendered.");
                    }

                    pages.Add(bitmap);
                }

                return pages;
            }
#pragma warning restore CA1416
            catch
            {
                foreach (var page in pages)
                {
                    page.Dispose();
                }

                throw;
            }
        }
        finally
        {
            PdfRasterGate.Release();
        }
    }

    private List<SKBitmap> DecodeImage(
        byte[] bytes,
        int maximumPages,
        CancellationToken cancellationToken)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new PreprocessingException(
                "image_invalid",
                "The image could not be decoded.");
        var frameCount = Math.Max(1, codec.FrameCount);
        if (frameCount > maximumPages)
        {
            throw new PreprocessingException(
                "page_count_limit",
                $"The image contains more than {maximumPages} frames.");
        }

        long totalPixels = 0;
        ValidateDimensions(codec.Info.Width, codec.Info.Height, ref totalPixels);
        if (frameCount > 1)
        {
            totalPixels = checked((long)codec.Info.Width * codec.Info.Height * frameCount);
            if (totalPixels > _options.MaxTotalPixels)
            {
                throw new PreprocessingException(
                    "total_pixel_limit",
                    "The decoded document exceeds the total pixel limit.");
            }
        }

        var pages = new List<SKBitmap>(frameCount);
        try
        {
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new SKImageInfo(
                    codec.Info.Width,
                    codec.Info.Height,
                    SKColorType.Rgba8888,
                    SKAlphaType.Premul);
                var bitmap = new SKBitmap(info);
                var result = codec.GetPixels(
                    info,
                    bitmap.GetPixels(),
                    new SKCodecOptions(frameIndex));
                if (result != SKCodecResult.Success)
                {
                    bitmap.Dispose();
                    throw new PreprocessingException(
                        "image_decode_failed",
                        $"Image frame {frameIndex + 1} could not be decoded ({result}).");
                }

                pages.Add(OrientAndFlatten(
                    bitmap,
                    codec.EncodedOrigin,
                    cancellationToken));
                bitmap.Dispose();
            }

            return pages;
        }
        catch
        {
            foreach (var page in pages)
            {
                page.Dispose();
            }

            throw;
        }
    }

    private List<PreprocessedPage> DecodeTiff(
        byte[] bytes,
        PreprocessingInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var image = Tiff.ClientOpen(
                "uploaded-image",
                "r",
                source,
                new TiffStream())
                ?? throw new PreprocessingException(
                    "image_invalid",
                    "The TIFF image could not be decoded.");
            var maximumPages = Math.Min(
                EffectiveMaximumPages(input),
                _options.MaxTiffPages);
            var directoryCount = image.NumberOfDirectories();
            if (directoryCount is <= 0 || directoryCount > maximumPages)
            {
                throw new PreprocessingException(
                    "page_count_limit",
                    $"The TIFF page count must be between 1 and {maximumPages}.");
            }

            var pages = new List<PreprocessedPage>(directoryCount);
            long totalPixels = 0;
            long totalArtifactBytes = 0;
            for (short directoryIndex = 0;
                 directoryIndex < directoryCount;
                 directoryIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!image.SetDirectory(directoryIndex))
                {
                    throw new PreprocessingException(
                        "image_invalid",
                        $"TIFF page {directoryIndex + 1} could not be opened.");
                }

                var widthField = image.GetField(TiffTag.IMAGEWIDTH);
                var heightField = image.GetField(TiffTag.IMAGELENGTH);
                if (widthField is null
                    || widthField.Length == 0
                    || heightField is null
                    || heightField.Length == 0)
                {
                    throw new PreprocessingException(
                        "image_invalid",
                        "A TIFF page is missing its dimensions.");
                }

                var width = widthField[0].ToInt();
                var height = heightField[0].ToInt();
                ValidateDimensions(
                    width,
                    height,
                    ref totalPixels,
                    Math.Min(
                        _options.MaxPixelsPerPage,
                        _options.MaxTiffPixelsPerPage),
                    Math.Min(
                        _options.MaxTotalPixels,
                        _options.MaxTiffTotalPixels));
                var raster = new int[checked(width * height)];
                if (!image.ReadRGBAImageOriented(
                        width,
                        height,
                        raster,
                        Orientation.TOPLEFT,
                        stopOnError: true))
                {
                    throw new PreprocessingException(
                        "image_decode_failed",
                        $"TIFF page {directoryIndex + 1} could not be decoded.");
                }

                using var bitmap = NewOpaqueBitmap(width, height);
                var pixels = bitmap.GetPixelSpan();
                for (var y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = 0; x < width; x++)
                    {
                        var sourcePixel = raster[(y * width) + x];
                        var alpha = (byte)Tiff.GetA(sourcePixel);
                        var offset = checked(((y * width) + x) * 4);
                        pixels[offset] = BlendOnWhite(
                            (byte)Tiff.GetR(sourcePixel),
                            alpha);
                        pixels[offset + 1] = BlendOnWhite(
                            (byte)Tiff.GetG(sourcePixel),
                            alpha);
                        pixels[offset + 2] = BlendOnWhite(
                            (byte)Tiff.GetB(sourcePixel),
                            alpha);
                        pixels[offset + 3] = byte.MaxValue;
                    }
                }

                var page = CreatePage(
                    bitmap,
                    directoryIndex + 1,
                    _options.ImageDpi);
                ValidateArtifactBytes(
                    page,
                    EffectiveMaximumArtifactBytes(input),
                    ref totalArtifactBytes);
                pages.Add(page);
            }

            return pages;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not PreprocessingException)
        {
            throw new PreprocessingException(
                "image_invalid",
                "The TIFF image could not be decoded.",
                exception);
        }
    }

    private PreprocessedPage CreatePage(SKBitmap bitmap, int pageNumber, int dpi)
    {
        var normalized = EncodePng(bitmap);
        using var thumbnail = CreateThumbnail(bitmap);
        var thumbnailArtifact = EncodePng(thumbnail);
        var quality = QualityAnalyzer.Analyze(bitmap, _options);
        var fingerprint = new PageFingerprint(
            normalized.Sha256,
            Fingerprinting.PerceptualDHash(bitmap));
        return new PreprocessedPage(
            pageNumber,
            bitmap.Width,
            bitmap.Height,
            dpi,
            dpi,
            normalized,
            thumbnailArtifact,
            quality,
            fingerprint);
    }

    private (int Width, int Height) AlignmentGridDimensions(
        int width,
        int height)
    {
        var maximum = _options.AlignmentGridMaxDimension;
        if (width >= height)
        {
            return (
                maximum,
                Math.Max(24, (int)Math.Round(
                    maximum * height / (double)width,
                    MidpointRounding.AwayFromZero)));
        }

        return (
            Math.Max(24, (int)Math.Round(
                maximum * width / (double)height,
                MidpointRounding.AwayFromZero)),
            maximum);
    }

    private StructuralMask BuildStructuralMask(
        SKBitmap bitmap,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var cells = new bool[checked(width * height)];
        var anchorCount = 0;
        for (var targetY = 0; targetY < height; targetY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceTop = targetY * bitmap.Height / height;
            var sourceBottom = Math.Max(
                sourceTop + 1,
                (targetY + 1) * bitmap.Height / height);
            for (var targetX = 0; targetX < width; targetX++)
            {
                var sourceLeft = targetX * bitmap.Width / width;
                var sourceRight = Math.Max(
                    sourceLeft + 1,
                    (targetX + 1) * bitmap.Width / width);
                var darkPixels = 0;
                var sampledPixels = 0;
                var stepX = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        (sourceRight - sourceLeft) / 12d));
                var stepY = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        (sourceBottom - sourceTop) / 12d));
                for (var sourceY = sourceTop;
                     sourceY < sourceBottom;
                     sourceY += stepY)
                {
                    for (var sourceX = sourceLeft;
                         sourceX < sourceRight;
                         sourceX += stepX)
                    {
                        sampledPixels++;
                        if (Fingerprinting.Luminance(
                                bitmap.GetPixel(sourceX, sourceY))
                            <= _options.AlignmentDarkLuminanceThreshold)
                        {
                            darkPixels++;
                        }
                    }
                }

                var minimumDarkPixels = Math.Max(
                    1,
                    (int)Math.Ceiling(sampledPixels * 0.035d));
                if (darkPixels < minimumDarkPixels)
                {
                    continue;
                }

                cells[(targetY * width) + targetX] = true;
                anchorCount++;
            }
        }

        return new StructuralMask(
            width,
            height,
            cells,
            Dilate(cells, width, height),
            anchorCount);
    }

    private static StructuralMask RotateAndResizeMask(
        StructuralMask source,
        int rotationDegrees,
        int targetWidth,
        int targetHeight)
    {
        var swapsAxes = rotationDegrees is 90 or 270;
        var rotatedWidth = swapsAxes ? source.Height : source.Width;
        var rotatedHeight = swapsAxes ? source.Width : source.Height;
        var rotated = new bool[checked(rotatedWidth * rotatedHeight)];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (!source.Cells[(y * source.Width) + x])
                {
                    continue;
                }

                var (targetX, targetY) = rotationDegrees switch
                {
                    0 => (x, y),
                    90 => (source.Height - y - 1, x),
                    180 => (
                        source.Width - x - 1,
                        source.Height - y - 1),
                    270 => (y, source.Width - x - 1),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(rotationDegrees)),
                };
                rotated[(targetY * rotatedWidth) + targetX] = true;
            }
        }

        bool[] cells;
        if (rotatedWidth == targetWidth && rotatedHeight == targetHeight)
        {
            cells = rotated;
        }
        else
        {
            cells = new bool[checked(targetWidth * targetHeight)];
            for (var y = 0; y < targetHeight; y++)
            {
                var sourceY = Math.Min(
                    rotatedHeight - 1,
                    y * rotatedHeight / targetHeight);
                for (var x = 0; x < targetWidth; x++)
                {
                    var sourceX = Math.Min(
                        rotatedWidth - 1,
                        x * rotatedWidth / targetWidth);
                    cells[(y * targetWidth) + x] =
                        rotated[(sourceY * rotatedWidth) + sourceX];
                }
            }
        }

        return new StructuralMask(
            targetWidth,
            targetHeight,
            cells,
            Dilate(cells, targetWidth, targetHeight),
            cells.Count(item => item));
    }

    private AlignmentCandidate FindBestTranslation(
        StructuralMask reference,
        StructuralMask candidate,
        int rotationDegrees,
        CancellationToken cancellationToken)
    {
        var maximumOffsetX = (int)Math.Ceiling(
            reference.Width * _options.AlignmentMaxTranslationFraction);
        var maximumOffsetY = (int)Math.Ceiling(
            reference.Height * _options.AlignmentMaxTranslationFraction);
        var best = new AlignmentCandidate(
            rotationDegrees,
            0,
            0,
            Score(reference, candidate, 0, 0));
        for (var offsetY = -maximumOffsetY;
             offsetY <= maximumOffsetY;
             offsetY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var offsetX = -maximumOffsetX;
                 offsetX <= maximumOffsetX;
                 offsetX++)
            {
                if (offsetX == 0 && offsetY == 0)
                {
                    continue;
                }

                var current = new AlignmentCandidate(
                    rotationDegrees,
                    offsetX,
                    offsetY,
                    Score(reference, candidate, offsetX, offsetY));
                if (IsBetter(current, best))
                {
                    best = current;
                }
            }
        }

        return best;
    }

    private static double Score(
        StructuralMask reference,
        StructuralMask candidate,
        int offsetX,
        int offsetY)
    {
        var matchedReference = 0;
        for (var y = 0; y < reference.Height; y++)
        {
            for (var x = 0; x < reference.Width; x++)
            {
                if (!reference.Cells[(y * reference.Width) + x])
                {
                    continue;
                }

                var candidateX = x + offsetX;
                var candidateY = y + offsetY;
                if (candidateX >= 0
                    && candidateX < candidate.Width
                    && candidateY >= 0
                    && candidateY < candidate.Height
                    && candidate.Dilated[
                        (candidateY * candidate.Width) + candidateX])
                {
                    matchedReference++;
                }
            }
        }

        var matchedCandidate = 0;
        for (var y = 0; y < candidate.Height; y++)
        {
            for (var x = 0; x < candidate.Width; x++)
            {
                if (!candidate.Cells[(y * candidate.Width) + x])
                {
                    continue;
                }

                var referenceX = x - offsetX;
                var referenceY = y - offsetY;
                if (referenceX >= 0
                    && referenceX < reference.Width
                    && referenceY >= 0
                    && referenceY < reference.Height
                    && reference.Dilated[
                        (referenceY * reference.Width) + referenceX])
                {
                    matchedCandidate++;
                }
            }
        }

        var recall = matchedReference / (double)reference.AnchorCount;
        var precision = matchedCandidate / (double)candidate.AnchorCount;
        return recall + precision == 0
            ? 0
            : 2 * recall * precision / (recall + precision);
    }

    private static bool IsBetter(
        AlignmentCandidate candidate,
        AlignmentCandidate current)
    {
        const double epsilon = 0.0000001d;
        if (candidate.Score > current.Score + epsilon)
        {
            return true;
        }

        if (Math.Abs(candidate.Score - current.Score) > epsilon)
        {
            return false;
        }

        var candidateDisplacement =
            Math.Abs(candidate.OffsetX) + Math.Abs(candidate.OffsetY);
        var currentDisplacement =
            Math.Abs(current.OffsetX) + Math.Abs(current.OffsetY);
        if (candidateDisplacement != currentDisplacement)
        {
            return candidateDisplacement < currentDisplacement;
        }

        return candidate.RotationDegrees < current.RotationDegrees;
    }

    private static bool[] Dilate(bool[] source, int width, int height)
    {
        var destination = new bool[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!source[(y * width) + x])
                {
                    continue;
                }

                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    var targetY = y + offsetY;
                    if (targetY < 0 || targetY >= height)
                    {
                        continue;
                    }

                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        var targetX = x + offsetX;
                        if (targetX >= 0 && targetX < width)
                        {
                            destination[(targetY * width) + targetX] = true;
                        }
                    }
                }
            }
        }

        return destination;
    }

    private static SKBitmap Rotate(SKBitmap source, int rotationDegrees)
    {
        var swapsAxes = rotationDegrees is 90 or 270;
        var destination = NewOpaqueBitmap(
            swapsAxes ? source.Height : source.Width,
            swapsAxes ? source.Width : source.Height);
        using var canvas = new SKCanvas(destination);
        canvas.Clear(SKColors.White);
        switch (rotationDegrees)
        {
            case 0:
                break;
            case 90:
                canvas.Translate(destination.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case 180:
                canvas.Translate(destination.Width, destination.Height);
                canvas.RotateDegrees(180);
                break;
            case 270:
                canvas.Translate(0, destination.Height);
                canvas.RotateDegrees(270);
                break;
            default:
                destination.Dispose();
                throw new ArgumentOutOfRangeException(nameof(rotationDegrees));
        }

        using var sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(
            sourceImage,
            new SKRect(0, 0, source.Width, source.Height),
            new SKSamplingOptions(
                SKFilterMode.Nearest,
                SKMipmapMode.None));
        return destination;
    }

    private static PageAlignmentResult FailedAlignment(
        PreprocessedPage page,
        PreprocessedPage reference,
        int scoreBasisPoints) =>
        new(
            page,
            "failed",
            scoreBasisPoints,
            0,
            0,
            0,
            reference.NormalizedPng.Sha256);

    private static int ToMillionths(int value, int scale) =>
        (int)Math.Round(
            value * 1_000_000d / scale,
            MidpointRounding.AwayFromZero);

    private sealed record StructuralMask(
        int Width,
        int Height,
        bool[] Cells,
        bool[] Dilated,
        int AnchorCount);

    private sealed record AlignmentCandidate(
        int RotationDegrees,
        int OffsetX,
        int OffsetY,
        double Score);

    private SKBitmap CreateThumbnail(SKBitmap source)
    {
        var scale = Math.Min(
            1d,
            _options.ThumbnailMaxDimension
            / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var thumbnail = NewOpaqueBitmap(width, height);
        using var canvas = new SKCanvas(thumbnail);
        canvas.Clear(SKColors.White);
        using var sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(
            sourceImage,
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKFilterMode.Linear));
        return thumbnail;
    }

    private static SKBitmap OrientAndFlatten(
        SKBitmap source,
        SKEncodedOrigin origin,
        CancellationToken cancellationToken = default)
    {
        var swapsAxes = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        var width = swapsAxes ? source.Height : source.Width;
        var height = swapsAxes ? source.Width : source.Height;
        var destination = NewOpaqueBitmap(width, height);
        destination.Erase(SKColors.White);

        for (var y = 0; y < source.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                var (destinationX, destinationY) = origin switch
                {
                    SKEncodedOrigin.TopRight => (source.Width - x - 1, y),
                    SKEncodedOrigin.BottomRight =>
                        (source.Width - x - 1, source.Height - y - 1),
                    SKEncodedOrigin.BottomLeft => (x, source.Height - y - 1),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (source.Height - y - 1, x),
                    SKEncodedOrigin.RightBottom =>
                        (source.Height - y - 1, source.Width - x - 1),
                    SKEncodedOrigin.LeftBottom => (y, source.Width - x - 1),
                    _ => (x, y),
                };
                destination.SetPixel(
                    destinationX,
                    destinationY,
                    Flatten(source.GetPixel(x, y)));
            }
        }

        return destination;
    }

    private static SKColor Flatten(SKColor color)
    {
        if (color.Alpha == byte.MaxValue)
        {
            return new SKColor(color.Red, color.Green, color.Blue, byte.MaxValue);
        }

        var alpha = color.Alpha;
        return new SKColor(
            BlendOnWhite(color.Red, alpha),
            BlendOnWhite(color.Green, alpha),
            BlendOnWhite(color.Blue, alpha),
            byte.MaxValue);
    }

    private static byte BlendOnWhite(byte channel, byte alpha) =>
        (byte)((channel * alpha + 255 * (255 - alpha) + 127) / 255);

    private static SKBitmap NewOpaqueBitmap(int width, int height) =>
        new(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque));

    private static ImageArtifact EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new PreprocessingException(
                "png_encode_failed",
                "The normalized image could not be encoded.");
        var bytes = encoded.ToArray();
        return new ImageArtifact(
            "image/png",
            "png",
            bitmap.Width,
            bitmap.Height,
            bytes,
            Fingerprinting.Sha256(bytes));
    }

    private void ValidateDimensions(
        int width,
        int height,
        ref long totalPixels,
        long? maximumPixelsPerPage = null,
        long? maximumTotalPixels = null)
    {
        if (width <= 0
            || height <= 0
            || width > _options.MaxDimensionPixels
            || height > _options.MaxDimensionPixels)
        {
            throw new PreprocessingException(
                "dimension_limit",
                "A decoded page has invalid or excessive dimensions.");
        }

        var pixels = checked((long)width * height);
        if (pixels > (maximumPixelsPerPage ?? _options.MaxPixelsPerPage))
        {
            throw new PreprocessingException(
                "page_pixel_limit",
                "A decoded page exceeds the per-page pixel limit.");
        }

        totalPixels = checked(totalPixels + pixels);
        if (totalPixels > (maximumTotalPixels ?? _options.MaxTotalPixels))
        {
            throw new PreprocessingException(
                "total_pixel_limit",
                "The decoded document exceeds the total pixel limit.");
        }
    }

    private static void ValidateArtifactBytes(
        PreprocessedPage page,
        long maximumBytes,
        ref long totalBytes)
    {
        totalBytes = checked(
            totalBytes
            + page.NormalizedPng.Bytes.LongLength
            + page.ThumbnailPng.Bytes.LongLength);
        if (totalBytes > maximumBytes)
        {
            throw new PreprocessingException(
                "normalized_artifact_byte_limit",
                "The normalized document exceeds the artifact byte limit.");
        }
    }

    private int EffectiveMaximumPages(PreprocessingInput input)
    {
        if (input.MaximumPages is <= 0)
        {
            throw new PreprocessingException(
                "page_count_limit",
                "The requested page limit must be positive.");
        }

        return Math.Min(
            _options.MaxPages,
            input.MaximumPages ?? _options.MaxPages);
    }

    private long EffectiveMaximumArtifactBytes(PreprocessingInput input)
    {
        if (input.MaximumNormalizedArtifactBytes is <= 0)
        {
            throw new PreprocessingException(
                "normalized_artifact_byte_limit",
                "The requested artifact byte limit must be positive.");
        }

        return Math.Min(
            _options.MaxNormalizedArtifactBytes,
            input.MaximumNormalizedArtifactBytes
                ?? _options.MaxNormalizedArtifactBytes);
    }

    private static string NormalizeMimeType(string mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        return mimeType.Trim().ToLowerInvariant() switch
        {
            "application/pdf" => "application/pdf",
            "image/jpeg" => "image/jpeg",
            "image/png" => "image/png",
            "image/tiff" => "image/tiff",
            "image/webp" => "image/webp",
            _ => throw new PreprocessingException(
                "mime_unsupported",
                "Only PDF, JPEG, PNG, TIFF, and WebP inputs are supported."),
        };
    }

    private static void ValidateSignature(ReadOnlySpan<byte> bytes, string mime)
    {
        var valid = mime switch
        {
            "application/pdf" => bytes.Length >= 5
                && bytes[..5].SequenceEqual("%PDF-"u8),
            "image/jpeg" => bytes.Length >= 3
                && bytes[0] == 0xff
                && bytes[1] == 0xd8
                && bytes[2] == 0xff,
            "image/png" => bytes.Length >= 8
                && bytes[..8].SequenceEqual(
                    new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/tiff" => bytes.Length >= 4
                && ((bytes[0] == 0x49
                        && bytes[1] == 0x49
                        && bytes[2] == 0x2a
                        && bytes[3] == 0x00)
                    || (bytes[0] == 0x4d
                        && bytes[1] == 0x4d
                        && bytes[2] == 0x00
                        && bytes[3] == 0x2a)),
            "image/webp" => bytes.Length >= 12
                && bytes[..4].SequenceEqual("RIFF"u8)
                && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
        if (!valid)
        {
            throw new PreprocessingException(
                "signature_mismatch",
                "The file signature does not match the verified MIME type.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length - source.Position > maxBytes)
        {
            throw new PreprocessingException(
                "input_byte_limit",
                "The input exceeds the configured byte limit.");
        }

        using var output = new MemoryStream(
            source.CanSeek
                ? checked((int)Math.Min(source.Length - source.Position, int.MaxValue))
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > maxBytes)
                {
                    throw new PreprocessingException(
                        "input_byte_limit",
                        "The input exceeds the configured byte limit.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

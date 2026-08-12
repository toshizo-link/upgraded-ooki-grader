using System.Security.Cryptography;
using SkiaSharp;

namespace OokiGrader.Preprocessing;

/// <summary>
/// Applies an explicit clockwise quarter turn to normalized page artifacts.
/// This operation deliberately does not reuse or modify the deskew angle.
/// </summary>
public static class PageQuarterTurnRotator
{
    private static readonly HashSet<int> AllowedDegrees = [0, 90, 180, 270];

    public static PreprocessedPage Rotate(
        PreprocessedPage page,
        int clockwiseDegrees,
        string source,
        double? confidence = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!AllowedDegrees.Contains(clockwiseDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(clockwiseDegrees),
                "A page orientation must be 0, 90, 180, or 270 degrees clockwise.");
        }

        if (confidence is < 0 or > 1 || (confidence is not null && !double.IsFinite(confidence.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        if (clockwiseDegrees == 0)
        {
            return page with
            {
                AppliedRotationDegrees = 0,
                OrientationConfidence = confidence,
                OrientationSource = source,
            };
        }

        var normalized = RotateArtifact(page.NormalizedPng, clockwiseDegrees);
        var thumbnail = RotateArtifact(page.ThumbnailPng, clockwiseDegrees);
        using var normalizedBitmap = SKBitmap.Decode(normalized.Bytes)
            ?? throw new PreprocessingException(
                "rotated_page_invalid",
                $"Rotated page {page.PageNumber} could not be decoded.");
        var swapsAxes = clockwiseDegrees is 90 or 270;
        return page with
        {
            Width = normalized.Width,
            Height = normalized.Height,
            DpiX = swapsAxes ? page.DpiY : page.DpiX,
            DpiY = swapsAxes ? page.DpiX : page.DpiY,
            NormalizedPng = normalized,
            ThumbnailPng = thumbnail,
            Fingerprint = new PageFingerprint(
                normalized.Sha256,
                Fingerprinting.PerceptualDHash(normalizedBitmap)),
            AppliedRotationDegrees = clockwiseDegrees,
            OrientationConfidence = confidence,
            OrientationSource = source,
        };
    }

    private static ImageArtifact RotateArtifact(
        ImageArtifact artifact,
        int clockwiseDegrees)
    {
        if (artifact.MimeType != "image/png")
        {
            throw new PreprocessingException(
                "rotation_source_invalid",
                "Only normalized PNG artifacts can be quarter-turned.");
        }

        using var original = SKBitmap.Decode(artifact.Bytes)
            ?? throw new PreprocessingException(
                "rotation_source_invalid",
                "The normalized PNG artifact could not be decoded.");
        var swapsAxes = clockwiseDegrees is 90 or 270;
        using var rotated = new SKBitmap(new SKImageInfo(
            swapsAxes ? original.Height : original.Width,
            swapsAxes ? original.Width : original.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.White);
            switch (clockwiseDegrees)
            {
                case 90:
                    canvas.Translate(rotated.Width, 0);
                    canvas.RotateDegrees(90);
                    break;
                case 180:
                    canvas.Translate(rotated.Width, rotated.Height);
                    canvas.RotateDegrees(180);
                    break;
                case 270:
                    canvas.Translate(0, rotated.Height);
                    canvas.RotateDegrees(270);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(clockwiseDegrees));
            }

            using var image = SKImage.FromBitmap(original);
            canvas.DrawImage(
                image,
                new SKRect(0, 0, original.Width, original.Height),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        using var rotatedImage = SKImage.FromBitmap(rotated);
        using var encoded = rotatedImage.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new PreprocessingException(
                "rotation_encode_failed",
                "The quarter-turned page could not be encoded.");
        var bytes = encoded.ToArray();
        if (bytes.Length < 8
            || !bytes.AsSpan(0, 8).SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new PreprocessingException(
                "rotation_encode_failed",
                "The quarter-turned page was not encoded as PNG.");
        }

        return new ImageArtifact(
            "image/png",
            "png",
            rotated.Width,
            rotated.Height,
            bytes,
            Fingerprinting.Sha256(bytes));
    }
}

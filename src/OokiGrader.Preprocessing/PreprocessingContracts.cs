namespace OokiGrader.Preprocessing;

public sealed record PreprocessingOptions
{
    public const string DefaultPipelineVersion = "local-raster-v3";

    public string PipelineVersion { get; init; } = DefaultPipelineVersion;
    public long MaxInputBytes { get; init; } = 250L * 1024 * 1024;
    public int MaxPages { get; init; } = 1_000;
    public int MaxDimensionPixels { get; init; } = 20_000;
    public long MaxPixelsPerPage { get; init; } = 100_000_000;
    public long MaxTotalPixels { get; init; } = 500_000_000;
    public int MaxTiffPages { get; init; } = 200;
    public long MaxTiffPixelsPerPage { get; init; } = 12_000_000;
    public long MaxTiffTotalPixels { get; init; } = 100_000_000;
    public long MaxNormalizedArtifactBytes { get; init; } = 64L * 1024 * 1024;
    public int PdfDpi { get; init; } = 300;
    public int ImageDpi { get; init; } = 300;
    public int ThumbnailMaxDimension { get; init; } = 480;
    public int QualityMaxSamples { get; init; } = 250_000;
    public double BlankDarkPixelFraction { get; init; } = 0.002;
    public double BlurVarianceWarningThreshold { get; init; } = 45;
    public double ContrastWarningThreshold { get; init; } = 0.12;
    public double EdgeInkWarningThreshold { get; init; } = 0.08;
    public int PerceptualRepeatHammingThreshold { get; init; } = 4;
    public int AlignmentGridMaxDimension { get; init; } = 96;
    public double AlignmentMaxTranslationFraction { get; init; } = 0.08;
    public byte AlignmentDarkLuminanceThreshold { get; init; } = 210;
    public int AlignmentWarningThresholdBasisPoints { get; init; } = 4_500;
    public int AlignmentAcceptedThresholdBasisPoints { get; init; } = 6_500;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipelineVersion)
            || PipelineVersion.Length > 100
            || MaxInputBytes <= 0
            || MaxPages <= 0
            || MaxDimensionPixels <= 0
            || MaxPixelsPerPage <= 0
            || MaxTotalPixels < MaxPixelsPerPage
            || MaxTiffPages <= 0
            || MaxTiffPixelsPerPage <= 0
            || MaxTiffTotalPixels < MaxTiffPixelsPerPage
            || MaxNormalizedArtifactBytes <= 0
            || PdfDpi is < 72 or > 600
            || ImageDpi is < 72 or > 1_200
            || ThumbnailMaxDimension is < 64 or > 4_096
            || QualityMaxSamples is < 1_000 or > 2_000_000
            || BlankDarkPixelFraction is < 0 or > 1
            || BlurVarianceWarningThreshold < 0
            || ContrastWarningThreshold is < 0 or > 1
            || EdgeInkWarningThreshold is < 0 or > 1
            || PerceptualRepeatHammingThreshold is < 0 or > 64
            || AlignmentGridMaxDimension is < 48 or > 256
            || AlignmentMaxTranslationFraction is < 0 or > 0.25
            || AlignmentDarkLuminanceThreshold is < 32 or > 250
            || AlignmentWarningThresholdBasisPoints is < 0 or > 10_000
            || AlignmentAcceptedThresholdBasisPoints is < 0 or > 10_000
            || AlignmentWarningThresholdBasisPoints
                > AlignmentAcceptedThresholdBasisPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreprocessingOptions),
                "One or more preprocessing limits are invalid.");
        }
    }
}

public sealed record PreprocessingInput(
    string VerifiedMimeType,
    string? SourceName = null,
    int? MaximumPages = null,
    long? MaximumNormalizedArtifactBytes = null);

public sealed record ImageArtifact(
    string MimeType,
    string Extension,
    int Width,
    int Height,
    byte[] Bytes,
    string Sha256);

public sealed record PageQualityMetrics(
    double MeanLuminance,
    double ContrastP05,
    double ContrastP95,
    double DarkPixelFraction,
    double LightPixelFraction,
    double EdgeInkFraction,
    double LaplacianVariance,
    bool IsProbablyBlank,
    IReadOnlyList<string> Warnings);

public sealed record PageFingerprint(
    string ExactSha256,
    string PerceptualHash,
    string Version = "dhash-9x8-v1");

public sealed record PreprocessedPage(
    int PageNumber,
    int Width,
    int Height,
    int DpiX,
    int DpiY,
    ImageArtifact NormalizedPng,
    ImageArtifact ThumbnailPng,
    PageQualityMetrics Quality,
    PageFingerprint Fingerprint);

public sealed record PageAlignmentResult(
    PreprocessedPage Page,
    string State,
    int? ScoreBasisPoints,
    int RotationDegrees,
    int OffsetXMillionths,
    int OffsetYMillionths,
    string? ReferenceSha256)
{
    public static PageAlignmentResult NotConfigured(PreprocessedPage page) =>
        new(page, "not_configured", null, 0, 0, 0, null);
}

public enum RepeatedPageMatchKind
{
    Exact,
    Perceptual
}

public sealed record RepeatedPageMatch(
    int FirstPageNumber,
    int DuplicatePageNumber,
    RepeatedPageMatchKind Kind,
    int HammingDistance);

public sealed record PreprocessingResult(
    string PipelineVersion,
    string InputSha256,
    string VerifiedMimeType,
    IReadOnlyList<PreprocessedPage> Pages,
    IReadOnlyList<RepeatedPageMatch> RepeatedPages,
    string ManifestSha256);

public readonly record struct MillionthsRegion(
    int X,
    int Y,
    int Width,
    int Height)
{
    public const int Scale = 1_000_000;

    public void Validate()
    {
        if (X < 0
            || Y < 0
            || Width <= 0
            || Height <= 0
            || (long)X + Width > Scale
            || (long)Y + Height > Scale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MillionthsRegion),
                "The normalized region must have positive area and remain within the page.");
        }
    }
}

public readonly record struct PixelRegion(
    int X,
    int Y,
    int Width,
    int Height);

public interface IPreprocessingService
{
    Task<PreprocessingResult> ProcessAsync(
        Stream source,
        PreprocessingInput input,
        CancellationToken cancellationToken = default);

    ImageArtifact Crop(
        PreprocessedPage page,
        MillionthsRegion region,
        int marginMillionths = 0);

    PageAlignmentResult AlignToReference(
        PreprocessedPage page,
        PreprocessedPage reference,
        CancellationToken cancellationToken = default) =>
        PageAlignmentResult.NotConfigured(page);
}

public sealed class PreprocessingException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

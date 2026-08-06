using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OokiGrader.Preprocessing;

public static class ManifestHasher
{
    public static string Compute(
        string pipelineVersion,
        string inputSha256,
        string verifiedMimeType,
        IEnumerable<PreprocessedPage> pages,
        IEnumerable<RepeatedPageMatch> repeatedPages,
        IEnumerable<PageAlignmentResult>? pageAlignments = null,
        IEnumerable<string>? alignmentReferenceSha256s = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedMimeType);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(repeatedPages);

        var canonical = new StringBuilder();
        Append(canonical, "pipeline", pipelineVersion);
        Append(canonical, "input", inputSha256);
        Append(canonical, "mime", verifiedMimeType);
        foreach (var page in pages
                     .OrderBy(item => item.PageNumber))
        {
            Append(canonical, "page", Invariant(page.PageNumber));
            Append(canonical, "width", Invariant(page.Width));
            Append(canonical, "height", Invariant(page.Height));
            Append(canonical, "dpi-x", Invariant(page.DpiX));
            Append(canonical, "dpi-y", Invariant(page.DpiY));
            Append(canonical, "normalized", page.NormalizedPng.Sha256);
            Append(canonical, "thumbnail", page.ThumbnailPng.Sha256);
            Append(canonical, "exact", page.Fingerprint.ExactSha256);
            Append(canonical, "perceptual", page.Fingerprint.PerceptualHash);
            Append(canonical, "fingerprint-version", page.Fingerprint.Version);
            Append(canonical, "quality-mean", Invariant(page.Quality.MeanLuminance));
            Append(canonical, "quality-p05", Invariant(page.Quality.ContrastP05));
            Append(canonical, "quality-p95", Invariant(page.Quality.ContrastP95));
            Append(canonical, "quality-dark", Invariant(page.Quality.DarkPixelFraction));
            Append(canonical, "quality-light", Invariant(page.Quality.LightPixelFraction));
            Append(canonical, "quality-edge", Invariant(page.Quality.EdgeInkFraction));
            Append(
                canonical,
                "quality-laplacian",
                Invariant(page.Quality.LaplacianVariance));
            Append(canonical, "quality-blank", page.Quality.IsProbablyBlank ? "1" : "0");
            foreach (var warning in page.Quality.Warnings
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                Append(canonical, "warning", warning);
            }
        }

        foreach (var repeat in repeatedPages
                     .OrderBy(item => item.DuplicatePageNumber)
                     .ThenBy(item => item.FirstPageNumber))
        {
            Append(canonical, "repeat-first", Invariant(repeat.FirstPageNumber));
            Append(canonical, "repeat-duplicate", Invariant(repeat.DuplicatePageNumber));
            Append(canonical, "repeat-kind", repeat.Kind.ToString());
            Append(canonical, "repeat-distance", Invariant(repeat.HammingDistance));
        }

        if (pageAlignments is not null)
        {
            foreach (var alignment in pageAlignments
                         .OrderBy(item => item.Page.PageNumber))
            {
                Append(
                    canonical,
                    "alignment-page",
                    Invariant(alignment.Page.PageNumber));
                Append(canonical, "alignment-state", alignment.State);
                Append(
                    canonical,
                    "alignment-score",
                    alignment.ScoreBasisPoints is null
                        ? string.Empty
                        : Invariant(alignment.ScoreBasisPoints.Value));
                Append(
                    canonical,
                    "alignment-rotation",
                    Invariant(alignment.RotationDegrees));
                Append(
                    canonical,
                    "alignment-offset-x",
                    Invariant(alignment.OffsetXMillionths));
                Append(
                    canonical,
                    "alignment-offset-y",
                    Invariant(alignment.OffsetYMillionths));
                Append(
                    canonical,
                    "alignment-reference",
                    alignment.ReferenceSha256 ?? string.Empty);
            }
        }

        if (alignmentReferenceSha256s is not null)
        {
            var referenceIndex = 0;
            foreach (var referenceSha256 in alignmentReferenceSha256s)
            {
                Append(
                    canonical,
                    "alignment-reference-index",
                    Invariant(referenceIndex));
                Append(
                    canonical,
                    "alignment-reference-page",
                    referenceSha256);
                referenceIndex++;
            }
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string name, string value)
    {
        builder.Append(name);
        builder.Append(':');
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using SkiaSharp;

namespace OokiGrader.Preprocessing;

public static class Fingerprinting
{
    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string PerceptualDHash(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            throw new ArgumentException("The image must not be empty.", nameof(bitmap));
        }

        Span<byte> samples = stackalloc byte[9 * 8];
        for (var y = 0; y < 8; y++)
        {
            var sourceY = MapCoordinate(y, 8, bitmap.Height);
            for (var x = 0; x < 9; x++)
            {
                var sourceX = MapCoordinate(x, 9, bitmap.Width);
                samples[(y * 9) + x] = Luminance(bitmap.GetPixel(sourceX, sourceY));
            }
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (samples[(y * 9) + x] > samples[(y * 9) + x + 1])
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public static int HammingDistance(string left, string right)
    {
        if (!ulong.TryParse(
                left,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var leftValue)
            || !ulong.TryParse(
                right,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var rightValue)
            || left.Length != 16
            || right.Length != 16)
        {
            throw new ArgumentException(
                "Perceptual hashes must be 16 hexadecimal characters.");
        }

        return BitOperations.PopCount(leftValue ^ rightValue);
    }

    public static IReadOnlyList<RepeatedPageMatch> FindRepeatedPages(
        IReadOnlyList<PreprocessedPage> pages,
        int perceptualHammingThreshold)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (perceptualHammingThreshold is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(perceptualHammingThreshold));
        }

        var matches = new List<RepeatedPageMatch>();
        for (var current = 0; current < pages.Count; current++)
        {
            for (var prior = 0; prior < current; prior++)
            {
                var first = pages[prior];
                var candidate = pages[current];
                if (first.Width != candidate.Width || first.Height != candidate.Height)
                {
                    continue;
                }

                if (string.Equals(
                        first.Fingerprint.ExactSha256,
                        candidate.Fingerprint.ExactSha256,
                        StringComparison.Ordinal))
                {
                    matches.Add(new RepeatedPageMatch(
                        first.PageNumber,
                        candidate.PageNumber,
                        RepeatedPageMatchKind.Exact,
                        0));
                    break;
                }

                var distance = HammingDistance(
                    first.Fingerprint.PerceptualHash,
                    candidate.Fingerprint.PerceptualHash);
                if (distance <= perceptualHammingThreshold)
                {
                    matches.Add(new RepeatedPageMatch(
                        first.PageNumber,
                        candidate.PageNumber,
                        RepeatedPageMatchKind.Perceptual,
                        distance));
                    break;
                }
            }
        }

        return matches;
    }

    internal static byte Luminance(SKColor color) =>
        (byte)Math.Clamp(
            ((77 * color.Red) + (150 * color.Green) + (29 * color.Blue) + 128) >> 8,
            0,
            255);

    private static int MapCoordinate(int coordinate, int targetSize, int sourceSize)
    {
        if (sourceSize == 1)
        {
            return 0;
        }

        return (int)Math.Round(
            coordinate * (sourceSize - 1d) / (targetSize - 1d),
            MidpointRounding.AwayFromZero);
    }
}

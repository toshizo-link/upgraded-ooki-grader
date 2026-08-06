using SkiaSharp;

namespace OokiGrader.Preprocessing;

public static class QualityAnalyzer
{
    public static PageQualityMetrics Analyze(
        SKBitmap bitmap,
        PreprocessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var pixelCount = checked((long)bitmap.Width * bitmap.Height);
        var step = Math.Max(
            1,
            (int)Math.Ceiling(Math.Sqrt(pixelCount / (double)options.QualityMaxSamples)));
        var histogram = new long[256];
        long samples = 0;
        long dark = 0;
        long light = 0;
        double luminanceTotal = 0;
        double laplacianTotal = 0;
        double laplacianSquaredTotal = 0;
        long laplacianSamples = 0;

        for (var y = 0; y < bitmap.Height; y += step)
        {
            for (var x = 0; x < bitmap.Width; x += step)
            {
                var value = Fingerprinting.Luminance(bitmap.GetPixel(x, y));
                histogram[value]++;
                luminanceTotal += value;
                samples++;
                if (value < 64)
                {
                    dark++;
                }

                if (value > 245)
                {
                    light++;
                }

                if (x > 0 && x < bitmap.Width - 1 && y > 0 && y < bitmap.Height - 1)
                {
                    var laplacian =
                        Fingerprinting.Luminance(bitmap.GetPixel(x - 1, y))
                        + Fingerprinting.Luminance(bitmap.GetPixel(x + 1, y))
                        + Fingerprinting.Luminance(bitmap.GetPixel(x, y - 1))
                        + Fingerprinting.Luminance(bitmap.GetPixel(x, y + 1))
                        - (4 * value);
                    laplacianTotal += laplacian;
                    laplacianSquaredTotal += laplacian * laplacian;
                    laplacianSamples++;
                }
            }
        }

        if (samples == 0)
        {
            throw new ArgumentException("The image must not be empty.", nameof(bitmap));
        }

        var mean = luminanceTotal / samples / 255d;
        var darkFraction = dark / (double)samples;
        var lightFraction = light / (double)samples;
        var p05 = Percentile(histogram, samples, 0.05) / 255d;
        var p95 = Percentile(histogram, samples, 0.95) / 255d;
        var laplacianMean = laplacianSamples == 0
            ? 0
            : laplacianTotal / laplacianSamples;
        var laplacianVariance = laplacianSamples == 0
            ? 0
            : Math.Max(
                0,
                (laplacianSquaredTotal / laplacianSamples)
                - (laplacianMean * laplacianMean));
        var edgeInkFraction = EdgeInkFraction(bitmap, step);
        var blank = darkFraction < options.BlankDarkPixelFraction
            && p05 > 0.90;
        var warnings = new List<string>();
        if (!blank && laplacianVariance < options.BlurVarianceWarningThreshold)
        {
            warnings.Add("blur_low_detail");
        }

        if (p95 - p05 < options.ContrastWarningThreshold)
        {
            warnings.Add("contrast_low");
        }

        if (edgeInkFraction > options.EdgeInkWarningThreshold)
        {
            warnings.Add("ink_touches_page_edge");
        }

        if (mean < 0.25)
        {
            warnings.Add("page_too_dark");
        }

        return new PageQualityMetrics(
            mean,
            p05,
            p95,
            darkFraction,
            lightFraction,
            edgeInkFraction,
            laplacianVariance,
            blank,
            warnings);
    }

    private static int Percentile(long[] histogram, long samples, double percentile)
    {
        var target = Math.Max(1L, (long)Math.Ceiling(samples * percentile));
        long cumulative = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
            {
                return value;
            }
        }

        return 255;
    }

    private static double EdgeInkFraction(SKBitmap bitmap, int sampleStep)
    {
        var band = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 100);
        var stride = Math.Max(1, sampleStep);
        long samples = 0;
        long ink = 0;

        for (var y = 0; y < bitmap.Height; y += stride)
        {
            for (var x = 0; x < bitmap.Width; x += stride)
            {
                if (x >= band
                    && x < bitmap.Width - band
                    && y >= band
                    && y < bitmap.Height - band)
                {
                    continue;
                }

                samples++;
                if (Fingerprinting.Luminance(bitmap.GetPixel(x, y)) < 200)
                {
                    ink++;
                }
            }
        }

        return samples == 0 ? 0 : ink / (double)samples;
    }
}

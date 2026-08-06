namespace OokiGrader.Preprocessing;

public static class RegionMapper
{
    public static PixelRegion ToPixels(
        MillionthsRegion region,
        int pageWidth,
        int pageHeight,
        int marginMillionths = 0)
    {
        region.Validate();
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageWidth),
                "Page dimensions must be positive.");
        }

        if (marginMillionths is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(marginMillionths),
                "The crop margin must be between zero and 100,000 millionths.");
        }

        var left = ScaleFloor(region.X, pageWidth);
        var top = ScaleFloor(region.Y, pageHeight);
        var right = ScaleCeiling((long)region.X + region.Width, pageWidth);
        var bottom = ScaleCeiling((long)region.Y + region.Height, pageHeight);
        var marginX = ScaleCeiling(marginMillionths, pageWidth);
        var marginY = ScaleCeiling(marginMillionths, pageHeight);

        left = Math.Max(0, left - marginX);
        top = Math.Max(0, top - marginY);
        right = Math.Min(pageWidth, right + marginX);
        bottom = Math.Min(pageHeight, bottom + marginY);

        if (right <= left || bottom <= top)
        {
            throw new InvalidOperationException(
                "The normalized region produced an empty pixel crop.");
        }

        return new PixelRegion(left, top, right - left, bottom - top);
    }

    private static int ScaleFloor(long normalized, int pixels) =>
        checked((int)(normalized * pixels / MillionthsRegion.Scale));

    private static int ScaleCeiling(long normalized, int pixels) =>
        checked((int)((normalized * pixels + MillionthsRegion.Scale - 1)
            / MillionthsRegion.Scale));
}

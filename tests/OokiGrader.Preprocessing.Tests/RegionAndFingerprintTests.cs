namespace OokiGrader.Preprocessing.Tests;

public sealed class RegionAndFingerprintTests
{
    [Fact]
    public void MapsMillionthsWithFloorCeilingAndClampedMargin()
    {
        var mapped = RegionMapper.ToPixels(
            new MillionthsRegion(100_000, 200_000, 333_333, 400_000),
            pageWidth: 101,
            pageHeight: 51,
            marginMillionths: 10_000);

        Assert.Equal(new PixelRegion(8, 9, 38, 23), mapped);

        var full = RegionMapper.ToPixels(
            new MillionthsRegion(0, 0, 1_000_000, 1_000_000),
            101,
            51,
            100_000);
        Assert.Equal(new PixelRegion(0, 0, 101, 51), full);
    }

    [Fact]
    public void RejectsOutOfBoundsRegion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RegionMapper.ToPixels(
                new MillionthsRegion(900_000, 0, 200_000, 100_000),
                100,
                100));
    }

    [Fact]
    public void ComputesHammingDistance()
    {
        Assert.Equal(
            1,
            Fingerprinting.HammingDistance(
                "0000000000000000",
                "0000000000000001"));
        Assert.Equal(
            64,
            Fingerprinting.HammingDistance(
                "0000000000000000",
                "ffffffffffffffff"));
    }

    [Fact]
    public void DetectsExactAndPerceptualRepeatedPages()
    {
        var pages = new[]
        {
            Page(1, new string('a', 64), "0000000000000000"),
            Page(2, new string('a', 64), "ffffffffffffffff"),
            Page(3, new string('b', 64), "0000000000000001"),
        };

        var repeats = Fingerprinting.FindRepeatedPages(pages, 1);

        Assert.Equal(2, repeats.Count);
        Assert.Equal(RepeatedPageMatchKind.Exact, repeats[0].Kind);
        Assert.Equal(1, repeats[0].FirstPageNumber);
        Assert.Equal(2, repeats[0].DuplicatePageNumber);
        Assert.Equal(RepeatedPageMatchKind.Perceptual, repeats[1].Kind);
        Assert.Equal(1, repeats[1].HammingDistance);
    }

    [Fact]
    public void ManifestHashChangesWhenPageEvidenceChanges()
    {
        var firstPage = Page(1, new string('a', 64), "0000000000000000");
        var changedPage = Page(1, new string('b', 64), "0000000000000000");

        var first = ManifestHasher.Compute(
            "pipeline-v1",
            new string('c', 64),
            "image/png",
            [firstPage],
            []);
        var same = ManifestHasher.Compute(
            "pipeline-v1",
            new string('c', 64),
            "image/png",
            [firstPage],
            []);
        var changed = ManifestHasher.Compute(
            "pipeline-v1",
            new string('c', 64),
            "image/png",
            [changedPage],
            []);

        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
    }

    private static PreprocessedPage Page(
        int pageNumber,
        string exact,
        string perceptual)
    {
        var artifact = new ImageArtifact(
            "image/png",
            "png",
            100,
            100,
            [],
            exact);
        return new PreprocessedPage(
            pageNumber,
            100,
            100,
            300,
            300,
            artifact,
            artifact,
            new PageQualityMetrics(1, 1, 1, 0, 1, 0, 0, true, []),
            new PageFingerprint(exact, perceptual));
    }
}

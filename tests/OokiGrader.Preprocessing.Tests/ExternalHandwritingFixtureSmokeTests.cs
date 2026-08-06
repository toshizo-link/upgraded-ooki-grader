namespace OokiGrader.Preprocessing.Tests;

public sealed class ExternalHandwritingFixtureSmokeTests
{
    [ExternalFixtureFact]
    public async Task ProcessesPinnedHandwrittenExamAndJapaneseHandwritingFixtures()
    {
        var repositoryRoot = Environment.GetEnvironmentVariable(
            "OOKI_EXTERNAL_FIXTURE_ROOT")
            ?? throw new InvalidOperationException(
                "OOKI_EXTERNAL_FIXTURE_ROOT was not supplied.");

        var fixtures = new[]
        {
            new Fixture(
                "tmp/handwritten-exam-fixtures/Student_18.pdf",
                "application/pdf",
                2,
                "68622bdd43848e17b487ab47a531eaaff578b1b29e9f9239fa90c59d0075c034"),
            new Fixture(
                "tmp/handwritten-exam-fixtures/Student_19.pdf",
                "application/pdf",
                4,
                "b49444fb96457a21b3a02c45ca2f8d885e34ff0e15a22debfda93dc2d2b3b854"),
            new Fixture(
                "tmp/handwritten-exam-fixtures/Student_26.pdf",
                "application/pdf",
                2,
                "d92dfd9886e1363f99f2ce282ff86fc5796cf9e71c9a830367b52caad686bd96"),
            new Fixture(
                "tmp/japanese-handwriting-fixtures/0051_01_2_2_1_h.jpg",
                "image/jpeg",
                1,
                "0239932e51aad04001834ae953541434f07232c2c71ad4bcc0bd3358e6d68aa1"),
            new Fixture(
                "tmp/japanese-handwriting-fixtures/0128_45_1_2_3_h.jpg",
                "image/jpeg",
                1,
                "32336f4bf9c16db8734d204181a31cbb2d26a927087e16601efe5a8b9c040d2c"),
        };
        var service = new PreprocessingService();

        foreach (var fixture in fixtures)
        {
            var path = Path.GetFullPath(
                fixture.RelativePath,
                repositoryRoot);
            Assert.True(
                File.Exists(path),
                $"Fixture is missing: {fixture.RelativePath}");

            await using var stream = File.OpenRead(path);
            var result = await service.ProcessAsync(
                stream,
                new PreprocessingInput(
                    fixture.MimeType,
                    Path.GetFileName(path)));

            Assert.Equal(fixture.Sha256, result.InputSha256);
            Assert.Equal(fixture.PageCount, result.Pages.Count);
            Assert.Equal(64, result.ManifestSha256.Length);
            Assert.All(result.Pages, page =>
            {
                Assert.True(page.Width > 0);
                Assert.True(page.Height > 0);
                Assert.NotEmpty(page.NormalizedPng.Bytes);
                Assert.NotEmpty(page.ThumbnailPng.Bytes);
                Assert.Equal(64, page.Fingerprint.ExactSha256.Length);
                Assert.Equal(16, page.Fingerprint.PerceptualHash.Length);
            });
        }
    }

    private sealed record Fixture(
        string RelativePath,
        string MimeType,
        int PageCount,
        string Sha256);

    private sealed class ExternalFixtureFactAttribute : FactAttribute
    {
        public ExternalFixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(
                        "OOKI_EXTERNAL_FIXTURE_ROOT")))
            {
                Skip = "Set OOKI_EXTERNAL_FIXTURE_ROOT after running both "
                    + "fixture downloaders.";
            }
        }
    }
}

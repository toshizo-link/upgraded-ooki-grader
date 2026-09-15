using PdfSharp.Pdf.IO;
using OokiGrader.SchoolManager.PassFail;

namespace OokiGrader.SchoolManager.Tests;

public sealed class PassFailPdfRendererTests
{
    [Fact]
    public void RenderProducesVerifiedLandscapePdfForWideTable()
    {
        var columns = Enumerable.Range(1, 25)
            .Select(index => $"第{index}回")
            .ToArray();
        var rows = Enumerable.Range(1, 32)
            .Select(index => new PassFailProjectionRow(
                index == 2 ? "本人" : $"同級生{index:00}",
                columns.Select(column => index % 2 == 0 ? "○" : "●").ToArray()))
            .ToArray();
        var projection = new PassFailProjection(
            "小６ 社会暗記テスト合格表",
            "小6",
            "A",
            columns,
            rows,
            "privacy-safe-canonical-text");

        var result = PassFailPdfRenderer.Render(
            projection,
            new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));

        Assert.True(result.Bytes.Length > 1_000);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.Bytes))
                .ToLowerInvariant(),
            result.Sha256);
        using var document = PdfReader.Open(
            new MemoryStream(result.Bytes),
            PdfDocumentOpenMode.Import);
        Assert.Equal(result.PageCount, document.PageCount);
        Assert.True(document.PageCount >= 4);
        Assert.All(document.Pages.Cast<PdfSharp.Pdf.PdfPage>(), page =>
            Assert.True(page.Width.Point > page.Height.Point));
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using OokiGrader.Reports.Pdf;
using PdfSharp.Pdf.IO;

namespace OokiGrader.Reports.Pdf.Tests;

public sealed class ResultPdfRendererTests
{
    [Fact]
    public void JapaneseLongReportRendersEmbeddedFontAndMultiplePages()
    {
        var report = CreateReport(questionCount: 45, longQuestions: true);

        var rendered = new ResultPdfRenderer().Render(report);
        WriteDiagnosticPdf("japanese-result-report-sample.pdf", rendered.PdfBytes);

        Assert.True(rendered.PageCount >= 3);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(rendered.PdfBytes))
                .ToLowerInvariant(),
            rendered.Sha256);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(rendered.PdfBytes, 0, 5));
        using var parsed = PdfReader.Open(
            new MemoryStream(rendered.PdfBytes, writable: false));
        Assert.Equal(rendered.PageCount, parsed.PageCount);

        var pdfSyntax = Encoding.Latin1.GetString(rendered.PdfBytes);
        Assert.Contains("/FontFile2", pdfSyntax, StringComparison.Ordinal);
        Assert.Contains("/ToUnicode", pdfSyntax, StringComparison.Ordinal);
    }

    [Fact]
    public void SameReportProducesSameVerifiedBytes()
    {
        var report = CreateReport(questionCount: 3, longQuestions: false);
        var renderer = new ResultPdfRenderer();

        var first = renderer.Render(report);
        var second = renderer.Render(report);
        WriteDiagnosticPdf("deterministic-first.pdf", first.PdfBytes);
        WriteDiagnosticPdf("deterministic-second.pdf", second.PdfBytes);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.PdfBytes, second.PdfBytes);
    }

    [Fact]
    public void ZeroQuestionReportRendersOnePage()
    {
        var report = CreateReport(questionCount: 0, longQuestions: false);

        var rendered = new ResultPdfRenderer().Render(report);

        Assert.Equal(1, rendered.PageCount);
    }

    [Fact]
    public void SourceHashChangesForCorrectionButNotReportIdentifier()
    {
        var report = CreateReport(questionCount: 1, longQuestions: false);
        var sameSourceNewExport = report with { ReportId = "01NEWEXPORT" };
        var correction = report with
        {
            EarnedPointsMilli = 500,
            IsCorrectedGrade = true,
            Questions =
            [
                report.Questions[0] with
                {
                    AwardedPointsMilli = 500,
                    Outcome = "partial",
                    IsCorrected = true,
                },
            ],
        };

        Assert.Equal(
            ResultReportSourceHasher.Compute(report),
            ResultReportSourceHasher.Compute(sameSourceNewExport));
        Assert.NotEqual(
            ResultReportSourceHasher.Compute(report),
            ResultReportSourceHasher.Compute(correction));
    }

    [Fact]
    public void RendererRejectsTotalsThatDoNotMatchQuestions()
    {
        var report = CreateReport(questionCount: 1, longQuestions: false) with
        {
            EarnedPointsMilli = 0,
        };

        var error = Assert.Throws<ArgumentException>(
            () => new ResultPdfRenderer().Render(report));

        Assert.Contains("totals", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ResultReportDocument CreateReport(
        int questionCount,
        bool longQuestions)
    {
        var questions = Enumerable.Range(1, questionCount)
            .Select(index => new ResultReportQuestion(
                index.ToString(CultureInfo.InvariantCulture),
                longQuestions
                    ? "次の文章を読んで、登場人物の気持ちの変化を本文中の表現に即して説明しなさい。" +
                        string.Concat(Enumerable.Repeat(
                            "これは日本語の長い設問が列幅に合わせて正しく折り返されることを確認する文章です。",
                            2))
                    : $"漢字「大木」の読みを答えなさい（{index}）。",
                index % 7 == 0
                    ? null
                    : "おおき。理由を含む少し長い手書き解答の認識結果です。",
                1_000,
                1_000,
                index % 7 == 0 ? "blank" : "correct",
                index == 2,
                index == 2 ? "教師が表記を確認し、現在の得点へ訂正しました。" : null))
            .ToArray();
        return new ResultReportDocument(
            "01JREPORT000000000000000001",
            "大木学習塾",
            "大木 花子",
            "S-0042",
            "国語・漢字確認テスト 第4回",
            new DateOnly(2026, 7, 27),
            3,
            7,
            questions.Sum(item => item.AwardedPointsMilli),
            questions.Sum(item => item.MaximumPointsMilli),
            questions,
            new DateTimeOffset(2026, 7, 27, 8, 15, 0, TimeSpan.Zero),
            IsCorrectedGrade: questions.Any(item => item.IsCorrected));
    }

    private static void WriteDiagnosticPdf(string filename, byte[] bytes)
    {
        var output = Environment.GetEnvironmentVariable("OOKI_REPORT_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, filename), bytes);
    }
}

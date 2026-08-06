using OokiGrader.Host.Api;

namespace OokiGrader.IntegrationTests;

public sealed class SubmissionQualityWarningsTests
{
    [Fact]
    public void BuildsBoundedLocalizedAlignmentAndImageWarnings()
    {
        var warnings = SubmissionQualityWarnings.Build(
            """
            {
              "pageCountMismatch": true,
              "pages": [
                {
                  "PageNumber": 1,
                  "IsProbablyBlank": true,
                  "alignment": { "state": "warning" },
                  "warnings": [
                    "blur_low_detail",
                    "contrast_low",
                    "ink_touches_page_edge",
                    "page_too_dark",
                    "unknown_future_warning"
                  ]
                }
              ]
            }
            """);

        Assert.Equal(7, warnings.Length);
        Assert.Contains(
            "提出ページ数がテンプレートと一致していません。",
            warnings);
        Assert.Contains(
            "1ページ目の位置合わせ精度を確認してください。",
            warnings);
        Assert.Contains(
            "1ページ目が空白の可能性があります。",
            warnings);
        Assert.DoesNotContain(
            warnings,
            item => item.Contains(
                "unknown_future_warning",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void InvalidOrLegacySummariesDoNotInventWarnings(string? json)
    {
        Assert.Empty(SubmissionQualityWarnings.Build(json));
    }

    [Fact]
    public void ReportsFailedAndUnconfiguredAlignmentWithoutRawMetadata()
    {
        var warnings = SubmissionQualityWarnings.Build(
            """
            {
              "pages": [
                {
                  "pageNumber": 2,
                  "alignment": {
                    "state": "failed",
                    "referenceSha256": "private-internal-value"
                  },
                  "warnings": []
                },
                {
                  "pageNumber": 3,
                  "alignment": { "state": "not_configured" },
                  "warnings": []
                }
              ]
            }
            """);

        Assert.Equal(2, warnings.Length);
        Assert.Contains(
            "2ページ目をテンプレートへ安全に位置合わせできませんでした。",
            warnings);
        Assert.Contains(
            "3ページ目には位置合わせ基準が設定されていません。",
            warnings);
        Assert.DoesNotContain(
            warnings,
            item => item.Contains(
                "private-internal-value",
                StringComparison.Ordinal));
    }
}

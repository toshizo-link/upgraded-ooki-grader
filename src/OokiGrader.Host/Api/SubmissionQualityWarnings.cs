using System.Text.Json;

namespace OokiGrader.Host.Api;

internal static class SubmissionQualityWarnings
{
    private const int MaximumSummaryCharacters = 1_000_000;
    private const int MaximumWarnings = 20;

    public static string[] Build(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)
            || json.Length > MaximumSummaryCharacters)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    MaxDepth = 16,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var warnings = new List<string>();
            if (root.TryGetProperty(
                    "pageCountMismatch",
                    out var pageCountMismatch)
                && pageCountMismatch.ValueKind == JsonValueKind.True)
            {
                warnings.Add(
                    "提出ページ数がテンプレートと一致していません。");
            }

            if (!root.TryGetProperty("pages", out var pages)
                || pages.ValueKind != JsonValueKind.Array)
            {
                return warnings.ToArray();
            }

            foreach (var page in pages.EnumerateArray())
            {
                if (warnings.Count >= MaximumWarnings)
                {
                    break;
                }

                if (page.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var pageNumber = ReadPageNumber(page);
                var pageLabel = pageNumber is null
                    ? "ページ"
                    : $"{pageNumber}ページ目";
                if (page.TryGetProperty(
                        "alignment",
                        out var alignment)
                    && alignment.ValueKind == JsonValueKind.Object
                    && alignment.TryGetProperty(
                        "state",
                        out var alignmentState)
                    && alignmentState.ValueKind == JsonValueKind.String)
                {
                    var alignmentWarning = alignmentState.GetString() switch
                    {
                        "failed" =>
                            $"{pageLabel}をテンプレートへ安全に位置合わせできませんでした。",
                        "warning" =>
                            $"{pageLabel}の位置合わせ精度を確認してください。",
                        "not_configured" =>
                            $"{pageLabel}には位置合わせ基準が設定されていません。",
                        _ => null,
                    };
                    if (alignmentWarning is not null)
                    {
                        warnings.Add(alignmentWarning);
                    }
                }

                if (ReadBoolean(page, "IsProbablyBlank"))
                {
                    warnings.Add($"{pageLabel}が空白の可能性があります。");
                }

                if (!page.TryGetProperty(
                        "warnings",
                        out var pageWarnings)
                    || pageWarnings.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var warning in pageWarnings.EnumerateArray())
                {
                    if (warnings.Count >= MaximumWarnings
                        || warning.ValueKind != JsonValueKind.String)
                    {
                        break;
                    }

                    var message = warning.GetString() switch
                    {
                        "blur_low_detail" =>
                            $"{pageLabel}がぼやけている可能性があります。",
                        "contrast_low" =>
                            $"{pageLabel}のコントラストが低い可能性があります。",
                        "ink_touches_page_edge" =>
                            $"{pageLabel}の記入が端で切れている可能性があります。",
                        "page_too_dark" =>
                            $"{pageLabel}が暗すぎる可能性があります。",
                        _ => null,
                    };
                    if (message is not null)
                    {
                        warnings.Add(message);
                    }
                }
            }

            return warnings
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumWarnings)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int? ReadPageNumber(JsonElement page)
    {
        if ((page.TryGetProperty("PageNumber", out var pageNumber)
                || page.TryGetProperty("pageNumber", out pageNumber))
            && pageNumber.TryGetInt32(out var value)
            && value > 0)
        {
            return value;
        }

        return null;
    }

    private static bool ReadBoolean(
        JsonElement value,
        string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.True;
    }
}

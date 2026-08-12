using OokiGrader.Domain.Grading;

namespace OokiGrader.Host.Api;

/// <summary>
/// Shared guardrails for teacher-facing list queries. Normalized values are also
/// used in cursor bindings so visually equivalent search input cannot silently
/// change the result set between pages.
/// </summary>
internal static class ListQuery
{
    public const int MaximumPageSize = 200;
    public const int MaximumSearchLength = 200;
    public const int MaximumFilterLength = 200;
    public const int MaximumIdLength = 128;
    public const int MaximumFacetValues = 200;
    private const int MaximumSearchTokens = 20;

    public static bool TryNormalizeSearch(
        HttpContext context,
        string? value,
        out string? normalized,
        out IReadOnlyList<string> tokens,
        out IResult? error)
    {
        normalized = null;
        tokens = [];
        error = null;
        var trimmed = CursorPagination.TrimToNull(value);
        if (trimmed is null)
        {
            return true;
        }

        if (trimmed.Length > MaximumSearchLength)
        {
            error = Invalid(
                context,
                $"search は {MaximumSearchLength} 文字以内で指定してください。");
            return false;
        }

        normalized = JapaneseTextNormalizer.NormalizeForComparison(trimmed)
            .ToLowerInvariant();
        if (normalized.Length is 0 or > MaximumSearchLength)
        {
            error = Invalid(
                context,
                $"search は {MaximumSearchLength} 文字以内で指定してください。");
            return false;
        }

        var parts = normalized.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > MaximumSearchTokens)
        {
            error = Invalid(
                context,
                $"search の語数は {MaximumSearchTokens} 個以内で指定してください。");
            return false;
        }

        tokens = parts;
        return true;
    }

    public static bool TryTrimFilter(
        HttpContext context,
        string? value,
        string parameterName,
        out string? normalized,
        out IResult? error,
        int maximumLength = MaximumFilterLength)
    {
        normalized = CursorPagination.TrimToNull(value);
        error = null;
        if (normalized is null || normalized.Length <= maximumLength)
        {
            return true;
        }

        error = Invalid(
            context,
            $"{parameterName} は {maximumLength} 文字以内で指定してください。");
        return false;
    }

    public static bool TryPageSize(
        HttpContext context,
        int? pageSize,
        int? limit,
        out int value,
        out IResult? error,
        int defaultValue = 50)
    {
        value = defaultValue;
        error = null;
        if (pageSize is < 1 or > MaximumPageSize)
        {
            error = Invalid(
                context,
                $"pageSize は 1 以上 {MaximumPageSize} 以下で指定してください。");
            return false;
        }

        if (limit is < 1 or > MaximumPageSize)
        {
            error = Invalid(
                context,
                $"limit は 1 以上 {MaximumPageSize} 以下で指定してください。");
            return false;
        }

        value = pageSize ?? limit ?? defaultValue;
        return true;
    }

    public static string ContainsPattern(string value) =>
        "%" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    public static IResult Invalid(HttpContext context, string detail) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status400BadRequest,
            "LIST_QUERY_INVALID",
            "一覧の絞り込み条件を読み取れません",
            detail);
}

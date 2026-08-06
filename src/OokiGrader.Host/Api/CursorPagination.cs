namespace OokiGrader.Host.Api;

internal static class CursorPagination
{
    public static string Bind(params (string Key, string? Value)[] filters) =>
        ProtectedCursorCodec.ComputeFilterBinding(
            filters.Select(filter =>
                new KeyValuePair<string, string?>(
                    filter.Key,
                    filter.Value)));

    public static bool TryRead<TPosition>(
        HttpContext context,
        ProtectedCursorCodec codec,
        string? cursor,
        string route,
        string filterBinding,
        out TPosition position,
        out IResult? error)
        where TPosition : notnull
    {
        position = default!;
        error = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        if (codec.TryDecode(
                cursor,
                route,
                filterBinding,
                out position))
        {
            return true;
        }

        error = Invalid(context);
        return false;
    }

    public static IResult Invalid(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status400BadRequest,
            "CURSOR_INVALID",
            "ページ位置を読み取れません",
            "cursor が無効か、指定した絞り込み条件または並び順と一致しません。");

    public static string? Next<TPosition>(
        ProtectedCursorCodec codec,
        string route,
        string filterBinding,
        bool hasMore,
        TPosition position)
        where TPosition : notnull =>
        hasMore
            ? codec.Encode(route, filterBinding, position)
            : null;

    public static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

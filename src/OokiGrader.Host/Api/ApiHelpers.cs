using System.Security.Claims;

namespace OokiGrader.Host.Api;

internal static class ApiHelpers
{
    public static string StaffId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated staff ID is unavailable.");

    public static void SetRevisionEtag(HttpResponse response, long revision) =>
        response.Headers.ETag = $"\"rev-{revision}\"";

    public static bool TryReadExpectedRevision(
        HttpRequest request,
        long? bodyRevision,
        out long revision)
    {
        revision = 0;
        var ifMatch = request.Headers.IfMatch.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            var value = ifMatch.Trim().Trim('"');
            if (value.StartsWith("rev-", StringComparison.Ordinal)
                && long.TryParse(value.AsSpan(4), out revision)
                && revision > 0)
            {
                return true;
            }

            return false;
        }

        if (bodyRevision > 0)
        {
            revision = bodyRevision.Value;
            return true;
        }

        return false;
    }

    public static IResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyList<object>? errors = null) =>
        Results.Problem(
            statusCode: status,
            type: $"https://ooki-grader.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            title: title,
            detail: detail,
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["correlationId"] = context.TraceIdentifier,
                ["errors"] = errors,
            });
}

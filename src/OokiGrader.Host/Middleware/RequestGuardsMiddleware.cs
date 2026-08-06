namespace OokiGrader.Host.Middleware;

public sealed class RequestGuardsMiddleware(
    RequestDelegate next,
    IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsMutation(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/v1")
            && HasUnsupportedContentType(context.Request))
        {
            await WriteProblem(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE",
                "対応していない形式です。");
            return;
        }

        var configuredOrigin = configuration["Security:AllowedOrigin"];
        if (IsMutation(context.Request.Method)
            && !string.IsNullOrWhiteSpace(configuredOrigin)
            && (context.Request.Headers.Origin.Count != 1
                || !string.Equals(
                    context.Request.Headers.Origin[0],
                    configuredOrigin,
                    StringComparison.Ordinal)))
        {
            await WriteProblem(
                context,
                StatusCodes.Status403Forbidden,
                "ORIGIN_REJECTED",
                "この接続元からの変更は許可されていません。");
            return;
        }

        await next(context);
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);

    private static bool HasUnsupportedContentType(HttpRequest request)
    {
        if (request.ContentLength is null or 0 || request.Path.Value?.EndsWith("/content", StringComparison.Ordinal) == true)
        {
            return false;
        }

        return request.ContentType is null
            || (!request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                && !request.ContentType.StartsWith("application/offset+octet-stream", StringComparison.OrdinalIgnoreCase)
                && !request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteProblem(
        HttpContext context,
        int status,
        string code,
        string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = $"https://ooki-grader.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            title = "リクエストを処理できません",
            status,
            code,
            detail,
            instance = context.Request.Path.Value,
            correlationId = context.TraceIdentifier,
        });
    }
}

using OokiGrader.Host.Common;

namespace OokiGrader.Host.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, IUlidGenerator ids)
    {
        var candidate = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = IsSafe(candidate) ? candidate! : ids.NewId();

        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }

    private static bool IsSafe(string? value) =>
        value is { Length: >= 8 and <= 64 }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.');
}

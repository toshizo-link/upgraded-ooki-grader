namespace OokiGrader.Host.Middleware;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = IsTemplateSourcePreview(context.Request.Path)
                ? "default-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'"
                : "default-src 'self'; script-src 'self'; style-src 'self'; " +
                    "img-src 'self' blob:; font-src 'self'; connect-src 'self'; " +
                    "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.CacheControl = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                ? "no-store"
                : headers.CacheControl;
            return Task.CompletedTask;
        });

        await next(context);
    }

    private static bool IsTemplateSourcePreview(PathString path)
    {
        var segments = path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is
            ["api", "v1", "templates", _, "versions", _, "sources", _, "content"];
    }
}

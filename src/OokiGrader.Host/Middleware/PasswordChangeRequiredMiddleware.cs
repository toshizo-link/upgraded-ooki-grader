using System.Security.Claims;

namespace OokiGrader.Host.Middleware;

public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly HashSet<PathString> AllowedApiPaths =
    [
        new("/api/v1/auth/me"),
        new("/api/v1/auth/csrf"),
        new("/api/v1/auth/change-password"),
        new("/api/v1/auth/logout"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var mustChangePassword =
            context.User.FindFirstValue("must_change_password") == "true";
        var isBlockedApiPath = context.Request.Path.StartsWithSegments(
                "/api/v1",
                StringComparison.OrdinalIgnoreCase)
            && !AllowedApiPaths.Contains(context.Request.Path);
        if (!mustChangePassword || !isBlockedApiPath)
        {
            await next(context);
            return;
        }

        await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                type: "https://ooki-grader.local/problems/password-change-required",
                title: "パスワードの変更が必要です",
                detail: "一時パスワードを新しいパスワードに変更してください。",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "PASSWORD_CHANGE_REQUIRED",
                })
            .ExecuteAsync(context);
    }
}

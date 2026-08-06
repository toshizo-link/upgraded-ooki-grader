using OokiGrader.Host.Security;

namespace OokiGrader.Host.Middleware;

public sealed class CsrfValidationMiddleware(
    RequestDelegate next,
    IConfiguration configuration)
{
    public async Task InvokeAsync(
        HttpContext context,
        IStaffAuthenticationService authentication)
    {
        if (!RequiresValidation(context))
        {
            await next(context);
            return;
        }

        var cookieName = OokiSessionAuthenticationHandler.GetCookieName(configuration);
        var sessionToken = context.Request.Cookies[cookieName];
        var csrfToken = context.Request.Headers["X-CSRF-Token"].FirstOrDefault();

        if (sessionToken is null
            || csrfToken is null
            || !await authentication.ValidateCsrfAsync(
                sessionToken,
                csrfToken,
                context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://ooki-grader.local/problems/csrf-rejected",
                title = "変更を確認できませんでした",
                status = StatusCodes.Status403Forbidden,
                code = "CSRF_REJECTED",
                detail = "画面を再読み込みしてから、もう一度お試しください。",
                instance = context.Request.Path.Value,
                correlationId = context.TraceIdentifier,
            });
            return;
        }

        await next(context);
    }

    private static bool RequiresValidation(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method);
    }
}

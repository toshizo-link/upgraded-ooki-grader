using Microsoft.EntityFrameworkCore;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Middleware;

public sealed class MaintenanceModeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IDbContextFactory<OokiGraderDbContext> databaseFactory)
    {
        if (!IsGuardedMutation(context))
        {
            await next(context);
            return;
        }

        await using var database = await databaseFactory.CreateDbContextAsync(
            context.RequestAborted);
        var maintenanceMode = await database.SiteSettings
            .AsNoTracking()
            .Select(settings => settings.MaintenanceMode)
            .SingleAsync(context.RequestAborted);
        if (!maintenanceMode)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.RetryAfter = "60";
        await context.Response.WriteAsJsonAsync(
            new
            {
                type = "https://ooki-grader.local/problems/maintenance-mode",
                title = "メンテナンス中です",
                status = StatusCodes.Status503ServiceUnavailable,
                code = "MAINTENANCE_MODE",
                detail = "現在は閲覧のみ利用できます。管理者がメンテナンスを終了するまでお待ちください。",
                instance = context.Request.Path.Value,
                correlationId = context.TraceIdentifier,
            },
            context.RequestAborted);
    }

    private static bool IsGuardedMutation(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || !context.Request.Path.StartsWithSegments("/api/v1"))
        {
            return false;
        }

        if (context.Request.Path.StartsWithSegments("/api/v1/auth")
            || context.Request.Path.StartsWithSegments("/api/v1/admin"))
        {
            return false;
        }

        return HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method);
    }
}

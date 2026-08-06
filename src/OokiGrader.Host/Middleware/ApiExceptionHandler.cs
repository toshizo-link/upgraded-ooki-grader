using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace OokiGrader.Host.Middleware;

public sealed partial class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        LogUnhandledRequest(logger, httpContext.TraceIdentifier, exception);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Type = "https://ooki-grader.local/problems/internal-error",
                Title = "処理を完了できませんでした",
                Detail = "しばらく待ってから再試行してください。",
                Status = StatusCodes.Status500InternalServerError,
                Instance = httpContext.Request.Path,
                Extensions =
                {
                    ["code"] = "INTERNAL_ERROR",
                    ["correlationId"] = httpContext.TraceIdentifier,
                },
            },
        });
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Error,
        Message = "Unhandled request failure. CorrelationId={CorrelationId}")]
    private static partial void LogUnhandledRequest(
        ILogger logger,
        string correlationId,
        Exception exception);
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Host.Common;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Api;

internal static class EventsEndpoints
{
    public static IEndpointRouteBuilder MapEventsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/events", StreamAsync)
            .RequireAuthorization("review");
        return endpoints;
    }

    private static async Task StreamAsync(
        HttpContext context,
        IDbContextFactory<OokiGraderDbContext> databaseFactory,
        IUlidGenerator ids,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.WriteAsync("retry: 5000\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);

        ReviewQueueCounts? previous = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var database = await databaseFactory
                .CreateDbContextAsync(cancellationToken);
            var current = await ReadCountsAsync(database, cancellationToken);
            if (previous is null || current != previous)
            {
                await WriteEventAsync(
                    context.Response,
                    ids.NewId(),
                    "review.counts",
                    current,
                    cancellationToken);
                previous = current;
            }
            else
            {
                await context.Response.WriteAsync(
                    $": heartbeat {DateTimeOffset.UtcNow:O}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    private static async Task<ReviewQueueCounts> ReadCountsAsync(
        OokiGraderDbContext database,
        CancellationToken cancellationToken)
    {
        var needsNameReview = await database.Submissions
            .AsNoTracking()
            .CountAsync(
                item => item.State == "needs_name_review",
                cancellationToken);
        var needsGradeReview = await database.QuestionResults
            .AsNoTracking()
            .CountAsync(
                item => item.ReviewRequired
                    && item.ReviewStatus == "pending",
                cancellationToken);
        var readyToFinalize = await database.Submissions
            .AsNoTracking()
            .CountAsync(
                item => item.State == "ready_to_finalize",
                cancellationToken);
        return new ReviewQueueCounts(
            needsNameReview,
            needsGradeReview,
            readyToFinalize);
    }

    private static async Task WriteEventAsync<T>(
        HttpResponse response,
        string id,
        string eventName,
        T value,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync($"id: {id}\n", cancellationToken);
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await response.WriteAsync(
            $"data: {JsonSerializer.Serialize(value)}\n\n",
            cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private sealed record ReviewQueueCounts(
        int NeedsNameReview,
        int NeedsGradeReview,
        int ReadyToFinalize);
}

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using OokiGrader.Host.Services;

namespace OokiGrader.Host.Api;

public static class OrderedScanBatchEndpoints
{
    public static IEndpointRouteBuilder MapOrderedScanBatchEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/test-sessions/{sessionId}/ordered-scan-batches",
                CreateBatch)
            .WithTags("Ordered scan batches")
            .RequireAuthorization("upload");

        var group = endpoints.MapGroup("/api/v1/ordered-scan-batches")
            .WithTags("Ordered scan batches")
            .RequireAuthorization("upload");
        group.MapGet("/{batchId}", GetBatch);
        group.MapPost("/{batchId}:finalize", FinalizeBatch);
        group.MapPost("/{batchId}:cancel", CancelBatch);
        return endpoints;
    }

    private static async Task<IResult> CreateBatch(
        string sessionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateOrderedScanBatchRequest request,
        [FromServices] OrderedScanBatchService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await service.CreateAsync(
                new CreateOrderedScanBatchCommand(
                    sessionId,
                    request.Items ?? [],
                    ApiHelpers.StaffId(principal),
                    context.TraceIdentifier),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, snapshot.RowVersion);
            return Results.Created(
                $"/api/v1/ordered-scan-batches/{snapshot.Id}",
                snapshot);
        }
        catch (OrderedScanBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> GetBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] OrderedScanBatchService service,
        CancellationToken cancellationToken)
    {
        var snapshot = await service.GetAsync(
            batchId,
            ApiHelpers.StaffId(principal),
            IsElevated(principal),
            cancellationToken);
        if (snapshot is null)
        {
            return Results.NotFound();
        }

        ApiHelpers.SetRevisionEtag(context.Response, snapshot.RowVersion);
        return Results.Ok(snapshot);
    }

    private static async Task<IResult> FinalizeBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] OrderedScanMutationRequest request,
        [FromServices] OrderedScanBatchService service,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedRowVersion is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "ROW_VERSION_REQUIRED",
                "更新前の状態が必要です",
                "読取バッチを再読み込みしてから、もう一度操作してください。");
        }

        try
        {
            var snapshot = await service.QueueFinalizeAsync(
                batchId,
                request.ExpectedRowVersion.Value,
                ApiHelpers.StaffId(principal),
                IsElevated(principal),
                context.TraceIdentifier,
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, snapshot.RowVersion);
            return Results.Accepted(
                $"/api/v1/ordered-scan-batches/{snapshot.Id}",
                snapshot);
        }
        catch (OrderedScanBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> CancelBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] OrderedScanMutationRequest request,
        [FromServices] OrderedScanBatchService service,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedRowVersion is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "ROW_VERSION_REQUIRED",
                "更新前の状態が必要です",
                "読取バッチを再読み込みしてから、もう一度操作してください。");
        }

        try
        {
            var snapshot = await service.CancelAsync(
                batchId,
                request.ExpectedRowVersion.Value,
                ApiHelpers.StaffId(principal),
                IsElevated(principal),
                context.TraceIdentifier,
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, snapshot.RowVersion);
            return Results.Ok(snapshot);
        }
        catch (OrderedScanBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static IResult Problem(
        HttpContext context,
        OrderedScanBatchServiceException exception)
    {
        if (exception.CurrentRowVersion is { } revision)
        {
            ApiHelpers.SetRevisionEtag(context.Response, revision);
        }

        return ApiHelpers.Problem(
            context,
            exception.StatusCode,
            exception.Code,
            exception.Title,
            exception.Detail);
    }

    private static bool IsElevated(ClaimsPrincipal principal) =>
        principal.IsInRole("administrator") || principal.IsInRole("teacher");

    public sealed record CreateOrderedScanBatchRequest(
        IReadOnlyList<CreateOrderedScanBatchItem>? Items);

    public sealed record OrderedScanMutationRequest(long? ExpectedRowVersion);
}

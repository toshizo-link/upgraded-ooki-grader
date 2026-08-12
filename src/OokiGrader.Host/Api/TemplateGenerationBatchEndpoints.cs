using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using OokiGrader.Application.Abstractions;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Services;

namespace OokiGrader.Host.Api;

public static class TemplateGenerationBatchEndpoints
{
    public static IEndpointRouteBuilder MapTemplateGenerationBatchEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/template-generation-batches")
            .WithTags("Template generation batches")
            .RequireAuthorization("teacher");

        group.MapPost("/", CreateBatch)
            .RequireIdempotency();
        group.MapPost("/{batchId}/generate", GenerateBatch)
            .RequireIdempotency();
        group.MapPost("/{batchId}/retry", RetryBatch)
            .RequireIdempotency();
        group.MapPost("/{batchId}/cancel", CancelBatch)
            .RequireIdempotency();
        group.MapPatch("/{batchId}/units/{unitId}", UpdateUnit)
            .RequireIdempotency();
        group.MapPatch("/{batchId}/step-sets/{setIndex:int}", UpdateStepSet)
            .RequireIdempotency();
        group.MapPost("/{batchId}/confirm", ConfirmBatch)
            .RequireIdempotency();
        group.MapGet("/resumable", ListResumableBatches);
        group.MapGet("/{batchId}", GetBatch);

        return endpoints;
    }

    private static async Task<IResult> ListResumableBatches(
        ClaimsPrincipal principal,
        [FromQuery] int? limit,
        TemplateGenerationBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListResumableAsync(
            ApiHelpers.StaffId(principal),
            principal.IsInRole("administrator"),
            limit ?? TemplateGenerationBatchService.DefaultResumableBatchLimit,
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateBatch(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateTemplateGenerationBatchRequest request,
        IConfiguration configuration,
        TemplateGenerationBatchService service,
        CancellationToken cancellationToken)
    {
        if (!IsNewGenerationEnabled(configuration))
        {
            return GenerationDisabled(context);
        }

        try
        {
            var batch = await service.CreateAsync(
                new CreateTemplateGenerationBatchCommand(
                    request.SourceId,
                    request.TestType,
                    request.Subject,
                    request.AnswerStyle,
                    request.ExpectedSourceRowVersion,
                    ApiHelpers.StaffId(principal),
                    principal.IsInRole("administrator"),
                    OperationId(context),
                    context.TraceIdentifier),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Created(
                $"/api/v1/template-generation-batches/{batch.Id}",
                batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> GenerateBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] GenerateTemplateGenerationBatchRequest request,
        IConfiguration configuration,
        TemplateGenerationBatchService service,
        CancellationToken cancellationToken)
    {
        if (!IsNewGenerationEnabled(configuration))
        {
            return GenerationDisabled(context);
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.ExpectedRowVersion,
                out var expectedRowVersion))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "ROW_VERSION_REQUIRED",
                "更新前の行バージョンが必要です",
                "If-MatchまたはexpectedRowVersionを指定してください。");
        }

        try
        {
            var batch = await service.GenerateAsync(
                new GenerateTemplateGenerationBatchCommand(
                    batchId,
                    expectedRowVersion,
                    ApiHelpers.StaffId(principal),
                    principal.IsInRole("administrator"),
                    OperationId(context),
                    context.TraceIdentifier),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Accepted(
                $"/api/v1/template-generation-batches/{batch.Id}",
                batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> GetBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        TemplateGenerationBatchService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var batch = await service.GetAsync(
                batchId,
                ApiHelpers.StaffId(principal),
                principal.IsInRole("administrator"),
                cancellationToken);
            if (batch is null)
            {
                return Results.NotFound();
            }

            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Ok(batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> UpdateUnit(
        string batchId,
        string unitId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] UpdateTemplateGenerationUnitRequest request,
        TemplateGenerationFinalizationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var batch = await service.UpdateUnitAsync(
                new UpdateTemplateGenerationUnitCommand(
                    batchId,
                    unitId,
                    request.BaseTestName,
                    request.ResolvedGrade,
                    request.GradeConfirmedByUser,
                    request.TeacherNote,
                    request.ExpectedRowVersion,
                    ApiHelpers.StaffId(principal),
                    principal.IsInRole("administrator"),
                    OperationId(context),
                    context.TraceIdentifier),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Ok(batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> UpdateStepSet(
        string batchId,
        int setIndex,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] UpdateTemplateGenerationStepSetRequest request,
        TemplateGenerationFinalizationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var batch = await service.UpdateStepSetAsync(
                new UpdateTemplateGenerationStepSetCommand(
                    batchId,
                    setIndex,
                    request.BaseTestName,
                    request.ExpectedUnitRowVersions,
                    ApiHelpers.StaffId(principal),
                    principal.IsInRole("administrator"),
                    OperationId(context),
                    context.TraceIdentifier),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Ok(batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> RetryBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] TemplateGenerationBatchMutationRequest request,
        IConfiguration configuration,
        TemplateGenerationFinalizationService service,
        CancellationToken cancellationToken)
    {
        if (!IsNewGenerationEnabled(configuration))
        {
            return GenerationDisabled(context);
        }

        if (!TryReadExpectedRevision(
                context,
                request.ExpectedRowVersion,
                out var expectedRowVersion,
                out var problem))
        {
            return problem!;
        }

        try
        {
            var batch = await service.RetryAsync(
                new RetryTemplateGenerationBatchCommand(
                    batchId,
                    expectedRowVersion,
                    ApiHelpers.StaffId(principal),
                    principal.IsInRole("administrator"),
                    OperationId(context),
                    context.TraceIdentifier),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Accepted(
                $"/api/v1/template-generation-batches/{batch.Id}",
                batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> CancelBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] TemplateGenerationBatchMutationRequest request,
        TemplateGenerationFinalizationService service,
        [FromServices] IWriteCoordinator writeCoordinator,
        CancellationToken cancellationToken)
    {
        if (!TryReadExpectedRevision(
                context,
                request.ExpectedRowVersion,
                out var expectedRowVersion,
                out var problem))
        {
            return problem!;
        }

        try
        {
            var command = new CancelTemplateGenerationBatchCommand(
                    batchId,
                    expectedRowVersion,
                    ApiHelpers.StaffId(principal),
                    principal.IsInRole("administrator"),
                    OperationId(context),
                    context.TraceIdentifier);
            var batch = await writeCoordinator.ExecuteAsync(
                token => service.CancelAsync(command, token),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Ok(batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static async Task<IResult> ConfirmBatch(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] TemplateGenerationBatchMutationRequest request,
        TemplateGenerationFinalizationService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadExpectedRevision(
                context,
                request.ExpectedRowVersion,
                out var expectedRowVersion,
                out var problem))
        {
            return problem!;
        }

        try
        {
            var batch = await service.ConfirmAsync(
                new ConfirmTemplateGenerationBatchCommand(
                    batchId,
                    expectedRowVersion,
                    ApiHelpers.StaffId(principal),
                    principal.IsInRole("administrator"),
                    OperationId(context),
                    context.TraceIdentifier),
                cancellationToken);
            ApiHelpers.SetRevisionEtag(context.Response, batch.RowVersion);
            return Results.Ok(batch);
        }
        catch (TemplateGenerationBatchServiceException exception)
        {
            return Problem(context, exception);
        }
    }

    private static bool TryReadExpectedRevision(
        HttpContext context,
        long? requestRevision,
        out long expectedRowVersion,
        out IResult? problem)
    {
        if (ApiHelpers.TryReadExpectedRevision(
                context.Request,
                requestRevision,
                out expectedRowVersion))
        {
            problem = null;
            return true;
        }

        problem = ApiHelpers.Problem(
            context,
            StatusCodes.Status428PreconditionRequired,
            "ROW_VERSION_REQUIRED",
            "更新前の行バージョンが必要です",
            "If-MatchまたはexpectedRowVersionを指定してください。");
        return false;
    }

    private static IResult Problem(
        HttpContext context,
        TemplateGenerationBatchServiceException exception)
    {
        if (exception.CurrentRowVersion is { } currentRowVersion)
        {
            ApiHelpers.SetRevisionEtag(context.Response, currentRowVersion);
        }

        return ApiHelpers.Problem(
            context,
            exception.StatusCode,
            exception.Code,
            exception.Title,
            exception.Detail);
    }

    private static string OperationId(HttpContext context) =>
        context.Request.Headers["Idempotency-Key"].FirstOrDefault()
        ?? context.TraceIdentifier;

    internal static bool IsNewGenerationEnabled(IConfiguration configuration) =>
        configuration.GetValue("Features:Ai.TemplateGeneration", false);

    private static IResult GenerationDisabled(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "TEMPLATE_GENERATION_DISABLED",
            "テンプレート生成は現在停止しています",
            "管理者がテンプレート生成を再開してから、もう一度お試しください。");
}

public sealed record CreateTemplateGenerationBatchRequest(
    string SourceId,
    TestType TestType,
    string Subject,
    AnswerStyle? AnswerStyle,
    long ExpectedSourceRowVersion);

public sealed record GenerateTemplateGenerationBatchRequest(
    long? ExpectedRowVersion);

public sealed record UpdateTemplateGenerationUnitRequest(
    string? BaseTestName,
    GradeLevel? ResolvedGrade,
    bool? GradeConfirmedByUser,
    string? TeacherNote,
    long ExpectedRowVersion);

public sealed record UpdateTemplateGenerationStepSetRequest(
    string BaseTestName,
    IReadOnlyDictionary<string, long> ExpectedUnitRowVersions);

public sealed record TemplateGenerationBatchMutationRequest(
    long? ExpectedRowVersion);

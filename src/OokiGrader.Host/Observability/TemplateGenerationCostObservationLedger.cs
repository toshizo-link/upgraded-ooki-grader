using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Observability;

internal sealed record TemplateGenerationBatchCostObservation(
    long TotalActualUsdMicros,
    long PreviousActualUsdMicros,
    long DeltaActualUsdMicros,
    string Outcome);

internal sealed record TemplateGenerationUnitCostObservation(
    TestType TestType,
    TemplatePromptSystem PromptSystem,
    int ProfileVersion,
    string? Provider,
    string? Model,
    long ActualUsdMicros,
    string Outcome);

/// <summary>
/// Adds durable audit markers before terminal cost histograms are emitted.
/// Batch samples are deltas from the preceding terminal observation, so a
/// failed run followed by retry and confirmation never reports the first
/// run's spend twice.
/// </summary>
internal static class TemplateGenerationCostObservationLedger
{
    internal const string BatchEventType =
        "TemplateGenerationBatchCostObserved";
    internal const string UnitEventType =
        "TemplateGenerationUnitCostObserved";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    internal static async Task<long> ReadBatchSettledCostAsync(
        OokiGraderDbContext db,
        string batchId,
        CancellationToken cancellationToken)
    {
        var unitIds = await db.TemplateGenerationUnits
            .AsNoTracking()
            .Where(item => item.BatchId == batchId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var reservations = await db.AiBudgetReservations
            .Where(item => item.AiRequest.EntityType == "template_generation_unit"
                && unitIds.Contains(item.AiRequest.EntityId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return reservations
            .Where(item => item.State == "settled")
            .Aggregate(
                0L,
                (total, item) => checked(total + item.ActualUsdMicros));
    }

    internal static async Task<TemplateGenerationBatchCostObservation?>
        PrepareBatchObservationAsync(
            OokiGraderDbContext db,
            string batchId,
            string operationId,
            string outcome,
            string? actorStaffUserId,
            IUlidGenerator ids,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken)
    {
        var normalizedOutcome = outcome switch
        {
            "succeeded" => "succeeded",
            "failed" => "failed",
            "cancelled" => "cancelled",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A terminal template-generation outcome is required."),
        };
        var alreadyObserved = await db.AuditEvents
            .AsNoTracking()
            .AnyAsync(item => item.EventType == BatchEventType
                && item.ObjectType == "template_generation_batch"
                && item.ObjectId == batchId
                && item.CorrelationId == operationId
                && item.ReasonCode == normalizedOutcome, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyObserved)
        {
            return null;
        }

        var previousMetadata = await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.EventType == BatchEventType
                && item.ObjectType == "template_generation_batch"
                && item.ObjectId == batchId)
            .Select(item => item.SafeMetadataJson)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var previousActualUsdMicros = previousMetadata
            .Select(ReadPriorTotal)
            .DefaultIfEmpty()
            .Max();
        var batchRowVersion = await db.TemplateGenerationBatches
            .Where(item => item.Id == batchId)
            .Select(item => item.Revision)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var totalActualUsdMicros = await ReadBatchSettledCostAsync(
                db,
                batchId,
                cancellationToken)
            .ConfigureAwait(false);
        var deltaActualUsdMicros = Math.Max(
            0,
            totalActualUsdMicros - previousActualUsdMicros);
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = ids.NewId(),
            OccurredAt = occurredAt,
            ActorStaffUserId = actorStaffUserId,
            EventType = BatchEventType,
            ObjectType = "template_generation_batch",
            ObjectId = batchId,
            Outcome = "succeeded",
            ReasonCode = normalizedOutcome,
            CorrelationId = operationId,
            SafeMetadataJson = JsonSerializer.Serialize(
                new
                {
                    outcome = normalizedOutcome,
                    totalActualUsdMicros,
                    previousActualUsdMicros,
                    deltaActualUsdMicros,
                    batchRowVersion,
                    profileVersion =
                        TemplateGenerationProfile.CurrentProfileVersion,
                    promptVersion =
                        TemplateGenerationBatchService.ExtractionPromptVersion,
                    schemaVersion =
                        TemplateGenerationBatchService.ExtractionSchemaVersion,
                },
                JsonOptions),
        });
        return new TemplateGenerationBatchCostObservation(
            totalActualUsdMicros,
            previousActualUsdMicros,
            deltaActualUsdMicros,
            normalizedOutcome);
    }

    internal static async Task<TemplateGenerationUnitCostObservation?>
        PrepareCancelledUnitObservationAsync(
            OokiGraderDbContext db,
            TemplateGenerationUnitEntity unit,
            string jobId,
            string? actorStaffUserId,
            IUlidGenerator ids,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken)
    {
        var alreadyObserved = await db.AuditEvents
            .AsNoTracking()
            .AnyAsync(item => item.EventType == UnitEventType
                && item.ObjectType == "template_generation_unit"
                && item.ObjectId == unit.Id
                && item.CorrelationId == jobId
                && item.ReasonCode == "cancelled", cancellationToken)
            .ConfigureAwait(false);
        if (alreadyObserved)
        {
            return null;
        }

        var requestPrefix = $"template_unit_run_{jobId}_";
        var reservations = await db.AiBudgetReservations
            .Where(item => item.AiRequest.EntityType == "template_generation_unit"
                && item.AiRequest.EntityId == unit.Id
                && item.AiRequest.RequestKey.StartsWith(requestPrefix))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var actualUsdMicros = reservations
            .Where(item => item.State == "settled")
            .Aggregate(
                0L,
                (total, item) => checked(total + item.ActualUsdMicros));
        var providerModel = await db.AiRequests
            .AsNoTracking()
            .Where(item => item.EntityType == "template_generation_unit"
                && item.EntityId == unit.Id
                && item.RequestKey.StartsWith(requestPrefix))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new
            {
                item.AiTaskProfile.AiConnection.Provider,
                item.AiTaskProfile.ModelId,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = ids.NewId(),
            OccurredAt = occurredAt,
            ActorStaffUserId = actorStaffUserId,
            EventType = UnitEventType,
            ObjectType = "template_generation_unit",
            ObjectId = unit.Id,
            Outcome = "succeeded",
            ReasonCode = "cancelled",
            CorrelationId = jobId,
            SafeMetadataJson = JsonSerializer.Serialize(
                new
                {
                    outcome = "cancelled",
                    actualUsdMicros,
                    unitRowVersion = unit.Revision,
                    profileVersion =
                        TemplateGenerationProfile.CurrentProfileVersion,
                    promptVersion =
                        TemplateGenerationBatchService.ExtractionPromptVersion,
                    schemaVersion =
                        TemplateGenerationBatchService.ExtractionSchemaVersion,
                },
                JsonOptions),
        });
        return new TemplateGenerationUnitCostObservation(
            unit.TestType,
            unit.PromptSystem,
            TemplateGenerationProfile.CurrentProfileVersion,
            providerModel?.Provider,
            providerModel?.ModelId,
            actualUsdMicros,
            "cancelled");
    }

    private static long ReadPriorTotal(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return 0;
        }

        try
        {
            using var metadata = JsonDocument.Parse(metadataJson);
            return metadata.RootElement.TryGetProperty(
                    "totalActualUsdMicros",
                    out var value)
                && value.TryGetInt64(out var parsed)
                && parsed >= 0
                    ? parsed
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

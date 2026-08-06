using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Auditing;

public sealed class EfAuditSink(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IClock clock) : IAuditSink
{
    public Task<string> AppendAsync(
        AuditWrite auditEvent,
        CancellationToken cancellationToken = default)
    {
        Validate(auditEvent);

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var entity = new AuditEventEntity
            {
                Id = UlidId.New(clock.UtcNow),
                OccurredAt = clock.UtcNow,
                ActorStaffUserId = auditEvent.ActorStaffUserId,
                EventType = auditEvent.EventType,
                ObjectType = auditEvent.ObjectType,
                ObjectId = auditEvent.ObjectId,
                Outcome = auditEvent.Outcome,
                ReasonCode = auditEvent.ReasonCode,
                CorrelationId = auditEvent.CorrelationId,
                SafeMetadataJson = auditEvent.SafeMetadataJson
            };
            dbContext.AuditEvents.Add(entity);
            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            return entity.Id;
        }, cancellationToken);
    }

    private static void Validate(AuditWrite auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (string.IsNullOrWhiteSpace(auditEvent.EventType) ||
            string.IsNullOrWhiteSpace(auditEvent.ObjectType) ||
            string.IsNullOrWhiteSpace(auditEvent.ObjectId) ||
            string.IsNullOrWhiteSpace(auditEvent.Outcome))
        {
            throw new ArgumentException(
                "Audit type, object, object ID, and outcome are required.",
                nameof(auditEvent));
        }
    }
}

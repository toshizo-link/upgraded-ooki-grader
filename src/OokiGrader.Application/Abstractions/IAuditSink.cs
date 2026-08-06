namespace OokiGrader.Application.Abstractions;

public sealed record AuditWrite(
    string EventType,
    string ObjectType,
    string ObjectId,
    string Outcome,
    string? ActorStaffUserId = null,
    string? CorrelationId = null,
    string? ReasonCode = null,
    string? SafeMetadataJson = null);

public interface IAuditSink
{
    Task<string> AppendAsync(
        AuditWrite auditEvent,
        CancellationToken cancellationToken = default);
}

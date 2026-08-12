namespace OokiGrader.Infrastructure.Persistence.Entities;

public interface IRevisionedEntity
{
    long Revision { get; set; }
}

public interface IUpdatedEntity
{
    DateTimeOffset UpdatedAt { get; set; }
}

public interface IAppendOnlyEntity
{
}

/// <summary>
/// Immutable lineage whose file-reference pointer may be cleared exactly once
/// when the referenced raw scan reaches its retention deadline.
/// </summary>
public interface IRetentionMutableLineageEntity
{
    string? FileReferenceId { get; set; }
}

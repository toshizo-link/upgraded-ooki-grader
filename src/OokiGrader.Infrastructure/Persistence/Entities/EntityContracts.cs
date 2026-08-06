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

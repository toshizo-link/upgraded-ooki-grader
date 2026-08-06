namespace OokiGrader.Infrastructure.DependencyInjection;

public sealed class OokiPersistenceOptions
{
    public required string DatabasePath { get; init; }
    public required string ContentRootPath { get; init; }
}

using Microsoft.AspNetCore.Builder;

namespace OokiGrader.Host.Middleware;

public sealed class RequireIdempotencyMetadata
{
    private RequireIdempotencyMetadata()
    {
    }

    public static RequireIdempotencyMetadata Instance { get; } = new();
}

public sealed class AllowNonIdempotentMutationMetadata
{
    private AllowNonIdempotentMutationMetadata()
    {
    }

    public static AllowNonIdempotentMutationMetadata Instance { get; } = new();
}

public static class IdempotencyEndpointConventionExtensions
{
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpointBuilder =>
            endpointBuilder.Metadata.Add(RequireIdempotencyMetadata.Instance));
        return builder;
    }

    public static TBuilder AllowNonIdempotentMutation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpointBuilder =>
            endpointBuilder.Metadata.Add(
                AllowNonIdempotentMutationMetadata.Instance));
        return builder;
    }
}

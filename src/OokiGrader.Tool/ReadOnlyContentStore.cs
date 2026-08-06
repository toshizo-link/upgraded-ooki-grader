using OokiGrader.Application.Abstractions;

namespace OokiGrader.Tool;

internal sealed class ReadOnlyContentStore : IContentStore
{
    public Task<ContentWriteResult> PutAsync(
        Stream source,
        ContentStorageClass storageClass,
        string verifiedExtension,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The diagnostics tool does not support content mutations.");

    public Task<Stream> OpenReadAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Archive verification reads the self-contained backup set.");

    public Task<bool> ExistsAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Archive verification reads the self-contained backup set.");

    public Task DeleteAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The diagnostics tool does not support content mutations.");
}

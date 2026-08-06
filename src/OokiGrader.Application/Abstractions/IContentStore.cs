namespace OokiGrader.Application.Abstractions;

public enum ContentStorageClass
{
    ManagedScanOriginal,
    ManagedScanDerived,
    TemplateSource,
    TemplateDerived,
    ResultReport,
    AiDiagnostic,
    Temporary
}

public sealed record ContentObjectLocator(
    ContentStorageClass StorageClass,
    string Sha256,
    long Bytes,
    string Extension);

public sealed record ContentWriteResult(
    ContentObjectLocator Locator,
    string RelativePath,
    bool Deduplicated);

public interface IContentStore
{
    Task<ContentWriteResult> PutAsync(
        Stream source,
        ContentStorageClass storageClass,
        string verifiedExtension,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ContentObjectLocator locator,
        CancellationToken cancellationToken = default);
}

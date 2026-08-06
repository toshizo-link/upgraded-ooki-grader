using System.Text;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Storage;

namespace OokiGrader.Infrastructure.Tests;

public sealed class ContentStoreTests
{
    [Fact]
    public async Task PutIsContentAddressedAtomicAndDeduplicated()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new NtfsContentStore(new ContentStoreOptions { RootPath = root });
            var bytes = Encoding.UTF8.GetBytes("答案の内容");

            var first = await store.PutAsync(
                new MemoryStream(bytes),
                ContentStorageClass.ManagedScanOriginal,
                "pdf");
            var duplicate = await store.PutAsync(
                new MemoryStream(bytes),
                ContentStorageClass.ManagedScanOriginal,
                ".PDF");

            Assert.False(first.Deduplicated);
            Assert.True(duplicate.Deduplicated);
            Assert.Equal(first.Locator, duplicate.Locator);
            Assert.Equal(first.RelativePath, duplicate.RelativePath);
            Assert.DoesNotContain("答案", first.RelativePath, StringComparison.Ordinal);

            await using var opened = await store.OpenReadAsync(first.Locator);
            using var copy = new MemoryStream();
            await opened.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());

            var storedFiles = Directory
                .EnumerateFiles(Path.Combine(root, "scan"), "*", SearchOption.AllDirectories)
                .ToArray();
            Assert.Single(storedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocatorRejectsTraversalAndUntrustedExtensions()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new NtfsContentStore(new ContentStoreOptions { RootPath = root });
            var invalidHash = new ContentObjectLocator(
                ContentStorageClass.TemplateSource,
                "../../outside",
                1,
                "pdf");
            var invalidExtension = new ContentObjectLocator(
                ContentStorageClass.TemplateSource,
                new string('a', 64),
                1,
                "../pdf");

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.OpenReadAsync(invalidHash));
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.OpenReadAsync(invalidExtension));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ooki-content-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

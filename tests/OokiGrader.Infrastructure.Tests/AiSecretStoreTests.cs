using System.Security.Cryptography;
using System.Text;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Security;

namespace OokiGrader.Infrastructure.Tests;

public sealed class AiSecretStoreTests
{
    [Fact]
    public async Task InMemoryStoreUsesOpaqueRevisionedReferencesAndDeletesSecrets()
    {
        using var store = new InMemoryAiSecretStore();
        var ownerId = UlidId.New(DateTimeOffset.UtcNow);

        var first = await store.WriteAsync(ownerId, 1, "first-secret".AsMemory());
        var second = await store.WriteAsync(ownerId, 2, "second-secret".AsMemory());

        Assert.Equal($"memory-v1/{ownerId}/00000000000000000001.secret", first.Value);
        Assert.DoesNotContain("first-secret", first.Value, StringComparison.Ordinal);
        using (var lease = await store.ReadAsync(first))
        {
            Assert.Equal(
                "first-secret",
                Encoding.UTF8.GetString(lease.Utf8Bytes.Span));
        }

        using (var lease = await store.ReadAsync(second))
        {
            Assert.Equal(
                "second-secret",
                Encoding.UTF8.GetString(lease.Utf8Bytes.Span));
        }

        Assert.True(await store.DeleteAsync(first));
        Assert.False(await store.DeleteAsync(first));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => store.ReadAsync(first));
    }

    [Fact]
    public async Task SecretLeaseCannotBeReadAfterDisposal()
    {
        using var store = new InMemoryAiSecretStore();
        var reference = await store.WriteAsync(
            UlidId.New(DateTimeOffset.UtcNow),
            1,
            "sensitive".AsMemory());
        var lease = await store.ReadAsync(reference);

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.Utf8Bytes);
    }

    [Fact]
    public async Task FileStorePersistsOnlyProtectedEnvelopeAndRejectsTampering()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new WindowsDpapiAiSecretStore(
                new WindowsDpapiAiSecretStoreOptions { RootPath = root },
                new TestSecretProtector());
            var ownerId = UlidId.New(DateTimeOffset.UtcNow);
            var reference = await store.WriteAsync(
                ownerId,
                7,
                "not-plaintext".AsMemory());
            var path = Path.Combine(
                root,
                ownerId,
                "00000000000000000007.secret");
            var envelope = await File.ReadAllBytesAsync(path);

            Assert.DoesNotContain(
                "not-plaintext",
                Encoding.UTF8.GetString(envelope),
                StringComparison.Ordinal);
            using (var lease = await store.ReadAsync(reference))
            {
                Assert.Equal(
                    "not-plaintext",
                    Encoding.UTF8.GetString(lease.Utf8Bytes.Span));
            }

            envelope[0] ^= 0xff;
            await File.WriteAllBytesAsync(path, envelope);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(reference));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("../secret")]
    [InlineData("memory-v1/not-a-ulid/00000000000000000001.secret")]
    [InlineData("memory-v1/01J00000000000000000000000/1.secret")]
    public async Task StoreRejectsForgedReferences(string value)
    {
        using var store = new InMemoryAiSecretStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ReadAsync(new(value)));
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ooki-ai-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestSecretProtector : IAiSecretProtector
    {
        private const int TagLength = 16;

        public byte[] Protect(
            ReadOnlySpan<byte> plaintext,
            ReadOnlySpan<byte> entropy)
        {
            var key = SHA256.HashData(entropy);
            try
            {
                var output = new byte[TagLength + plaintext.Length];
                key.AsSpan(0, TagLength).CopyTo(output);
                for (var index = 0; index < plaintext.Length; index++)
                {
                    output[TagLength + index] =
                        (byte)(plaintext[index] ^ key[index % key.Length]);
                }

                return output;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        public byte[] Unprotect(
            ReadOnlySpan<byte> protectedBytes,
            ReadOnlySpan<byte> entropy)
        {
            var key = SHA256.HashData(entropy);
            try
            {
                if (protectedBytes.Length <= TagLength
                    || !CryptographicOperations.FixedTimeEquals(
                        protectedBytes[..TagLength],
                        key.AsSpan(0, TagLength)))
                {
                    throw new CryptographicException("Test envelope mismatch.");
                }

                var plaintext = new byte[protectedBytes.Length - TagLength];
                for (var index = 0; index < plaintext.Length; index++)
                {
                    plaintext[index] = (byte)(
                        protectedBytes[TagLength + index]
                        ^ key[index % key.Length]);
                }

                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using OokiGrader.Application.Abstractions;
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
    public async Task DataProtectionFileStorePersistsAcrossInstancesWithoutPlaintext()
    {
        var root = CreateTemporaryRoot();
        var keyRingRoot = Path.Combine(root, "key-ring");
        var secretRoot = Path.Combine(root, "secrets");
        Directory.CreateDirectory(keyRingRoot);
        try
        {
            var ownerId = UlidId.New(DateTimeOffset.UtcNow);
            AiSecretReference reference;
            var firstProvider = CreateDataProtectionProvider(keyRingRoot);
            try
            {
                var firstStore = CreateDataProtectionStore(
                    secretRoot,
                    firstProvider);
                reference = await firstStore.WriteAsync(
                    ownerId,
                    3,
                    "persisted-development-secret".AsMemory());
            }
            finally
            {
                (firstProvider as IDisposable)?.Dispose();
            }

            Assert.Equal(
                $"devfile-v1/{ownerId}/00000000000000000003.secret",
                reference.Value);
            var envelopePath = Path.Combine(
                secretRoot,
                ownerId,
                "00000000000000000003.secret");
            var envelope = await File.ReadAllBytesAsync(envelopePath);
            Assert.DoesNotContain(
                "persisted-development-secret",
                Encoding.UTF8.GetString(envelope),
                StringComparison.Ordinal);

            var secondProvider = CreateDataProtectionProvider(keyRingRoot);
            try
            {
                var secondStore = CreateDataProtectionStore(
                    secretRoot,
                    secondProvider);
                using var lease = await secondStore.ReadAsync(reference);
                Assert.Equal(
                    "persisted-development-secret",
                    Encoding.UTF8.GetString(lease.Utf8Bytes.Span));
            }
            finally
            {
                (secondProvider as IDisposable)?.Dispose();
            }

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(secretRoot));
                Assert.Equal(
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.Combine(secretRoot, ownerId)));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(envelopePath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DataProtectionFileStoreRejectsTamperingAndEnvelopeSwaps()
    {
        var root = CreateTemporaryRoot();
        var keyRingRoot = Path.Combine(root, "key-ring");
        var secretRoot = Path.Combine(root, "secrets");
        Directory.CreateDirectory(keyRingRoot);
        var provider = CreateDataProtectionProvider(keyRingRoot);
        try
        {
            var store = CreateDataProtectionStore(secretRoot, provider);
            var firstOwner = UlidId.New(DateTimeOffset.UtcNow);
            var secondOwner = UlidId.New(DateTimeOffset.UtcNow.AddSeconds(1));
            var first = await store.WriteAsync(
                firstOwner,
                1,
                "first-protected-secret".AsMemory());
            var second = await store.WriteAsync(
                secondOwner,
                1,
                "second-protected-secret".AsMemory());
            var firstPath = Path.Combine(
                secretRoot,
                firstOwner,
                "00000000000000000001.secret");
            var secondPath = Path.Combine(
                secretRoot,
                secondOwner,
                "00000000000000000001.secret");

            var firstEnvelope = await File.ReadAllBytesAsync(firstPath);
            await File.WriteAllBytesAsync(secondPath, firstEnvelope);
            await Assert.ThrowsAsync<CryptographicException>(
                () => store.ReadAsync(second));

            firstEnvelope[0] ^= 0xff;
            await File.WriteAllBytesAsync(firstPath, firstEnvelope);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(first));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DataProtectionFileStoreWritesAtomicallyUnderConcurrency()
    {
        var root = CreateTemporaryRoot();
        var keyRingRoot = Path.Combine(root, "key-ring");
        var secretRoot = Path.Combine(root, "secrets");
        Directory.CreateDirectory(keyRingRoot);
        var provider = CreateDataProtectionProvider(keyRingRoot);
        try
        {
            var store = CreateDataProtectionStore(secretRoot, provider);
            var ownerId = UlidId.New(DateTimeOffset.UtcNow);
            var values = Enumerable.Range(1, 12)
                .Select(index => $"concurrent-secret-{index:D2}")
                .ToArray();

            var references = await Task.WhenAll(values.Select(value =>
                store.WriteAsync(ownerId, 8, value.AsMemory())));

            Assert.Single(references.Select(reference => reference.Value).Distinct());
            using var lease = await store.ReadAsync(references[0]);
            Assert.Contains(
                Encoding.UTF8.GetString(lease.Utf8Bytes.Span),
                values);
            var ownerFiles = Directory.GetFiles(Path.Combine(secretRoot, ownerId));
            Assert.Single(ownerFiles);
            Assert.EndsWith(
                "00000000000000000008.secret",
                ownerFiles[0],
                StringComparison.Ordinal);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DataProtectionFileStoreRejectsSymlinkedOwnerDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryRoot();
        var keyRingRoot = Path.Combine(root, "key-ring");
        var secretRoot = Path.Combine(root, "secrets");
        var outsideRoot = Path.Combine(root, "outside");
        Directory.CreateDirectory(keyRingRoot);
        Directory.CreateDirectory(secretRoot);
        Directory.CreateDirectory(outsideRoot);
        var provider = CreateDataProtectionProvider(keyRingRoot);
        try
        {
            var store = CreateDataProtectionStore(secretRoot, provider);
            var ownerId = UlidId.New(DateTimeOffset.UtcNow);
            Directory.CreateSymbolicLink(
                Path.Combine(secretRoot, ownerId),
                outsideRoot);

            await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(
                ownerId,
                1,
                "must-not-follow-link".AsMemory()));
            Assert.Empty(Directory.GetFiles(outsideRoot));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DataProtectionFileStoreTreatsLegacyMemoryReferenceAsMissing()
    {
        var root = CreateTemporaryRoot();
        var keyRingRoot = Path.Combine(root, "key-ring");
        Directory.CreateDirectory(keyRingRoot);
        var provider = CreateDataProtectionProvider(keyRingRoot);
        try
        {
            var store = CreateDataProtectionStore(
                Path.Combine(root, "secrets"),
                provider);
            var ownerId = UlidId.New(DateTimeOffset.UtcNow);
            var legacy = new AiSecretReference(
                $"memory-v1/{ownerId}/00000000000000000017.secret");

            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => store.ReadAsync(legacy));
            Assert.False(await store.DeleteAsync(legacy));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DataProtectionFileStoreTreatsAbsentEnvelopeAsMissing()
    {
        var root = CreateTemporaryRoot();
        var keyRingRoot = Path.Combine(root, "key-ring");
        Directory.CreateDirectory(keyRingRoot);
        var provider = CreateDataProtectionProvider(keyRingRoot);
        try
        {
            var store = CreateDataProtectionStore(
                Path.Combine(root, "secrets"),
                provider);
            var ownerId = UlidId.New(DateTimeOffset.UtcNow);
            var missing = new AiSecretReference(
                $"devfile-v1/{ownerId}/00000000000000000001.secret");

            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => store.ReadAsync(missing));
            Assert.False(await store.DeleteAsync(missing));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            Directory.Delete(root, recursive: true);
        }
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

    [Theory]
    [InlineData("")]
    [InlineData("../secret")]
    [InlineData("devfile-v1/not-a-ulid/00000000000000000001.secret")]
    [InlineData("devfile-v1/01J00000000000000000000000/1.secret")]
    public async Task DataProtectionFileStoreRejectsForgedReferences(string value)
    {
        var root = CreateTemporaryRoot();
        var keyRingRoot = Path.Combine(root, "key-ring");
        Directory.CreateDirectory(keyRingRoot);
        var provider = CreateDataProtectionProvider(keyRingRoot);
        try
        {
            var store = CreateDataProtectionStore(
                Path.Combine(root, "secrets"),
                provider);
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.ReadAsync(new(value)));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ooki-ai-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static IDataProtectionProvider CreateDataProtectionProvider(
        string keyRingRoot) =>
        DataProtectionProvider.Create(
            new DirectoryInfo(keyRingRoot),
            configuration => configuration.SetApplicationName(
                "OokiGrader.Infrastructure.Tests"));

    private static DataProtectionFileAiSecretStore CreateDataProtectionStore(
        string secretRoot,
        IDataProtectionProvider provider) =>
        new(
            new DataProtectionFileAiSecretStoreOptions
            {
                RootPath = secretRoot,
            },
            provider);

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

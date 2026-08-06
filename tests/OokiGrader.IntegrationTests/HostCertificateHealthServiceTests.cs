using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OokiGrader.Host.Common;

namespace OokiGrader.IntegrationTests;

public sealed class HostCertificateHealthServiceTests
{
    [Fact]
    public void MissingCertificateIsAllowedOnlyOutsideProduction()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ooki-certificate-health",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().Build();
            var clock = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero));

            var testing = new HostCertificateHealthService(
                configuration,
                new TestHostEnvironment(root, Environments.Development),
                clock).Read();
            var production = new HostCertificateHealthService(
                configuration,
                new TestHostEnvironment(root, Environments.Production),
                clock).Read();

            Assert.Equal("unknown", testing.State);
            Assert.Equal("unavailable", production.State);
            Assert.Equal("certificate_not_configured", production.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsUsablePfxAndWarnsSixtyDaysBeforeExpiry()
    {
        // Creating an ephemeral self-signed certificate uses the login keychain
        // on macOS, which is unavailable in the sandboxed CI runner. Production
        // support is Windows, where this test exercises the intended code path.
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "ooki-certificate-health",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(
                2026,
                7,
                27,
                9,
                0,
                0,
                TimeSpan.Zero);
            const string password = "bounded-test-password";
            var certificatePath = Path.Combine(root, "host.pfx");
            using (var rsa = RSA.Create(2_048))
            {
                var request = new CertificateRequest(
                    "CN=ooki-grader.local",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(
                        certificateAuthority: false,
                        hasPathLengthConstraint: false,
                        pathLengthConstraint: 0,
                        critical: true));
                using var certificate = request.CreateSelfSigned(
                    now.AddDays(-1),
                    now.AddDays(30));
                File.WriteAllBytes(
                    certificatePath,
                    certificate.Export(X509ContentType.Pfx, password));
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kestrel:Certificates:Default:Path"] = "host.pfx",
                    ["Kestrel:Certificates:Default:Password"] = password,
                })
                .Build();
            var result = new HostCertificateHealthService(
                configuration,
                new TestHostEnvironment(root, Environments.Production),
                new FixedTimeProvider(now)).Read();

            Assert.Equal("degraded", result.State);
            Assert.Equal(
                "certificate_expires_within_60_days",
                result.ErrorCode);
            Assert.True(result.HasPrivateKey);
            Assert.NotNull(result.ExpiresAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestHostEnvironment(
        string contentRootPath,
        string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "OokiGrader.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}

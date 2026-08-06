using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OokiGrader.Host.Common;

public sealed record CertificateHealthSnapshot(
    string State,
    string? ErrorCode,
    string? Detail,
    DateTimeOffset CheckedAt,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt,
    bool HasPrivateKey);

public sealed class HostCertificateHealthService(
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(60);

    public CertificateHealthSnapshot Read()
    {
        var checkedAt = timeProvider.GetUtcNow();
        var configuredPath =
            configuration["Kestrel:Certificates:Default:Path"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return environment.IsDevelopment()
                || environment.IsEnvironment("Testing")
                ? Snapshot(
                    "unknown",
                    "certificate_not_required_in_current_environment",
                    "開発環境ではホスト証明書を省略できます。",
                    checkedAt)
                : Snapshot(
                    "unavailable",
                    "certificate_not_configured",
                    "HTTPS ホスト証明書を構成してください。",
                    checkedAt);
        }

        var certificatePath = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
        if (!File.Exists(certificatePath))
        {
            return Snapshot(
                "unavailable",
                "certificate_file_missing",
                "構成された HTTPS 証明書ファイルが見つかりません。",
                checkedAt);
        }

        try
        {
            using var certificate = LoadCertificate(certificatePath);
            var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
            var expiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            if (!certificate.HasPrivateKey)
            {
                return Snapshot(
                    "unavailable",
                    "certificate_private_key_missing",
                    "HTTPS 証明書の秘密鍵を使用できません。",
                    checkedAt,
                    notBefore,
                    expiresAt);
            }

            if (notBefore > checkedAt.AddMinutes(5))
            {
                return Snapshot(
                    "unavailable",
                    "certificate_not_yet_valid",
                    "HTTPS 証明書の有効期間がまだ始まっていません。",
                    checkedAt,
                    notBefore,
                    expiresAt,
                    hasPrivateKey: true);
            }

            if (expiresAt <= checkedAt)
            {
                return Snapshot(
                    "unavailable",
                    "certificate_expired",
                    "HTTPS 証明書の有効期限が切れています。",
                    checkedAt,
                    notBefore,
                    expiresAt,
                    hasPrivateKey: true);
            }

            if (expiresAt - checkedAt <= ExpiryWarning)
            {
                return Snapshot(
                    "degraded",
                    "certificate_expires_within_60_days",
                    "HTTPS 証明書を60日以内に更新してください。",
                    checkedAt,
                    notBefore,
                    expiresAt,
                    hasPrivateKey: true);
            }

            return Snapshot(
                "healthy",
                errorCode: null,
                detail: null,
                checkedAt,
                notBefore,
                expiresAt,
                hasPrivateKey: true);
        }
        catch (Exception exception) when (
            exception is CryptographicException
                or IOException
                or UnauthorizedAccessException)
        {
            return Snapshot(
                "unavailable",
                "certificate_load_failed",
                "HTTPS 証明書または秘密鍵を読み込めません。",
                checkedAt);
        }
    }

    private X509Certificate2 LoadCertificate(string certificatePath)
    {
        var extension = Path.GetExtension(certificatePath);
        if (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                configuration["Kestrel:Certificates:Default:Password"],
                X509KeyStorageFlags.EphemeralKeySet);
        }

        return X509CertificateLoader.LoadCertificateFromFile(certificatePath);
    }

    private static CertificateHealthSnapshot Snapshot(
        string state,
        string? errorCode,
        string? detail,
        DateTimeOffset checkedAt,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null,
        bool hasPrivateKey = false) =>
        new(
            state,
            errorCode,
            detail,
            checkedAt,
            notBefore,
            expiresAt,
            hasPrivateKey);
}

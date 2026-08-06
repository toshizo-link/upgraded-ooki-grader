using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Contracts;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Security;

public sealed record BootstrapCompletionResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IBootstrapService
{
    Task<BootstrapStatusResponse> GetStatusAsync(
        bool isHostLocal,
        CancellationToken cancellationToken = default);

    Task EnsureTokenAsync(CancellationToken cancellationToken = default);

    Task<BootstrapCompletionResult> CompleteAsync(
        CompleteBootstrapRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class BootstrapService(
    OokiGraderDbContext db,
    ISessionTokenService tokens,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider,
    IConfiguration configuration,
    IHostEnvironment environment) : IBootstrapService
{
    private const string BootstrapTokenFileName = "bootstrap-token.txt";

    public async Task<BootstrapStatusResponse> GetStatusAsync(
        bool isHostLocal,
        CancellationToken cancellationToken = default)
    {
        var settings = await db.SiteSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == "site", cancellationToken);

        return new BootstrapStatusResponse(
            settings?.BootstrapCompletedAt is not null,
            isHostLocal,
            isHostLocal ? settings?.BootstrapTokenExpiresAt : null);
    }

    public async Task EnsureTokenAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var settings = await db.SiteSettings.SingleAsync(
            item => item.Id == "site",
            cancellationToken);
        if (settings.BootstrapCompletedAt is not null)
        {
            DeleteTokenFile();
            return;
        }

        var tokenPath = GetTokenPath();
        if (settings.BootstrapTokenHash is not null
            && settings.BootstrapTokenExpiresAt > now
            && File.Exists(tokenPath))
        {
            return;
        }

        var configuredToken = configuration["Security:BootstrapToken"];
        var token = string.IsNullOrWhiteSpace(configuredToken)
            ? tokens.Create().SessionToken
            : configuredToken.Trim();
        var expiryHours = Math.Clamp(
            configuration.GetValue("Security:BootstrapTokenHours", 24),
            1,
            24);

        settings.BootstrapTokenHash = tokens.Hash(token);
        settings.BootstrapTokenExpiresAt = now.AddHours(expiryHours);
        await db.SaveChangesAsync(cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        await File.WriteAllTextAsync(
            tokenPath,
            token + Environment.NewLine,
            cancellationToken);
    }

    public async Task<BootstrapCompletionResult> CompleteAsync(
        CompleteBootstrapRequest request,
        CancellationToken cancellationToken = default)
    {
        var passwordErrors = PasswordPolicy.Validate(request.Password);
        if (passwordErrors.Count > 0)
        {
            return new BootstrapCompletionResult(
                false,
                "PASSWORD_POLICY",
                string.Join(' ', passwordErrors));
        }

        var normalizedUsername = StaffAuthenticationService.NormalizeUsername(request.Username);
        if (normalizedUsername.Length is < 1 or > 200
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.SchoolName))
        {
            return new BootstrapCompletionResult(
                false,
                "BOOTSTRAP_INPUT_INVALID",
                "学校名、表示名、ユーザー名を入力してください。");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await db.SiteSettings.SingleAsync(
            item => item.Id == "site",
            cancellationToken);

        if (settings.BootstrapCompletedAt is not null)
        {
            return new BootstrapCompletionResult(
                false,
                "BOOTSTRAP_COMPLETED",
                "初期設定はすでに完了しています。");
        }

        if (settings.BootstrapTokenHash is null
            || settings.BootstrapTokenExpiresAt <= now
            || !tokens.Verify(request.Token, settings.BootstrapTokenHash))
        {
            return new BootstrapCompletionResult(
                false,
                "BOOTSTRAP_TOKEN_INVALID",
                "初期設定トークンが無効か、有効期限が切れています。");
        }

        if (await db.StaffUsers.AnyAsync(
            user => user.UsernameNormalized == normalizedUsername,
            cancellationToken))
        {
            return new BootstrapCompletionResult(
                false,
                "USERNAME_ALREADY_EXISTS",
                "このユーザー名は使用できません。");
        }

        var staffId = UlidId.New(now);
        var user = new StaffUserEntity
        {
            Id = staffId,
            Username = request.Username.Trim(),
            UsernameNormalized = normalizedUsername,
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = await passwordHasher.HashAsync(
                request.Password,
                cancellationToken),
            PasswordAlgorithm = "argon2id",
            PasswordAlgorithmVersion = 1,
            Status = "active",
            CredentialChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.StaffUsers.Add(user);
        db.StaffUserRoles.Add(new StaffUserRoleEntity
        {
            StaffUserId = staffId,
            RoleName = "administrator",
            GrantedByStaffUserId = staffId,
            GrantedAt = now,
        });
        settings.SchoolName = request.SchoolName.Trim();
        settings.BootstrapCompletedAt = now;
        settings.BootstrapTokenHash = null;
        settings.BootstrapTokenExpiresAt = null;
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(1)),
            OccurredAt = now,
            ActorStaffUserId = staffId,
            EventType = "bootstrap.completed",
            ObjectType = "site_settings",
            ObjectId = "site",
            Outcome = "succeeded",
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        DeleteTokenFile();
        return new BootstrapCompletionResult(true);
    }

    private string GetTokenPath()
    {
        var configuredRoot = configuration["Data:Root"] ?? ".data";
        var root = Path.IsPathFullyQualified(configuredRoot)
            ? configuredRoot
            : Path.GetFullPath(configuredRoot, environment.ContentRootPath);
        return Path.Combine(root, BootstrapTokenFileName);
    }

    private void DeleteTokenFile()
    {
        var tokenPath = GetTokenPath();
        if (File.Exists(tokenPath))
        {
            File.Delete(tokenPath);
        }
    }
}

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static class SchoolManagerAdminEndpoints
{
    private const string SecretOwnerId = "00000000000000000000000001";

    public static IEndpointRouteBuilder MapSchoolManagerAdminEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/school-manager")
            .WithTags("School Manager administration")
            .RequireAuthorization("administrator");
        group.MapGet("/", GetSettingsAsync);
        group.MapPut("/", SaveSettingsAsync);
        group.MapGet("/deliveries", ListDeliveriesAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSettingsAsync(
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = await db.SchoolManagerSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        var counts = await db.GuardianDeliveries
            .AsNoTracking()
            .GroupBy(item => item.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        return Results.Ok(new
        {
            configured = settings?.PasswordSecretReference is not null,
            enabled = settings?.Enabled ?? false,
            dryRun = settings?.DryRun ?? true,
            baseUrl = settings?.BaseUrl ?? "https://fsm.flens.jp/",
            username = settings?.Username ?? string.Empty,
            settings?.ActivationStartedAt,
            settings?.LastCredentialTestedAt,
            settings?.LastErrorCode,
            settings?.SafeErrorDetail,
            settingsRevision = settings?.Revision ?? 0,
            deliveries = counts.ToDictionary(item => item.State, item => item.Count),
        });
    }

    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] SaveSchoolManagerSettingsRequest request,
        OokiGraderDbContext db,
        IAiSecretStore secretStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(request, out var baseUri, out var error))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "SCHOOL_MANAGER_SETTINGS_INVALID",
                "School Manager の設定を確認してください",
                error!);
        }

        var settings = await db.SchoolManagerSettings
            .SingleOrDefaultAsync(cancellationToken);
        if (request.Enabled
            && string.IsNullOrEmpty(request.Password)
            && settings?.PasswordSecretReference is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "SCHOOL_MANAGER_CREDENTIAL_MISSING",
                "School Manager の認証情報が必要です",
                "有効化する前にパスワードを登録してください。");
        }

        var now = timeProvider.GetUtcNow();
        var wasEnabled = settings?.Enabled ?? false;
        var previousReference = settings?.PasswordSecretReference;
        AiSecretReference? newReference = null;
        if (!string.IsNullOrEmpty(request.Password))
        {
            var nextRevision = checked((settings?.CredentialRevision ?? 0) + 1);
            newReference = await secretStore.WriteAsync(
                SecretOwnerId,
                nextRevision,
                request.Password.AsMemory(),
                cancellationToken);
            settings ??= new SchoolManagerSettingsEntity
            {
                Id = "school-manager",
                CreatedAt = now,
            };
            settings.CredentialRevision = nextRevision;
            settings.PasswordSecretReference = newReference.Value;
        }

        settings ??= new SchoolManagerSettingsEntity
        {
            Id = "school-manager",
            CreatedAt = now,
        };
        settings.BaseUrl = baseUri!.AbsoluteUri;
        settings.Username = request.Username.Trim();
        settings.Enabled = request.Enabled;
        settings.DryRun = request.DryRun ?? true;
        if (request.Enabled && !wasEnabled)
        {
            settings.ActivationStartedAt = now;
        }
        else if (!request.Enabled)
        {
            settings.ActivationStartedAt = null;
            var unsent = await db.GuardianDeliveries
                .Where(item => item.State == "pending"
                    || item.State == "ready"
                    || item.State == "failed")
                .ToArrayAsync(cancellationToken);
            foreach (var delivery in unsent)
            {
                delivery.State = "canceled";
                delivery.LastErrorCode = "automation_disabled";
                delivery.SafeErrorDetail =
                    "The unsent delivery was canceled when automation was disabled.";
                delivery.UpdatedAt = now;
            }
        }

        settings.LastErrorCode = null;
        settings.SafeErrorDetail = null;
        settings.UpdatedAt = now;
        if (db.Entry(settings).State == EntityState.Detached)
        {
            db.SchoolManagerSettings.Add(settings);
        }

        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Application.Identifiers.UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = "school_manager.settings_updated",
            ObjectType = "school_manager_settings",
            ObjectId = settings.Id,
            Outcome = "succeeded",
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                settings.Enabled,
                settings.DryRun,
                credentialChanged = newReference is not null,
            }),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (newReference is not null)
            {
                await TryDeleteAsync(secretStore, newReference);
            }

            throw;
        }

        if (newReference is not null
            && previousReference is not null
            && !string.Equals(
                previousReference,
                newReference.Value,
                StringComparison.Ordinal))
        {
            await TryDeleteAsync(secretStore, new AiSecretReference(previousReference));
        }

        return Results.Ok(new
        {
            configured = settings.PasswordSecretReference is not null,
            settings.Enabled,
            settings.DryRun,
            settings.BaseUrl,
            settings.Username,
            settings.ActivationStartedAt,
            settingsRevision = settings.Revision,
        });
    }

    private static async Task<IResult> ListDeliveriesAsync(
        int? limit,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var deliveries = await db.GuardianDeliveries
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(take)
            .Select(item => new
            {
                item.Id,
                item.Kind,
                item.StudentId,
                item.SubmissionId,
                item.State,
                item.NotBeforeAt,
                item.AttemptCount,
                item.LastAttemptAt,
                item.LastDryRunAt,
                item.SentAt,
                item.LastErrorCode,
                item.SafeErrorDetail,
                item.CreatedAt,
                item.UpdatedAt,
                item.Revision,
            })
            .ToArrayAsync(cancellationToken);
        return Results.Ok(new { items = deliveries });
    }

    private static bool TryValidate(
        SaveSchoolManagerSettingsRequest request,
        out Uri? baseUri,
        out string? error)
    {
        baseUri = null;
        error = null;
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.Equals(parsed.Host, "fsm.flens.jp", StringComparison.OrdinalIgnoreCase)
            || parsed.Port != 443
            || parsed.AbsolutePath != "/")
        {
            error = "接続先は https://fsm.flens.jp/ に固定されています。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Username)
            || request.Username.Length > 200
            || request.Username.Any(char.IsControl))
        {
            error = "ユーザー名を確認してください。";
            return false;
        }

        if (request.Password is { Length: > 4096 }
            || request.Password?.Any(char.IsControl) == true)
        {
            error = "パスワードを確認してください。";
            return false;
        }

        baseUri = new Uri("https://fsm.flens.jp/", UriKind.Absolute);
        return true;
    }

    private static async Task TryDeleteAsync(
        IAiSecretStore secretStore,
        AiSecretReference reference)
    {
        try
        {
            await secretStore.DeleteAsync(reference);
        }
        catch
        {
            // The new database reference remains authoritative. An orphaned
            // encrypted envelope is safer than failing a credential rotation.
        }
    }

    public sealed record SaveSchoolManagerSettingsRequest(
        string BaseUrl,
        string Username,
        string? Password,
        bool Enabled,
        bool? DryRun);
}

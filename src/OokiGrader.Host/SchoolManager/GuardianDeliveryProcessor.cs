using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.Host.SchoolManager;

public sealed class GuardianDeliveryProcessor(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IContentStore contentStore,
    IAiSecretStore secretStore,
    ISchoolManagerClient schoolManagerClient,
    TimeProvider timeProvider)
{
    private const int MaximumAttachmentBytes = 25 * 1024 * 1024;

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        if (await PromoteOrFailWaitingResultAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var settings = await db.SchoolManagerSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (settings is not { Enabled: true }
            || settings.PasswordSecretReference is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var delivery = await LoadNextAsync(db, settings.DryRun, now, cancellationToken)
            .ConfigureAwait(false);
        if (delivery is null)
        {
            return false;
        }

        if (delivery.Kind == "pass_fail_table")
        {
            var lastSent = await db.GuardianDeliveries
                .AsNoTracking()
                .Where(item => item.StudentId == delivery.StudentId
                    && item.Kind == "pass_fail_table"
                    && (item.State == "sent"
                        || item.State == "sending"
                        || item.LastErrorCode == "message_send_outcome_unknown"))
                .OrderByDescending(item => item.SentAt ?? item.LastAttemptAt)
                .Select(item => item.SentAt ?? item.LastAttemptAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var allowedAt = GuardianDeliverySchedule.NextPassFailNotBefore(
                now,
                lastSent);
            if (allowedAt > now)
            {
                await RescheduleAsync(delivery.Id, allowedAt, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        byte[] attachment;
        try
        {
            attachment = await ReadAttachmentAsync(delivery, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or InvalidDataException
                or UnauthorizedAccessException)
        {
            await FailAsync(
                    delivery.Id,
                    "delivery_attachment_unavailable",
                    "The verified PDF attachment is unavailable.",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        try
        {
            await MarkAttemptStartedAsync(
                    delivery.Id,
                    settings.DryRun,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            using var secret = await secretStore.ReadAsync(
                    new AiSecretReference(settings.PasswordSecretReference),
                    cancellationToken)
                .ConfigureAwait(false);
            var password = Encoding.UTF8.GetString(secret.Utf8Bytes.Span);
            var result = await schoolManagerClient.DeliverAsync(
                    new SchoolManagerDeliveryRequest(
                        new Uri(settings.BaseUrl, UriKind.Absolute),
                        new SchoolManagerCredentials(settings.Username, password),
                        delivery.StudentNumber,
                        delivery.StudentDisplayName,
                        BuildTitle(delivery.Kind),
                        BuildBody(delivery.Kind),
                        delivery.AttachmentName,
                        attachment,
                        settings.DryRun),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.DryRun)
            {
                await CompleteDryRunAsync(delivery.Id, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await CompleteSendAsync(
                        delivery.Id,
                        result.ThreadId!,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (SchoolManagerClientException exception)
        {
            await FailAsync(
                    delivery.Id,
                    exception.OutcomeUnknown
                        ? "message_send_outcome_unknown"
                        : exception.Code,
                    exception.OutcomeUnknown
                        ? "The send outcome is unknown; automatic retry is blocked."
                        : "School Manager delivery stopped before a confirmed send.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException
                or ArgumentException
                or InvalidDataException)
        {
            await FailAsync(
                    delivery.Id,
                    "school_manager_credential_unavailable",
                    "The protected School Manager credential is unavailable.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(attachment);
        }

        return true;
    }

    private async Task<bool> PromoteOrFailWaitingResultAsync(
        CancellationToken cancellationToken)
    {
        return await writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var delivery = await db.GuardianDeliveries
                .Include(item => item.ExportRecord)
                .Where(item => item.Kind == "result_pdf" && item.State == "pending")
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (delivery is null || delivery.ExportRecord is null)
            {
                return false;
            }

            if (delivery.ExportRecord.State == "verified")
            {
                delivery.State = "ready";
            }
            else if (delivery.ExportRecord.State is "failed" or "superseded")
            {
                delivery.State = "failed";
                delivery.LastErrorCode = "result_pdf_unavailable";
                delivery.SafeErrorDetail =
                    "The result PDF could not be prepared for guardian delivery.";
            }
            else
            {
                return false;
            }

            delivery.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DeliveryMaterial?> LoadNextAsync(
        OokiGraderDbContext db,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return await db.GuardianDeliveries
            .AsNoTracking()
            .Where(item => item.State == "ready"
                && item.NotBeforeAt <= now
                && (!dryRun || item.LastDryRunAt == null))
            .OrderBy(item => item.NotBeforeAt)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => new DeliveryMaterial(
                item.Id,
                item.Kind,
                item.StudentId,
                item.Student.StudentNumber,
                item.Student.DisplayName,
                item.AttachmentName,
                item.Kind == "result_pdf"
                    ? item.ExportRecord!.FileReference!.FileObject.StorageClass
                    : item.FileReference!.FileObject.StorageClass,
                item.Kind == "result_pdf"
                    ? item.ExportRecord!.FileReference!.FileObject.Sha256
                    : item.FileReference!.FileObject.Sha256,
                item.Kind == "result_pdf"
                    ? item.ExportRecord!.FileReference!.FileObject.Bytes
                    : item.FileReference!.FileObject.Bytes,
                item.Kind == "result_pdf"
                    ? item.ExportRecord!.FileReference!.FileObject.Extension
                    : item.FileReference!.FileObject.Extension,
                item.Kind == "result_pdf"
                    ? item.ExportRecord!.FileReference!.FileObject.State
                    : item.FileReference!.FileObject.State))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<byte[]> ReadAttachmentAsync(
        DeliveryMaterial delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.StorageClass != ContentStorageClass.ResultReport.ToString()
            || delivery.State != "available"
            || delivery.Extension != "pdf"
            || delivery.Bytes is <= 4 or > MaximumAttachmentBytes
            || delivery.Sha256.Length != 64)
        {
            throw new InvalidDataException("The delivery PDF metadata is invalid.");
        }

        var locator = new ContentObjectLocator(
            ContentStorageClass.ResultReport,
            delivery.Sha256,
            delivery.Bytes,
            delivery.Extension);
        await using var stream = await contentStore.OpenReadAsync(
                locator,
                cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream((int)delivery.Bytes);
        await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        var bytes = output.ToArray();
        if (bytes.LongLength != delivery.Bytes
            || bytes[0] != (byte)'%'
            || bytes[1] != (byte)'P'
            || bytes[2] != (byte)'D'
            || bytes[3] != (byte)'F'
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                delivery.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("The delivery PDF failed verification.");
        }

        return bytes;
    }

    private Task MarkAttemptStartedAsync(
        string deliveryId,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateAsync(deliveryId, delivery =>
        {
            if (delivery.State != "ready")
            {
                throw new InvalidOperationException(
                    "The guardian delivery is no longer ready.");
            }

            delivery.AttemptCount = checked(delivery.AttemptCount + 1);
            delivery.LastAttemptAt = now;
            delivery.LastErrorCode = null;
            delivery.SafeErrorDetail = null;
            if (!dryRun)
            {
                delivery.State = "sending";
                if (delivery.Kind == "pass_fail_table")
                {
                    delivery.SentLocalDate = GuardianDeliverySchedule.TokyoDate(now);
                }
            }
        }, cancellationToken);

    private Task CompleteDryRunAsync(
        string deliveryId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateAsync(deliveryId, delivery =>
        {
            if (delivery.State != "ready")
            {
                throw new InvalidOperationException(
                    "The guardian delivery dry run is no longer current.");
            }

            delivery.LastDryRunAt = now;
            delivery.LastErrorCode = null;
            delivery.SafeErrorDetail = null;
        }, cancellationToken, "guardian_delivery.dry_run_verified", "dry_run",
            markSettingsSuccess: true);

    private Task CompleteSendAsync(
        string deliveryId,
        string threadId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateAsync(deliveryId, delivery =>
        {
            if (delivery.State != "sending")
            {
                throw new InvalidOperationException(
                    "The guardian delivery send is no longer current.");
            }

            delivery.State = "sent";
            delivery.SentAt = now;
            delivery.SentLocalDate = GuardianDeliverySchedule.TokyoDate(now);
            delivery.SchoolManagerThreadId = threadId;
            delivery.LastErrorCode = null;
            delivery.SafeErrorDetail = null;
        }, cancellationToken, "guardian_delivery.sent", "school_manager_confirmed",
            markSettingsSuccess: true);

    private Task FailAsync(
        string deliveryId,
        string errorCode,
        string safeDetail,
        CancellationToken cancellationToken) =>
        UpdateAsync(deliveryId, delivery =>
        {
            if (delivery.State is "sent" or "canceled")
            {
                return;
            }

            delivery.State = "failed";
            delivery.LastErrorCode = errorCode;
            delivery.SafeErrorDetail = safeDetail;
            if (delivery.Kind == "pass_fail_table"
                && errorCode == "message_send_outcome_unknown")
            {
                delivery.SentLocalDate = GuardianDeliverySchedule.TokyoDate(
                    delivery.LastAttemptAt ?? timeProvider.GetUtcNow());
            }
            else if (delivery.Kind == "pass_fail_table")
            {
                delivery.SentLocalDate = null;
            }
        }, cancellationToken, "guardian_delivery.failed", errorCode);

    private Task RescheduleAsync(
        string deliveryId,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken) =>
        UpdateAsync(deliveryId, delivery => delivery.NotBeforeAt = notBefore,
            cancellationToken);

    private Task UpdateAsync(
        string deliveryId,
        Action<GuardianDeliveryEntity> update,
        CancellationToken cancellationToken,
        string? auditEvent = null,
        string? reasonCode = null,
        bool markSettingsSuccess = false) =>
        writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var delivery = await db.GuardianDeliveries
                .SingleAsync(item => item.Id == deliveryId, token)
                .ConfigureAwait(false);
            update(delivery);
            var now = timeProvider.GetUtcNow();
            delivery.UpdatedAt = now;
            if (markSettingsSuccess)
            {
                var settings = await db.SchoolManagerSettings
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (settings is not null)
                {
                    settings.LastCredentialTestedAt = now;
                    settings.LastErrorCode = null;
                    settings.SafeErrorDetail = null;
                    settings.UpdatedAt = now;
                }
            }

            if (auditEvent is not null)
            {
                db.AuditEvents.Add(new AuditEventEntity
                {
                    Id = UlidId.New(now),
                    OccurredAt = now,
                    EventType = auditEvent,
                    ObjectType = "guardian_delivery",
                    ObjectId = delivery.Id,
                    Outcome = auditEvent.EndsWith("failed", StringComparison.Ordinal)
                        ? "failed"
                        : "succeeded",
                    ReasonCode = reasonCode,
                    SafeMetadataJson = JsonSerializer.Serialize(new
                    {
                        delivery.Kind,
                        delivery.AttemptCount,
                    }),
                });
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private static string BuildTitle(string kind) => kind == "result_pdf"
        ? "採点結果のお知らせ"
        : "合否表更新のお知らせ";

    private static string BuildBody(string kind) => kind == "result_pdf"
        ? "採点結果をお送りします。添付のPDFをご確認ください。"
        : "合否表を更新しました。氏名と生徒IDを除いた、同じクラスの範囲をお送りします。";

    private sealed record DeliveryMaterial(
        string Id,
        string Kind,
        string StudentId,
        string StudentNumber,
        string StudentDisplayName,
        string AttachmentName,
        string StorageClass,
        string Sha256,
        long Bytes,
        string Extension,
        string State);
}

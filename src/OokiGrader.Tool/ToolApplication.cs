using System.Text.Json;
using System.Text.Json.Serialization;
using OokiGrader.Infrastructure.Backups;

namespace OokiGrader.Tool;

public static class ToolApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        CommandLine? commandLine = null;
        try
        {
            commandLine = CommandLine.Parse(arguments);
            return (commandLine.Command, commandLine.Subcommand) switch
            {
                ("help", null) => await WriteHelpAsync(output)
                    .ConfigureAwait(false),
                ("health", null) => await RunHealthAsync(
                        commandLine,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false),
                ("backup", "verify") => await RunBackupVerifyAsync(
                        commandLine,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false),
                ("restore", "plan") => await RunRestorePlanAsync(
                        commandLine,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false),
                ("restore", "execute") => await RunRestoreExecuteAsync(
                        commandLine,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ToolUsageException(
                    "command_unknown",
                    "The command or subcommand is not supported."),
            };
        }
        catch (ToolUsageException exception)
        {
            await WriteErrorAsync(
                    error,
                    commandLine?.Command ?? "unknown",
                    exception.ErrorCode,
                    exception.SafeDetail,
                    commandLine?.Json ?? arguments.Contains(
                        "--json",
                        StringComparer.Ordinal))
                .ConfigureAwait(false);
            return ToolExitCodes.Usage;
        }
        catch (RestoreExecutionException exception)
        {
            await WriteErrorAsync(
                    error,
                    "restore.execute",
                    exception.ErrorCode,
                    exception.SafeDetail,
                    commandLine?.Json ?? false,
                    exception.MutationPerformed)
                .ConfigureAwait(false);
            return exception.SafetyRefusal
                ? ToolExitCodes.SafetyRefusal
                : ToolExitCodes.CheckFailed;
        }
        catch (BackupOperationException exception)
        {
            await WriteErrorAsync(
                    error,
                    commandLine?.Command ?? "backup",
                    exception.ErrorCode,
                    exception.SafeDetail,
                    commandLine?.Json ?? false)
                .ConfigureAwait(false);
            return exception.IsConfigurationError
                ? ToolExitCodes.SafetyRefusal
                : ToolExitCodes.CheckFailed;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            await WriteErrorAsync(
                    error,
                    commandLine?.Command ?? "unknown",
                    "operation_cancelled",
                    "The operation was cancelled.",
                    commandLine?.Json ?? false)
                .ConfigureAwait(false);
            return ToolExitCodes.CheckFailed;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            await WriteErrorAsync(
                    error,
                    commandLine?.Command ?? "unknown",
                    "diagnostic_read_failed",
                    "The requested diagnostic data could not be read safely.",
                    commandLine?.Json ?? false)
                .ConfigureAwait(false);
            return ToolExitCodes.CheckFailed;
        }
    }

    private static async Task<int> RunHealthAsync(
        CommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        commandLine.AllowOnly(
            "database",
            "data-root",
            "content-root",
            "json");
        var databasePath = SafePaths.RequireAbsoluteNonRoot(
            commandLine.RequireValue("database"),
            "--database",
            requireExistingFile: true);
        var dataRoot = SafePaths.RequireAbsoluteNonRoot(
            commandLine.RequireValue("data-root"),
            "--data-root",
            requireExistingDirectory: true);
        var contentRoot = SafePaths.RequireAbsoluteNonRoot(
            commandLine.OptionalValue("content-root")
                ?? Path.Combine(dataRoot, "objects"),
            "--content-root",
            requireExistingDirectory: true);
        var inspector = new ReadOnlyHealthInspector(TimeProvider.System);
        var result = await inspector.InspectAsync(
                databasePath,
                dataRoot,
                contentRoot,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteResultAsync(output, result, commandLine.Json)
            .ConfigureAwait(false);
        return result.State == "healthy"
            ? ToolExitCodes.Success
            : ToolExitCodes.CheckFailed;
    }

    private static async Task<int> RunBackupVerifyAsync(
        CommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        commandLine.AllowOnly(
            "database",
            "content-root",
            "destination",
            "destination-encryption-confirmed",
            "backup-id",
            "relative-path",
            "manifest-sha256",
            "json");
        var invocation = ReadBackupInvocation(commandLine);
        var service = CreateReadOnlyBackupService(invocation);
        var verification = await service.VerifyAsync(
                invocation.BackupId,
                invocation.RelativePath,
                invocation.ManifestSha256,
                cancellationToken)
            .ConfigureAwait(false);
        var result = new BackupVerifyCommandResult(
            "backup.verify",
            verification.Verified ? "healthy" : "unavailable",
            verification.CheckedAt,
            MutationPerformed: false,
            verification.BackupId,
            verification.Verified,
            verification.IntegrityResult,
            verification.VerifiedFileCount,
            verification.VerifiedBytes,
            verification.DatabaseMigrationId,
            verification.ErrorCode,
            verification.SafeErrorDetail);
        await WriteResultAsync(output, result, commandLine.Json)
            .ConfigureAwait(false);
        return verification.Verified
            ? ToolExitCodes.Success
            : ToolExitCodes.CheckFailed;
    }

    private static async Task<int> RunRestorePlanAsync(
        CommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        commandLine.AllowOnly(
            "database",
            "content-root",
            "destination",
            "destination-encryption-confirmed",
            "backup-id",
            "relative-path",
            "manifest-sha256",
            "json");
        var invocation = ReadBackupInvocation(commandLine);
        var service = CreateReadOnlyBackupService(invocation);
        var plan = await service.ValidateRestorePlanAsync(
                invocation.BackupId,
                invocation.RelativePath,
                invocation.ManifestSha256,
                cancellationToken)
            .ConfigureAwait(false);
        var result = new RestorePlanCommandResult(
            "restore.plan",
            plan.CanRestore ? "ready" : "unavailable",
            plan.CheckedAt,
            MutationPerformed: false,
            OperationMode: "validation-and-plan-only",
            LiveDataOverwriteSupported: false,
            MaintenanceConfirmationRequired: true,
            OfflineConfirmationRequired: true,
            plan.BackupId,
            plan.CanRestore,
            plan.IntegrityResult,
            plan.BackupMigrationId,
            plan.CurrentMigrationId,
            plan.RequiresMigration,
            plan.ManagedScansIncluded,
            plan.RequiredActions,
            plan.ErrorCode,
            plan.SafeErrorDetail);
        await WriteResultAsync(output, result, commandLine.Json)
            .ConfigureAwait(false);
        return plan.CanRestore
            ? ToolExitCodes.Success
            : ToolExitCodes.CheckFailed;
    }

    private static async Task<int> RunRestoreExecuteAsync(
        CommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        commandLine.AllowOnly(
            "database",
            "data-root",
            "content-root",
            "destination",
            "destination-encryption-confirmed",
            "backup-id",
            "relative-path",
            "manifest-sha256",
            "maintenance-confirmed",
            "offline-confirmed",
            "confirm-restore",
            "json");
        var invocation = ReadBackupInvocation(commandLine);
        if (invocation.ManifestSha256 is null)
        {
            throw new ToolUsageException(
                "manifest_hash_required",
                "Offline restore execution requires the expected manifest SHA-256.");
        }

        var dataRoot = SafePaths.RequireAbsoluteNonRoot(
            commandLine.RequireValue("data-root"),
            "--data-root",
            requireExistingDirectory: true);
        var request = new RestoreExecutionRequest(
            invocation.DatabasePath,
            dataRoot,
            invocation.ContentRoot,
            invocation.DestinationRoot,
            invocation.BackupId,
            invocation.RelativePath,
            invocation.ManifestSha256,
            commandLine.HasFlag("maintenance-confirmed"),
            commandLine.HasFlag("offline-confirmed"),
            commandLine.RequireValue("confirm-restore"));
        var executor = new OfflineRestoreExecutor(
            CreateReadOnlyBackupService(invocation),
            TimeProvider.System);
        var execution = await executor.ExecuteAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        var result = new RestoreExecuteCommandResult(
            Command: "restore.execute",
            State: "restored-awaiting-signoff",
            CompletedAt: execution.CompletedAt,
            MutationPerformed: true,
            OperationMode: "offline-atomic-data-root-replacement",
            BackupId: execution.BackupId,
            ManifestSha256: execution.ManifestSha256,
            RestoredFileCount: execution.RestoredFileCount,
            RestoredBytes: execution.RestoredBytes,
            RollbackSnapshotCreated: true,
            RollbackSnapshotId: execution.RollbackSnapshotId,
            MaintenanceModeEnforced: true,
            RestoreMarkerPresent: true,
            ManagedScansIncluded: execution.ManagedScansIncluded,
            ProviderCredentialsRequireValidation: true,
            RequiredActions:
            [
                "Keep the service offline until the restored database and rollback snapshot are independently checked.",
                "Validate or re-enter provider credentials on this Windows host.",
                "Start only the approved read-only verification workflow.",
                "Remove the restore marker and exit maintenance mode only after administrator sign-off.",
                "Retain the rollback snapshot until the restore drill is documented and accepted.",
            ]);
        await WriteResultAsync(output, result, commandLine.Json)
            .ConfigureAwait(false);
        return ToolExitCodes.Success;
    }

    private static BackupInvocation ReadBackupInvocation(
        CommandLine commandLine)
    {
        if (!commandLine.HasFlag("destination-encryption-confirmed"))
        {
            throw new ToolUsageException(
                "destination_encryption_confirmation_required",
                "Archive diagnostics require explicit confirmation that the destination is encrypted.");
        }

        var manifestSha256 = commandLine.OptionalValue("manifest-sha256");
        if (manifestSha256 is not null
            && (manifestSha256.Length != 64
                || !manifestSha256.All(character =>
                    character is >= '0' and <= '9'
                        or >= 'a' and <= 'f'
                        or >= 'A' and <= 'F')))
        {
            throw new ToolUsageException(
                "manifest_hash_invalid",
                "The expected manifest SHA-256 is invalid.");
        }

        var backupId = commandLine.RequireValue("backup-id");
        var relativePath = SafePaths.RequireCanonicalBackupRelativePath(
            commandLine.RequireValue("relative-path"),
            backupId);
        return new BackupInvocation(
            SafePaths.RequireAbsoluteNonRoot(
                commandLine.RequireValue("database"),
                "--database",
                requireExistingFile: true),
            SafePaths.RequireAbsoluteNonRoot(
                commandLine.RequireValue("content-root"),
                "--content-root",
                requireExistingDirectory: true),
            SafePaths.RequireAbsoluteNonRoot(
                commandLine.RequireValue("destination"),
                "--destination",
                requireExistingDirectory: true),
            backupId,
            relativePath,
            manifestSha256);
    }

    private static SqliteOnlineBackupArchiveService CreateReadOnlyBackupService(
        BackupInvocation invocation) =>
        new SqliteOnlineBackupArchiveService(
            new BackupOptions
            {
                DatabasePath = invocation.DatabasePath,
                ContentRootPath = invocation.ContentRoot,
                DestinationRootPath = invocation.DestinationRoot,
                DestinationEncryptionConfirmed = true,
                Enabled = false,
                IncludeManagedScans = false,
                ProbeDestinationWriteAccess = false,
            },
            new ReadOnlyContentStore(),
            TimeProvider.System);

    private static async Task WriteResultAsync<T>(
        TextWriter output,
        T result,
        bool json)
    {
        if (json)
        {
            await output.WriteLineAsync(
                    JsonSerializer.Serialize(result, JsonOptions))
                .ConfigureAwait(false);
            return;
        }

        switch (result)
        {
            case HealthCommandResult health:
                await output.WriteLineAsync($"state={health.State}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"database={health.Database.State}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"schemaCurrent={health.Database.SchemaCurrent}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"storage={health.Storage.State}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync("mutationPerformed=false")
                    .ConfigureAwait(false);
                break;
            case BackupVerifyCommandResult verification:
                await output.WriteLineAsync($"state={verification.State}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"backupId={verification.BackupId}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"verified={verification.Verified}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"integrity={verification.IntegrityResult}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync("mutationPerformed=false")
                    .ConfigureAwait(false);
                break;
            case RestorePlanCommandResult plan:
                await output.WriteLineAsync($"state={plan.State}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync($"backupId={plan.BackupId}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"canRestore={plan.CanRestore}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"operationMode={plan.OperationMode}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        "liveDataOverwriteSupported=false")
                    .ConfigureAwait(false);
                await output.WriteLineAsync("mutationPerformed=false")
                    .ConfigureAwait(false);
                foreach (var action in plan.RequiredActions)
                {
                    await output.WriteLineAsync($"action={action}")
                        .ConfigureAwait(false);
                }

                break;
            case RestoreExecuteCommandResult execution:
                await output.WriteLineAsync($"state={execution.State}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"backupId={execution.BackupId}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"operationMode={execution.OperationMode}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"restoredFileCount={execution.RestoredFileCount}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"rollbackSnapshotCreated={execution.RollbackSnapshotCreated}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"restoreMarkerPresent={execution.RestoreMarkerPresent}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync("mutationPerformed=true")
                    .ConfigureAwait(false);
                foreach (var action in execution.RequiredActions)
                {
                    await output.WriteLineAsync($"action={action}")
                        .ConfigureAwait(false);
                }

                break;
        }
    }

    private static async Task WriteErrorAsync(
        TextWriter error,
        string command,
        string errorCode,
        string detail,
        bool json,
        bool mutationPerformed = false)
    {
        var result = new ToolErrorResult(
            command,
            "error",
            mutationPerformed,
            errorCode,
            detail);
        if (json)
        {
            await error.WriteLineAsync(
                    JsonSerializer.Serialize(result, JsonOptions))
                .ConfigureAwait(false);
            return;
        }

        await error.WriteLineAsync($"error={errorCode}")
            .ConfigureAwait(false);
        await error.WriteLineAsync($"detail={detail}")
            .ConfigureAwait(false);
        await error.WriteLineAsync(
                $"mutationPerformed={mutationPerformed.ToString().ToLowerInvariant()}")
            .ConfigureAwait(false);
    }

    private static async Task<int> WriteHelpAsync(TextWriter output)
    {
        await output.WriteLineAsync(
            """
            OokiGrader.Tool performs local diagnostics and explicitly gated
            offline recovery.

            Commands:
              health --database <absolute-file> --data-root <absolute-dir>
                     [--content-root <absolute-dir>] [--json]

              backup verify --database <absolute-file>
                     --content-root <absolute-dir>
                     --destination <absolute-encrypted-dir>
                     --destination-encryption-confirmed
                     --backup-id <canonical-ulid>
                     --relative-path <backup-set-relative-path>
                     [--manifest-sha256 <sha256>] [--json]

              restore plan --database <absolute-file>
                     --content-root <absolute-dir>
                     --destination <absolute-encrypted-dir>
                     --destination-encryption-confirmed
                     --backup-id <canonical-ulid>
                     --relative-path <backup-set-relative-path>
                     [--manifest-sha256 <sha256>] [--json]

              restore execute --database <absolute-file>
                     --data-root <absolute-dir>
                     --content-root <absolute-dir>
                     --destination <absolute-encrypted-dir>
                     --destination-encryption-confirmed
                     --backup-id <canonical-ulid>
                     --relative-path <canonical-backup-set-path>
                     --manifest-sha256 <sha256>
                     --maintenance-confirmed --offline-confirmed
                     --confirm-restore <same-backup-id> [--json]

            Restore execution refuses a running or non-maintenance installation.
            It stages and verifies a replacement beside the live data root,
            preserves the current root as a rollback snapshot, and leaves the
            restored database in maintenance mode with a restore marker.
            """).ConfigureAwait(false);
        return ToolExitCodes.Success;
    }

    private sealed record BackupInvocation(
        string DatabasePath,
        string ContentRoot,
        string DestinationRoot,
        string BackupId,
        string RelativePath,
        string? ManifestSha256);
}

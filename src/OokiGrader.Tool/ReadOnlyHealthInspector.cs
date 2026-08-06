using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Tool;

internal sealed class ReadOnlyHealthInspector(TimeProvider timeProvider)
{
    public async Task<HealthCommandResult> InspectAsync(
        string databasePath,
        string dataRoot,
        string contentRoot,
        CancellationToken cancellationToken)
    {
        var checkedAt = timeProvider.GetUtcNow();
        var database = await InspectDatabaseAsync(
                databasePath,
                dataRoot,
                cancellationToken)
            .ConfigureAwait(false);
        var storage = InspectStorage(
            dataRoot,
            contentRoot,
            database.PhysicalFreeReserveBytes);

        var state = storage.RestoreOrMigrationMarkerPresent
            ? "unavailable"
            : database.State == "unavailable"
            || storage.State == "unavailable"
            ? "unavailable"
            : database.MaintenanceMode
                || storage.RestoreOrMigrationMarkerPresent
                ? "degraded"
                : "healthy";
        return new HealthCommandResult(
            "health",
            state,
            checkedAt,
            MutationPerformed: false,
            database,
            storage);
    }

    private static StorageHealthResult InspectStorage(
        string dataRoot,
        string contentRoot,
        long? requiredReserveBytes)
    {
        var dataRootReadable = SafePaths.IsReadableDirectory(dataRoot);
        var contentRootReadable = SafePaths.IsReadableDirectory(contentRoot);
        long? freeBytes = null;
        if (dataRootReadable)
        {
            try
            {
                var root = Path.GetPathRoot(dataRoot);
                if (root is not null)
                {
                    freeBytes = new DriveInfo(root).AvailableFreeSpace;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException)
            {
                freeBytes = null;
            }
        }

        var operationMarkerPresent =
            File.Exists(Path.Combine(
                dataRoot,
                "operations",
                "restore.in-progress"))
            || File.Exists(Path.Combine(
                dataRoot,
                "operations",
                "migration.in-progress"));
        var reserveSatisfied = freeBytes is not null
            && requiredReserveBytes is not null
            && freeBytes.Value >= requiredReserveBytes.Value;
        var state = dataRootReadable
            && contentRootReadable
            && reserveSatisfied
            ? "healthy"
            : "unavailable";
        return new StorageHealthResult(
            state,
            dataRootReadable,
            contentRootReadable,
            WriteProbePerformed: false,
            freeBytes,
            requiredReserveBytes,
            reserveSatisfied,
            operationMarkerPresent,
            state == "healthy"
                ? null
                : !dataRootReadable || !contentRootReadable
                    ? "storage_not_readable"
                    : freeBytes is null
                        ? "physical_storage_unavailable"
                        : "physical_reserve_not_satisfied");
    }

    private static async Task<DatabaseHealthResult> InspectDatabaseAsync(
        string databasePath,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var expectedMigrationId = FindExpectedMigrationId();
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                ForeignKeys = true,
                Pooling = false,
                DefaultTimeout = 15,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var queryOnly = connection.CreateCommand())
            {
                queryOnly.CommandText = "PRAGMA query_only = ON;";
                await queryOnly.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var integrityResult = await ReadIntegrityAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            var currentMigrationId = await ReadScalarStringAsync(
                    connection,
                    """
                    SELECT MigrationId
                    FROM "__EFMigrationsHistory"
                    ORDER BY MigrationId DESC
                    LIMIT 1;
                    """,
                    cancellationToken)
                .ConfigureAwait(false);
            var schemaCurrent = string.Equals(
                currentMigrationId,
                expectedMigrationId,
                StringComparison.Ordinal);

            await using var settings = connection.CreateCommand();
            settings.CommandText =
                """
                SELECT maintenance_mode, data_root, physical_free_reserve_bytes
                FROM site_settings
                WHERE id = 'site'
                LIMIT 1;
                """;
            await using var reader = await settings
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Unavailable(
                    integrityResult,
                    currentMigrationId,
                    expectedMigrationId,
                    "site_settings_missing");
            }

            var maintenanceMode = reader.GetBoolean(0);
            var configuredDataRootMatches = SafePaths.Equal(
                reader.GetString(1),
                dataRoot);
            var requiredReserve = reader.GetInt64(2);
            var latestVerifiedBackupAt = await ReadNullableUnixMillisecondsAsync(
                    connection,
                    """
                    SELECT MAX(verified_at)
                    FROM backup_record
                    WHERE state = 'verified';
                    """,
                    cancellationToken)
                .ConfigureAwait(false);
            var databaseState = integrityResult == "ok"
                && schemaCurrent
                && configuredDataRootMatches
                ? "healthy"
                : "unavailable";
            return new DatabaseHealthResult(
                databaseState,
                integrityResult,
                currentMigrationId,
                expectedMigrationId,
                schemaCurrent,
                maintenanceMode,
                configuredDataRootMatches,
                requiredReserve,
                latestVerifiedBackupAt,
                databaseState == "healthy"
                    ? null
                    : integrityResult != "ok"
                        ? "database_integrity_failed"
                        : !schemaCurrent
                            ? "database_schema_not_current"
                            : "configured_data_root_mismatch")
            {
            };
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            return Unavailable(
                "unavailable",
                currentMigrationId: null,
                expectedMigrationId,
                "database_read_failed");
        }
    }

    private static async Task<string> ReadIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check(16);";
        var rows = new List<string>(capacity: 2);
        await using (var reader = await integrity
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == 16)
                {
                    return "integrity_check_returned_too_many_rows";
                }

                rows.Add(Bound(reader.GetString(0)));
            }
        }

        if (rows.Count != 1
            || !string.Equals(rows[0], "ok", StringComparison.Ordinal))
        {
            return rows.Count == 0
                ? "integrity_check_returned_no_result"
                : string.Join("; ", rows);
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var foreignKeyReader = await foreignKeys
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await foreignKeyReader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false)
            ? "foreign_key_check_failed"
            : "ok";
    }

    private static async Task<string?> ReadScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var result = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return result is DBNull or null ? null : (string)result;
    }

    private static async Task<DateTimeOffset?> ReadNullableUnixMillisecondsAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var result = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return result is DBNull or null
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds((long)result);
    }

    private static string? FindExpectedMigrationId() =>
        typeof(OokiGraderDbContext).Assembly
            .GetTypes()
            .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
            .Where(id => id is not null)
            .OrderBy(id => id, StringComparer.Ordinal)
            .LastOrDefault();

    private static DatabaseHealthResult Unavailable(
        string integrityResult,
        string? currentMigrationId,
        string? expectedMigrationId,
        string errorCode) =>
        new(
            "unavailable",
            integrityResult,
            currentMigrationId,
            expectedMigrationId,
            SchemaCurrent: false,
            MaintenanceMode: false,
            ConfiguredDataRootMatches: false,
            PhysicalFreeReserveBytes: null,
            LastVerifiedBackupAt: null,
            errorCode);

    private static string Bound(string value) =>
        value.Length <= 160 ? value : value[..160];
}

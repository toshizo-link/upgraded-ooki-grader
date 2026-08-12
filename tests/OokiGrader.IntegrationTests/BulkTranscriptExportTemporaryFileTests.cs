using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class BulkTranscriptExportTemporaryFileTests
{
    [Fact]
    public void SweepDeletesOnlyStaleOwnedTemporaryArchives()
    {
        var directory = CreateTestDirectory();
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var staleOwned = CreateFile(
                BulkTranscriptExportTemporaryFiles.CreatePath(
                    directory,
                    "stale-job",
                    1),
                now.Subtract(
                    BulkTranscriptExportTemporaryFiles.StaleAge
                    + TimeSpan.FromMinutes(1)));
            var freshOwned = CreateFile(
                BulkTranscriptExportTemporaryFiles.CreatePath(
                    directory,
                    "fresh-job",
                    1),
                now.Subtract(
                    BulkTranscriptExportTemporaryFiles.StaleAge
                    - TimeSpan.FromMinutes(1)));
            var staleForeign = CreateFile(
                Path.Combine(directory, "bulk-transcript-foreign.zip.part"),
                now.Subtract(TimeSpan.FromDays(7)));
            var unrelatedForeign = CreateFile(
                Path.Combine(directory, "unrelated.zip.part"),
                now.Subtract(TimeSpan.FromDays(7)));
            var nestedDirectory = Directory.CreateDirectory(
                Path.Combine(directory, "nested")).FullName;
            var nestedOwned = CreateFile(
                Path.Combine(nestedDirectory, Path.GetFileName(staleOwned)),
                now.Subtract(TimeSpan.FromDays(7)));

            var deleted = BulkTranscriptExportTemporaryFiles.SweepStale(
                directory,
                now);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(staleOwned));
            Assert.True(File.Exists(freshOwned));
            Assert.True(File.Exists(staleForeign));
            Assert.True(File.Exists(unrelatedForeign));
            Assert.True(File.Exists(nestedOwned));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SweepBoundsDeletionWorkPerPass()
    {
        var directory = CreateTestDirectory();
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        try
        {
            for (var index = 0;
                 index < BulkTranscriptExportTemporaryFiles
                     .MaximumCleanupDeletions + 1;
                 index++)
            {
                _ = CreateFile(
                    BulkTranscriptExportTemporaryFiles.CreatePath(
                        directory,
                        $"stale-job-{index}",
                        1),
                    now.Subtract(TimeSpan.FromDays(7)));
            }

            var deleted = BulkTranscriptExportTemporaryFiles.SweepStale(
                directory,
                now);

            Assert.Equal(
                BulkTranscriptExportTemporaryFiles.MaximumCleanupDeletions,
                deleted);
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ooki-bulk-export-cleanup-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateFile(
        string path,
        DateTimeOffset lastWriteTime)
    {
        File.WriteAllText(path, "test");
        File.SetLastWriteTimeUtc(path, lastWriteTime.UtcDateTime);
        return path;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;
using OokiGrader.Reports.Pdf;
using PdfSharp.Pdf.IO;

namespace OokiGrader.IntegrationTests;

public sealed class ResultPdfJobWorkerTests
{
    [Fact]
    public async Task LatestExportStateIsAvailableForReportLists()
    {
        await using var fixture = await ResultPdfWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var queued = await SubmissionsEndpoints
                .LoadLatestExportStatesAsync(db, [seeded.SubmissionId]);
            Assert.Equal("queued", queued[seeded.SubmissionId]);
        }

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var verified = await SubmissionsEndpoints
                .LoadLatestExportStatesAsync(db, [seeded.SubmissionId]);
            Assert.Equal("verified", verified[seeded.SubmissionId]);
        }
    }

    [Fact]
    public async Task RenderPersistsVerifiedReportAndContentAddressedMetadata()
    {
        await using var fixture = await ResultPdfWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var record = await db.ExportRecords
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ExportId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var reference = await db.FileReferences
            .AsNoTracking()
            .SingleAsync(item => item.Id == record.FileReferenceId);
        var fileObject = await db.FileObjects
            .AsNoTracking()
            .SingleAsync(item => item.Id == reference.FileObjectId);

        Assert.Equal("verified", record.State);
        Assert.NotNull(record.CompletedAt);
        Assert.NotNull(record.Sha256);
        Assert.True(record.Bytes > 0);
        Assert.True(record.PageCount > 0);
        Assert.Equal(ResultPdfRenderer.CurrentRendererVersion, record.RendererVersion);
        Assert.Null(record.ErrorCode);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(10_000, job.ProgressBasisPoints);
        Assert.Equal("export_record", reference.OwnerType);
        Assert.Equal(record.Id, reference.OwnerId);
        Assert.Equal("result_pdf", reference.Purpose);
        Assert.Equal(ContentStorageClass.ResultReport.ToString(), fileObject.StorageClass);
        Assert.Equal("result_report", fileObject.RetentionClass);
        Assert.Equal("application/pdf", fileObject.VerifiedMime);
        Assert.Equal("pdf", fileObject.Extension);
        Assert.Equal(record.Sha256, fileObject.Sha256);
        Assert.Equal(record.Bytes, fileObject.Bytes);
        Assert.False(fileObject.ManagedScanBytes);
        Assert.Equal(1, fileObject.ReferenceCountCache);

        var locator = new ContentObjectLocator(
            ContentStorageClass.ResultReport,
            fileObject.Sha256,
            fileObject.Bytes,
            fileObject.Extension);
        await using var pdf = await fixture.ContentStore.OpenReadAsync(locator);
        using var copy = new MemoryStream();
        await pdf.CopyToAsync(copy);
        var bytes = copy.ToArray();
        Assert.Equal(
            record.Sha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var parsed = PdfReader.Open(new MemoryStream(bytes, writable: false));
        Assert.Equal(record.PageCount, parsed.PageCount);
        Assert.Contains(
            await db.AuditEvents.AsNoTracking().ToListAsync(),
            item => item.EventType == "export.completed"
                && item.ObjectId == record.Id);
        Assert.Contains(
            await db.OutboxEvents.AsNoTracking().ToListAsync(),
            item => item.EventType == "export.status"
                && item.AggregateId == record.Id
                && item.PayloadJson.Contains(
                    "\"verified\"",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task RendererAndContentStoreRunOutsideDatabaseWriteCoordinator()
    {
        await using var fixture = await ResultPdfWorkerFixture.CreateAsync();
        _ = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.True(fixture.Renderer.WasCalled);
        Assert.True(fixture.ContentStore.PutWasCalled);
        Assert.False(fixture.Renderer.WasCalledInsideWriteCoordinator);
        Assert.False(fixture.ContentStore.PutWasCalledInsideWriteCoordinator);
    }

    [Fact]
    public async Task RedeliveryCompletesWithoutDuplicatingReportArtifact()
    {
        await using var fixture = await ResultPdfWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await fixture.RequeueAsync(seeded.JobId);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var record = await db.ExportRecords
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ExportId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("verified", record.State);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(1, await db.FileObjects.CountAsync());
        Assert.Equal(1, await db.FileReferences.CountAsync());
        Assert.Equal(
            1,
            await db.AuditEvents.CountAsync(
                item => item.EventType == "export.completed"));
    }

    [Fact]
    public async Task ChangedResultRevisionFailsAndSupersedesWithoutArtifact()
    {
        await using var fixture = await ResultPdfWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();
        await fixture.ChangeResultRevisionAsync(seeded.GradingRunId);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var record = await db.ExportRecords
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ExportId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("failed", record.State);
        Assert.Equal("export_source_changed", record.ErrorCode);
        Assert.NotNull(record.SupersededAt);
        Assert.Equal("source_changed_before_render", record.SupersededReason);
        Assert.Null(record.FileReferenceId);
        Assert.Null(record.Sha256);
        Assert.Equal("failed", job.State);
        Assert.Equal("export_source_changed", job.ErrorCode);
        Assert.Empty(await db.FileObjects.AsNoTracking().ToListAsync());
        Assert.Empty(await db.FileReferences.AsNoTracking().ToListAsync());
    }

    private sealed class ResultPdfWorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly string _root;

        private ResultPdfWorkerFixture(
            SqliteConnection connection,
            ServiceProvider services,
            string root)
        {
            _connection = connection;
            _services = services;
            _root = root;
            Worker = services.GetRequiredService<ResultPdfJobWorker>();
            Renderer = services.GetRequiredService<GuardedResultPdfRenderer>();
            ContentStore = services.GetRequiredService<GuardedContentStore>();
        }

        public ResultPdfJobWorker Worker { get; }
        public GuardedResultPdfRenderer Renderer { get; }
        public GuardedContentStore ContentStore { get; }

        public static async Task<ResultPdfWorkerFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ooki-result-pdf-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IClock>(SystemClock.Instance);
            services.AddSingleton<ObservingWriteCoordinator>();
            services.AddSingleton<IWriteCoordinator>(provider =>
                provider.GetRequiredService<ObservingWriteCoordinator>());
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<GuardedResultPdfRenderer>();
            services.AddSingleton<IResultPdfRenderer>(provider =>
                provider.GetRequiredService<GuardedResultPdfRenderer>());
            services.AddSingleton(provider => new GuardedContentStore(
                new NtfsContentStore(new ContentStoreOptions
                {
                    RootPath = Path.Combine(root, "objects"),
                }),
                provider.GetRequiredService<ObservingWriteCoordinator>()));
            services.AddSingleton<IContentStore>(provider =>
                provider.GetRequiredService<GuardedContentStore>());
            services.AddSingleton<ResultPdfJobWorker>();
            var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });

            try
            {
                await using var db = await provider
                    .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                    .CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                return new ResultPdfWorkerFixture(connection, provider, root);
            }
            catch
            {
                await provider.DisposeAsync();
                await connection.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync() =>
            _services
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();

        public async Task<SeededReport> SeedAsync()
        {
            var now = new DateTimeOffset(
                2026,
                7,
                27,
                8,
                15,
                0,
                TimeSpan.Zero);
            await using var db = await CreateDbContextAsync();
            var staff = new StaffUserEntity
            {
                Id = UlidId.New(now),
                Username = "report.teacher",
                UsernameNormalized = "report.teacher",
                DisplayName = "帳票担当",
                PasswordHash = "test",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var template = new TestTemplateEntity
            {
                Id = UlidId.New(now),
                Title = "国語・漢字確認テスト",
                State = "active",
                CreatedByStaffUserId = staff.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var version = new TemplateVersionEntity
            {
                Id = UlidId.New(now),
                TestTemplateId = template.Id,
                VersionNumber = 2,
                State = "published",
                PipelineVersion = "manual-template-v1",
                PublishedByStaffUserId = staff.Id,
                PublishedAt = now,
                ContentHash = new string('a', 64),
                CreatedAt = now,
                UpdatedAt = now,
            };
            var question = new QuestionEntity
            {
                Id = UlidId.New(now),
                TemplateVersionId = version.Id,
                LogicalQuestionId = UlidId.New(now),
                OrderIndex = 0,
                DisplayLabel = "1",
                QuestionText = "漢字「大木」の読みを答えなさい。",
                QuestionType = "exact_short_text",
                GradingMode = "deterministic",
                MaxPointsMilli = 1_000,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var session = new TestSessionEntity
            {
                Id = UlidId.New(now),
                TemplateVersionId = version.Id,
                TestDate = new DateOnly(2026, 7, 27),
                Priority = "economy",
                State = "closed",
                CreatedByStaffUserId = staff.Id,
                CreatedAt = now,
                UpdatedAt = now,
                ClosedAt = now,
            };
            var student = new StudentEntity
            {
                Id = UlidId.New(now),
                StudentNumber = "S-0042",
                StudentNumberNormalized = "s-0042",
                FamilyName = "大木",
                GivenName = "花子",
                FamilyNameNormalized = "大木",
                GivenNameNormalized = "花子",
                DisplayName = "大木 花子",
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var submission = new SubmissionEntity
            {
                Id = UlidId.New(now),
                TestSessionId = session.Id,
                State = "finalized",
                ScanPayloadState = "scan_deleted",
                ScanDeletedAt = now,
                ScanDeletionReason = "retention",
                AssignedStudentId = student.Id,
                AssignmentMethod = "teacher",
                AttemptNumber = 1,
                CanonicalForSession = true,
                UploadedByStaffUserId = staff.Id,
                FinalizedByStaffUserId = staff.Id,
                FinalizedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.AddRange(
                new SiteSettingsEntity
                {
                    Id = "site",
                    SchoolName = "大木学習塾",
                    TimeZone = "Asia/Tokyo",
                    Locale = "ja-JP",
                    DataRoot = _root,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                staff,
                template,
                version,
                question,
                session,
                student,
                submission);
            await db.SaveChangesAsync();

            var run = new GradingRunEntity
            {
                Id = UlidId.New(now),
                SubmissionId = submission.Id,
                RunNumber = 1,
                TemplateVersionId = version.Id,
                Reason = "initial",
                State = "finalized",
                PipelineVersion = "test-v1",
                CanonicalInputManifestHash = new string('b', 64),
                EarnedPointsMilli = 1_000,
                PossiblePointsMilli = 1_000,
                ResultSourceRevision = 1,
                CreatedAt = now,
                FinishedAt = now,
                FinalizedAt = now,
                FinalizedByStaffUserId = staff.Id,
            };
            db.GradingRuns.Add(run);
            await db.SaveChangesAsync();
            var result = new QuestionResultEntity
            {
                Id = UlidId.New(now),
                GradingRunId = run.Id,
                QuestionId = question.Id,
                TranscribedAnswer = "おおき",
                NormalizedAnswer = "おおき",
                ProposedPointsMilli = 1_000,
                MaximumPointsMilli = 1_000,
                Outcome = "correct",
                Method = "deterministic",
                ConfidenceBasisPoints = 10_000,
                ReviewRequired = false,
                ReviewStatus = "not_required",
                CreatedAt = now,
            };
            db.QuestionResults.Add(result);
            await db.SaveChangesAsync();
            var revision = new ResultRevisionEntity
            {
                Id = UlidId.New(now),
                QuestionResultId = result.Id,
                RevisionNumber = 1,
                AwardedPointsMilli = 1_000,
                Outcome = "correct",
                Source = "initial",
                CreatedAt = now,
            };
            db.ResultRevisions.Add(revision);
            await db.SaveChangesAsync();
            result.CurrentRevisionId = revision.Id;
            submission.CurrentGradingRunId = run.Id;
            await db.SaveChangesAsync();

            var exportId = UlidId.New(now);
            var jobId = UlidId.New(now);
            var document = new ResultReportDocument(
                exportId,
                "大木学習塾",
                student.DisplayName,
                student.StudentNumber,
                template.Title,
                session.TestDate,
                version.VersionNumber,
                run.ResultSourceRevision,
                1_000,
                1_000,
                [
                    new ResultReportQuestion(
                        question.DisplayLabel,
                        question.QuestionText,
                        result.TranscribedAnswer,
                        1_000,
                        1_000,
                        "correct",
                        IsCorrected: false,
                        TeacherComment: null),
                ],
                now,
                IsCorrectedGrade: false,
                IncludeTeacherComments: false);
            var job = new BackgroundJobEntity
            {
                Id = jobId,
                Type = ResultPdfJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey = $"export:{exportId}:test",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new { exportId }),
                State = "queued",
                MaxAttempts = 5,
                NextAttemptAt = now.AddMinutes(-1),
                CorrelationId = "report-test",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var export = new ExportRecordEntity
            {
                Id = exportId,
                SubmissionId = submission.Id,
                GradingRunId = run.Id,
                ResultSourceRevision = run.ResultSourceRevision,
                SubmissionRevisionAtCreate = submission.Revision,
                TemplateVersionId = version.Id,
                TemplateVersionNumber = version.VersionNumber,
                ExportRevision = 1,
                RendererVersion = ResultPdfRenderer.CurrentRendererVersion,
                SourceHash = ResultReportSourceHasher.Compute(document),
                BackgroundJobId = job.Id,
                State = "queued",
                CreatedByStaffUserId = staff.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BackgroundJobs.Add(job);
            db.ExportRecords.Add(export);
            await db.SaveChangesAsync();
            return new SeededReport(
                export.Id,
                job.Id,
                run.Id,
                submission.Id);
        }

        public async Task RequeueAsync(string jobId)
        {
            await using var db = await CreateDbContextAsync();
            var job = await db.BackgroundJobs.SingleAsync(item => item.Id == jobId);
            job.State = "queued";
            job.ProgressBasisPoints = 0;
            job.CompletedAt = null;
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        public async Task ChangeResultRevisionAsync(string gradingRunId)
        {
            await using var db = await CreateDbContextAsync();
            var run = await db.GradingRuns.SingleAsync(item => item.Id == gradingRunId);
            run.ResultSourceRevision = checked(run.ResultSourceRevision + 1);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class ObservingWriteCoordinator : IWriteCoordinator, IDisposable
    {
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private readonly AsyncLocal<int> _depth = new();

        public bool IsHeld => _depth.Value > 0;

        public async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                _depth.Value++;
                await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
                _mutex.Release();
            }
        }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                _depth.Value++;
                return await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
                _mutex.Release();
            }
        }

        public void Dispose()
        {
            _mutex.Dispose();
        }
    }

    private sealed class GuardedResultPdfRenderer(
        ObservingWriteCoordinator coordinator) : IResultPdfRenderer
    {
        private readonly ResultPdfRenderer _inner = new();

        public bool WasCalled { get; private set; }
        public bool WasCalledInsideWriteCoordinator { get; private set; }

        public ResultPdfRenderResult Render(ResultReportDocument report)
        {
            WasCalled = true;
            WasCalledInsideWriteCoordinator |= coordinator.IsHeld;
            return _inner.Render(report);
        }
    }

    private sealed class GuardedContentStore(
        IContentStore inner,
        ObservingWriteCoordinator coordinator) : IContentStore
    {
        public bool PutWasCalled { get; private set; }
        public bool PutWasCalledInsideWriteCoordinator { get; private set; }

        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            PutWasCalled = true;
            PutWasCalledInsideWriteCoordinator |= coordinator.IsHeld;
            return inner.PutAsync(
                source,
                storageClass,
                verifiedExtension,
                cancellationToken);
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(locator, cancellationToken);

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(locator, cancellationToken);

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(locator, cancellationToken);
    }

    private sealed record SeededReport(
        string ExportId,
        string JobId,
        string GradingRunId,
        string SubmissionId);
}

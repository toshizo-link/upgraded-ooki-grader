using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;
using OokiGrader.Preprocessing;
using SkiaSharp;

namespace OokiGrader.IntegrationTests;

public sealed class SubmissionPreprocessingWorkerTests
{
    [Fact]
    public async Task PersistsFullPagesWithoutCropsAndStableRedelivery()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(validImage: true);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());
        Assert.True(fixture.Boundary.PreprocessingCalls > 0);
        Assert.True(fixture.Boundary.ContentReadCalls > 0);
        Assert.True(fixture.Boundary.ContentWriteCalls >= 2);

        Counts initialCounts;
        await using (var db = await fixture.CreateDbContextAsync())
        {
            var submission = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.SubmissionId);
            var job = await db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.JobId);
            var page = await db.SubmissionPages
                .AsNoTracking()
                .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
            var artifacts = await db.SubmissionArtifacts
                .AsNoTracking()
                .Where(item => item.SubmissionId == seeded.SubmissionId)
                .OrderBy(item => item.ArtifactType)
                .ToArrayAsync();

            Assert.Equal("needs_name_review", submission.State);
            Assert.Equal(PreprocessingOptions.DefaultPipelineVersion,
                submission.PreprocessingPipelineVersion);
            Assert.NotNull(submission.PreprocessingCompletedAt);
            Assert.Equal(1, submission.PageCount);
            Assert.Equal(64, submission.PreprocessingManifestHash?.Length);
            Assert.Equal("succeeded", job.State);
            Assert.Equal(10_000, job.ProgressBasisPoints);
            Assert.Null(job.ErrorCode);

            Assert.Equal(200, page.WidthPixels);
            Assert.Equal(100, page.HeightPixels);
            Assert.Equal(seeded.OriginalSha256, page.SourceSha256);
            Assert.Equal(64, page.NormalizedSha256.Length);
            Assert.Equal(16, page.DifferenceHash.Length);
            Assert.Equal(page.DifferenceHash, page.PerceptualHash);
            Assert.InRange(page.BlurBasisPoints, 0, 10_000);
            Assert.InRange(page.ContrastBasisPoints, 0, 10_000);
            Assert.InRange(page.BrightnessBasisPoints, 0, 10_000);
            Assert.InRange(page.InkCoverageBasisPoints, 0, 10_000);
            Assert.Null(page.RepeatedPageNumber);

            Assert.Empty(artifacts);

            using var quality = JsonDocument.Parse(
                submission.QualitySummaryJson!);
            Assert.Equal(
                submission.PreprocessingManifestHash,
                quality.RootElement
                    .GetProperty("manifestSha256")
                    .GetString());
            Assert.Equal(
                1,
                quality.RootElement.GetProperty("pageCount").GetInt32());
            Assert.Equal(
                JsonValueKind.Array,
                quality.RootElement.GetProperty("pages").ValueKind);
            Assert.Equal(
                0,
                quality.RootElement
                    .GetProperty("repeatedPages")
                    .GetArrayLength());

            var derivedReferences = await db.FileReferences
                .AsNoTracking()
                .Where(item =>
                    item.OwnerType == "submission_page"
                    || item.OwnerType == "submission_artifact")
                .ToArrayAsync();
            Assert.Equal(2, derivedReferences.Length);
            Assert.All(
                derivedReferences,
                item => Assert.Equal(
                    seeded.RetentionAnchorAt.ToUnixTimeMilliseconds(),
                    item.RetentionAnchorAt.ToUnixTimeMilliseconds()));
            var derivedObjects = await db.FileObjects
                .AsNoTracking()
                .Where(item =>
                    item.StorageClass
                    == ContentStorageClass.ManagedScanDerived.ToString())
                .ToArrayAsync();
            Assert.NotEmpty(derivedObjects);
            Assert.All(derivedObjects, item =>
            {
                Assert.True(item.ManagedScanBytes);
                Assert.Equal("available", item.State);
                Assert.Equal("submitted_scan_derived", item.RetentionClass);
                Assert.True(item.ReferenceCountCache > 0);
            });

            foreach (var fileObject in derivedObjects)
            {
                Assert.True(await fixture.ContentStore.ExistsAsync(
                    new ContentObjectLocator(
                        ContentStorageClass.ManagedScanDerived,
                        fileObject.Sha256,
                        fileObject.Bytes,
                        fileObject.Extension)));
            }

            Assert.Contains(
                await db.AuditEvents.AsNoTracking().ToArrayAsync(),
                item => item.EventType == "submission.preprocessed"
                    && item.ObjectId == seeded.SubmissionId);
            Assert.Contains(
                await db.OutboxEvents.AsNoTracking().ToArrayAsync(),
                item => item.EventType == "submission.status"
                    && item.AggregateId == seeded.SubmissionId);
            initialCounts = new Counts(
                await db.SubmissionPages.CountAsync(),
                await db.SubmissionArtifacts.CountAsync(),
                await db.FileReferences.CountAsync(),
                await db.FileObjects.CountAsync(),
                await db.AuditEvents.CountAsync(),
                await db.OutboxEvents.CountAsync());
        }

        await fixture.RequeueAsync(seeded.JobId);
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var afterRedelivery = new Counts(
                await db.SubmissionPages.CountAsync(),
                await db.SubmissionArtifacts.CountAsync(),
                await db.FileReferences.CountAsync(),
                await db.FileObjects.CountAsync(),
                await db.AuditEvents.CountAsync(),
                await db.OutboxEvents.CountAsync());
            Assert.Equal(initialCounts, afterRedelivery);
            var job = await db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.JobId);
            Assert.Equal("succeeded", job.State);
            Assert.Equal(2, job.AttemptCount);
        }
    }

    [Theory]
    [InlineData("blank_test")]
    [InlineData("contains_non_model_answers")]
    public async Task AlignsAgainstNonAuthoritativeTemplateSources(
        string alignmentSourceRole)
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(
            validImage: true,
            includeAlignmentReference: true,
            alignmentSourceRole: alignmentSourceRole);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var page = await db.SubmissionPages
            .AsNoTracking()
            .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
        var artifacts = await db.SubmissionArtifacts
            .AsNoTracking()
            .Where(item => item.SubmissionId == seeded.SubmissionId)
            .ToArrayAsync();

        Assert.Equal("needs_name_review", submission.State);
        Assert.Equal("aligned", page.AlignmentState);
        Assert.InRange(
            page.AlignmentScoreBasisPoints!.Value,
            6_500,
            10_000);
        Assert.Equal(0, page.RotationDegrees);
        Assert.Empty(artifacts);
        using var quality = JsonDocument.Parse(
            submission.QualitySummaryJson!);
        var alignment = quality.RootElement
            .GetProperty("pages")[0]
            .GetProperty("alignment");
        Assert.Equal("aligned", alignment.GetProperty("state").GetString());
        Assert.Equal(
            page.AlignmentScoreBasisPoints,
            alignment.GetProperty("scoreBasisPoints").GetInt32());
    }

    [Fact]
    public async Task ConfiguredUnusableTemplateFailsClosed()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(
            validImage: true,
            includeAlignmentReference: true,
            unusableAlignmentReference: true);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var page = await db.SubmissionPages
            .AsNoTracking()
            .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
        var artifacts = await db.SubmissionArtifacts
            .AsNoTracking()
            .Where(item => item.SubmissionId == seeded.SubmissionId)
            .ToArrayAsync();

        Assert.Equal("needs_attention", submission.State);
        Assert.Equal("failed", page.AlignmentState);
        Assert.Equal(0, page.AlignmentScoreBasisPoints);
        Assert.Empty(artifacts);
        using var quality = JsonDocument.Parse(
            submission.QualitySummaryJson!);
        Assert.Equal(
            "needs_attention",
            quality.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvalidContentFailsSafelyWithoutDerivedMetadata()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(validImage: false);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);

        Assert.Equal("needs_attention", submission.State);
        Assert.Null(submission.PreprocessingManifestHash);
        Assert.Null(submission.PreprocessingCompletedAt);
        Assert.Equal("failed", job.State);
        Assert.Equal("preprocessing_signature_mismatch", job.ErrorCode);
        Assert.Equal(
            "The submission could not be safely preprocessed.",
            job.SafeErrorDetail);
        Assert.Empty(await db.SubmissionPages.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.SubmissionArtifacts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.FileObjects
            .AsNoTracking()
            .Where(item =>
                item.StorageClass
                == ContentStorageClass.ManagedScanDerived.ToString())
            .ToArrayAsync());
        Assert.Contains(
            await db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "submission.preprocessing_failed"
                && item.ReasonCode == "preprocessing_signature_mismatch");
        Assert.True(await fixture.ContentStore.ExistsAsync(
            new ContentObjectLocator(
                ContentStorageClass.ManagedScanOriginal,
                seeded.OriginalSha256,
                seeded.OriginalBytes,
                "png")));
    }

    [Fact]
    public async Task IgnoresEveryOtherJobType()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var jobId = await fixture.SeedUnrelatedJobAsync();

        Assert.False(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == jobId);
        Assert.Equal("queued", job.State);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.LeaseOwner);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly string _rootPath;
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        private WorkerFixture(
            string rootPath,
            SqliteConnection connection,
            ServiceProvider services,
            BoundaryProbe boundary)
        {
            _rootPath = rootPath;
            _connection = connection;
            _services = services;
            Boundary = boundary;
            Worker = services.GetRequiredService<SubmissionPreprocessingWorker>();
            ContentStore = services.GetRequiredService<IContentStore>();
        }

        public BoundaryProbe Boundary { get; }
        public SubmissionPreprocessingWorker Worker { get; }
        public IContentStore ContentStore { get; }

        public static async Task<WorkerFixture> CreateAsync()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "ooki-preprocessing-worker-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var boundary = new BoundaryProbe();
            var innerStore = new NtfsContentStore(new ContentStoreOptions
            {
                RootPath = Path.Combine(rootPath, "objects"),
            });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IClock>(SystemClock.Instance);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(boundary);
            services.AddSingleton<IWriteCoordinator>(
                new BoundaryWriteCoordinator(boundary));
            services.AddSingleton<IContentStore>(
                new BoundaryContentStore(innerStore, boundary));
            services.AddSingleton<IPreprocessingService>(
                new BoundaryPreprocessingService(
                    new PreprocessingService(new PreprocessingOptions
                    {
                        MaxInputBytes = 2 * 1024 * 1024,
                        MaxPages = 4,
                        MaxDimensionPixels = 1_000,
                        MaxPixelsPerPage = 1_000_000,
                        MaxTotalPixels = 2_000_000,
                        ThumbnailMaxDimension = 64,
                    }),
                    boundary));
            services.AddSingleton<IOptions<SubmissionPreprocessingWorkerOptions>>(
                Options.Create(new SubmissionPreprocessingWorkerOptions
                {
                    LeaseDuration = TimeSpan.FromMinutes(5),
                    CropMarginMillionths = 5_000,
                }));
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<SubmissionPreprocessingWorker>();
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
                return new WorkerFixture(
                    rootPath,
                    connection,
                    provider,
                    boundary);
            }
            catch
            {
                await provider.DisposeAsync();
                await connection.DisposeAsync();
                Directory.Delete(rootPath, recursive: true);
                throw;
            }
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync()
        {
            return _services
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();
        }

        public async Task<SeededSubmission> SeedSubmissionAsync(
            bool validImage,
            bool includeAlignmentReference = false,
            bool unusableAlignmentReference = false,
            string alignmentSourceRole = "blank_test")
        {
            var now = DateTimeOffset.UtcNow;
            var content = validImage
                ? CreateSubmissionPng()
                : new byte[] { 1, 2, 3, 4, 5 };
            await using var contentStream = new MemoryStream(
                content,
                writable: false);
            var stored = await ContentStore.PutAsync(
                contentStream,
                ContentStorageClass.ManagedScanOriginal,
                "png");
            ContentWriteResult? alignmentStored = null;
            byte[]? alignmentBytes = null;
            if (includeAlignmentReference)
            {
                alignmentBytes = CreateTemplatePng(
                    unusableAlignmentReference);
                await using var alignmentStream = new MemoryStream(
                    alignmentBytes,
                    writable: false);
                alignmentStored = await ContentStore.PutAsync(
                    alignmentStream,
                    ContentStorageClass.TemplateSource,
                    "png");
            }

            var staffId = UlidId.New(now);
            var templateId = UlidId.New(now);
            var versionId = UlidId.New(now);
            var questionId = UlidId.New(now);
            var answerRegionId = UlidId.New(now);
            var nameRegionId = UlidId.New(now);
            var sessionId = UlidId.New(now);
            var submissionId = UlidId.New(now);
            var fileObjectId = UlidId.New(now);
            var retentionAnchor = now.AddMinutes(-5);
            var jobId = UlidId.New(now);

            await using var db = await CreateDbContextAsync();
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "Worker fixture",
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "published",
                PipelineVersion = "template-v1",
                PublishedByStaffUserId = staffId,
                PublishedAt = now,
                ContentHash = new string('a', 64),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Regions.AddRange(
                new RegionEntity
                {
                    Id = answerRegionId,
                    OwnerType = "question",
                    OwnerId = questionId,
                    PageNumber = 1,
                    RegionType = "answer",
                    XMillionths = 500_000,
                    YMillionths = 200_000,
                    WidthMillionths = 450_000,
                    HeightMillionths = 600_000,
                    RotationDegrees = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new RegionEntity
                {
                    Id = nameRegionId,
                    OwnerType = "question",
                    OwnerId = questionId,
                    PageNumber = 1,
                    RegionType = "name",
                    XMillionths = 25_000,
                    YMillionths = 50_000,
                    WidthMillionths = 350_000,
                    HeightMillionths = 200_000,
                    RotationDegrees = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            db.Questions.Add(new QuestionEntity
            {
                Id = questionId,
                TemplateVersionId = versionId,
                LogicalQuestionId = UlidId.New(now),
                OrderIndex = 0,
                DisplayLabel = "Q1",
                QuestionText = "Write an answer.",
                QuestionType = "exact_short_text",
                GradingMode = "transcribe_then_rules",
                MaxPointsMilli = 1_000,
                AnswerRegionId = answerRegionId,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = DateOnly.FromDateTime(now.UtcDateTime),
                Priority = "economy",
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = stored.Locator.Sha256,
                Bytes = stored.Locator.Bytes,
                VerifiedMime = "image/png",
                Extension = stored.Locator.Extension,
                RelativeObjectPath = stored.RelativePath,
                StorageClass =
                    ContentStorageClass.ManagedScanOriginal.ToString(),
                RetentionClass = "submitted_scan",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "needs_name_review",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                CanonicalForSession = false,
                UploadedByStaffUserId = staffId,
                OriginalFileName = "submission.png",
                OriginalFileObjectId = fileObjectId,
                UploadCompletedAt = retentionAnchor,
                QualitySummaryJson =
                    """{"pipeline":"safe-ingest-v1","status":"accepted"}""",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = UlidId.New(now),
                FileObjectId = fileObjectId,
                OwnerType = "submission",
                OwnerId = submissionId,
                Purpose = "original_scan",
                RetentionAnchorAt = retentionAnchor,
                CreatedAt = now,
            });
            if (alignmentStored is not null && alignmentBytes is not null)
            {
                var alignmentUploadId = UlidId.New(now);
                var alignmentFileObjectId = UlidId.New(now);
                var alignmentFileReferenceId = UlidId.New(now);
                db.UploadSessions.Add(new UploadSessionEntity
                {
                    Id = alignmentUploadId,
                    CreatedByStaffUserId = staffId,
                    Purpose = "template_source",
                    DestinationType = "template_source",
                    OriginalFileName = "blank-template.png",
                    DeclaredMimeType = "image/png",
                    ExpectedBytes = alignmentBytes.LongLength,
                    CurrentBytes = alignmentBytes.LongLength,
                    FinalSha256 = alignmentStored.Locator.Sha256,
                    IncomingRelativePath = "fixture/alignment-source",
                    State = "completed",
                    ExpiresAt = now.AddHours(1),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.FileObjects.Add(new FileObjectEntity
                {
                    Id = alignmentFileObjectId,
                    Sha256 = alignmentStored.Locator.Sha256,
                    Bytes = alignmentStored.Locator.Bytes,
                    VerifiedMime = "image/png",
                    Extension = alignmentStored.Locator.Extension,
                    RelativeObjectPath = alignmentStored.RelativePath,
                    StorageClass =
                        ContentStorageClass.TemplateSource.ToString(),
                    RetentionClass = "template_source",
                    ManagedScanBytes = false,
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 1,
                });
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = alignmentFileReferenceId,
                    FileObjectId = alignmentFileObjectId,
                    OwnerType = "upload_session",
                    OwnerId = alignmentUploadId,
                    Purpose = "template_source",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
                db.TemplateSources.Add(new TemplateSourceEntity
                {
                    Id = UlidId.New(now),
                    TemplateVersionId = versionId,
                    UploadSessionId = alignmentUploadId,
                    FileReferenceId = alignmentFileReferenceId,
                    SourceRole = alignmentSourceRole,
                    DisplayName = "blank-template.png",
                    Ordinal = 0,
                    UploadedByStaffUserId = staffId,
                    CreatedAt = now,
                });
            }

            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = SubmissionPreprocessingWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey =
                    $"submission:{submissionId}:preprocess",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new { submissionId }),
                State = "queued",
                MaxAttempts = 3,
                NextAttemptAt = now.AddMinutes(-1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();

            return new SeededSubmission(
                submissionId,
                jobId,
                questionId,
                answerRegionId,
                nameRegionId,
                stored.Locator.Sha256,
                stored.Locator.Bytes,
                retentionAnchor);
        }

        public async Task<string> SeedUnrelatedJobAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var id = UlidId.New(now);
            await using var db = await CreateDbContextAsync();
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = id,
                Type = "provider_free_grade",
                SchemaVersion = 1,
                DeduplicationKey = $"unrelated:{id}",
                Priority = 100,
                PayloadJson = "{}",
                State = "queued",
                MaxAttempts = 3,
                NextAttemptAt = now.AddMinutes(-1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task RequeueAsync(string jobId)
        {
            await using var db = await CreateDbContextAsync();
            var job = await db.BackgroundJobs
                .SingleAsync(item => item.Id == jobId);
            job.State = "queued";
            job.ProgressBasisPoints = 0;
            job.CompletedAt = null;
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }

        private static byte[] CreateSubmissionPng()
        {
            using var bitmap = new SKBitmap(new SKImageInfo(
                200,
                100,
                SKColorType.Rgba8888,
                SKAlphaType.Opaque));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            DrawTemplateStructure(canvas);
            using var namePaint = new SKPaint
            {
                Color = SKColors.DarkBlue,
                StrokeWidth = 3,
            };
            canvas.DrawLine(15, 12, 60, 18, namePaint);
            canvas.DrawLine(20, 20, 55, 8, namePaint);
            using var answerPaint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 5,
            };
            canvas.DrawLine(110, 35, 175, 65, answerPaint);
            canvas.DrawLine(115, 70, 180, 30, answerPaint);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static byte[] CreateTemplatePng(bool unusable)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(
                200,
                100,
                SKColorType.Rgba8888,
                SKAlphaType.Opaque));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            if (!unusable)
            {
                DrawTemplateStructure(canvas);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static void DrawTemplateStructure(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.DarkGray,
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke,
            };
            canvas.DrawRect(new SKRect(5, 5, 195, 95), paint);
            canvas.DrawRect(new SKRect(10, 8, 80, 28), paint);
            canvas.DrawRect(new SKRect(98, 20, 190, 85), paint);
            canvas.DrawLine(10, 34, 90, 34, paint);
            canvas.DrawLine(10, 48, 90, 48, paint);
            canvas.DrawLine(10, 62, 90, 62, paint);
            canvas.DrawCircle(105, 10, 4, paint);
        }
    }

    public sealed class BoundaryProbe
    {
        private readonly AsyncLocal<int> _writeDepth = new();
        private int _preprocessingCalls;
        private int _contentReadCalls;
        private int _contentWriteCalls;

        public int PreprocessingCalls => Volatile.Read(
            ref _preprocessingCalls);
        public int ContentReadCalls => Volatile.Read(ref _contentReadCalls);
        public int ContentWriteCalls => Volatile.Read(ref _contentWriteCalls);

        public IDisposable EnterWrite()
        {
            _writeDepth.Value++;
            return new Scope(this);
        }

        public void ObservePreprocessing()
        {
            AssertOutsideWrite();
            Interlocked.Increment(ref _preprocessingCalls);
        }

        public void ObserveContentRead()
        {
            AssertOutsideWrite();
            Interlocked.Increment(ref _contentReadCalls);
        }

        public void ObserveContentWrite()
        {
            AssertOutsideWrite();
            Interlocked.Increment(ref _contentWriteCalls);
        }

        private void AssertOutsideWrite()
        {
            if (_writeDepth.Value != 0)
            {
                throw new InvalidOperationException(
                    "Content or preprocessing work ran while the write lock was held.");
            }
        }

        private void ExitWrite()
        {
            _writeDepth.Value--;
        }

        private sealed class Scope(BoundaryProbe owner) : IDisposable
        {
            public void Dispose()
            {
                owner.ExitWrite();
            }
        }
    }

    private sealed class BoundaryWriteCoordinator(
        BoundaryProbe boundary) : IWriteCoordinator, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var scope = boundary.EnterWrite();
                await operation(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var scope = boundary.EnterWrite();
                return await operation(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    private sealed class BoundaryContentStore(
        IContentStore inner,
        BoundaryProbe boundary) : IContentStore
    {
        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            boundary.ObserveContentWrite();
            return inner.PutAsync(
                source,
                storageClass,
                verifiedExtension,
                cancellationToken);
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            boundary.ObserveContentRead();
            return inner.OpenReadAsync(locator, cancellationToken);
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            return inner.ExistsAsync(locator, cancellationToken);
        }

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            return inner.DeleteAsync(locator, cancellationToken);
        }
    }

    private sealed class BoundaryPreprocessingService(
        IPreprocessingService inner,
        BoundaryProbe boundary) : IPreprocessingService
    {
        public Task<PreprocessingResult> ProcessAsync(
            Stream source,
            PreprocessingInput input,
            CancellationToken cancellationToken = default)
        {
            boundary.ObservePreprocessing();
            return inner.ProcessAsync(source, input, cancellationToken);
        }

        public ImageArtifact Crop(
            PreprocessedPage page,
            MillionthsRegion region,
            int marginMillionths = 0)
        {
            boundary.ObservePreprocessing();
            return inner.Crop(page, region, marginMillionths);
        }

        public PageAlignmentResult AlignToReference(
            PreprocessedPage page,
            PreprocessedPage reference,
            CancellationToken cancellationToken = default)
        {
            boundary.ObservePreprocessing();
            return inner.AlignToReference(
                page,
                reference,
                cancellationToken);
        }
    }

    private sealed record SeededSubmission(
        string SubmissionId,
        string JobId,
        string QuestionId,
        string AnswerRegionId,
        string NameRegionId,
        string OriginalSha256,
        long OriginalBytes,
        DateTimeOffset RetentionAnchorAt);

    private sealed record Counts(
        int Pages,
        int Artifacts,
        int References,
        int Objects,
        int Audits,
        int OutboxEvents);
}

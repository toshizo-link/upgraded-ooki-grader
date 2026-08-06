using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class ProviderFreeVerticalSliceTests
{
    [Fact]
    public async Task GradingCreatesConservativeResultsAndHandlesRedelivery()
    {
        await using var fixture = await ProviderFreeWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedGradingJobAsync([1_500, 2_500]);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var submission = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.SubmissionId);
            var run = await db.GradingRuns
                .AsNoTracking()
                .Include(item => item.QuestionResults)
                    .ThenInclude(item => item.Revisions)
                .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
            var job = await db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.JobId);

            Assert.Equal("needs_grade_review", submission.State);
            Assert.Equal(run.Id, submission.CurrentGradingRunId);
            Assert.Null(submission.FinalizedAt);
            Assert.Equal("needs_grade_review", run.State);
            Assert.Equal("provider-free-unreadable-v1", run.PipelineVersion);
            Assert.Equal(seeded.ManifestHash, run.CanonicalInputManifestHash);
            Assert.Equal(0, run.EarnedPointsMilli);
            Assert.Equal(4_000, run.PossiblePointsMilli);
            Assert.Null(run.FinalizedAt);
            Assert.Equal("succeeded", job.State);
            Assert.Equal(10_000, job.ProgressBasisPoints);

            var results = run.QuestionResults
                .OrderBy(item => item.QuestionId, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(seeded.QuestionMaximums.Count, results.Length);
            Assert.Equal(
                seeded.QuestionMaximums.Keys.Order(StringComparer.Ordinal),
                results.Select(item => item.QuestionId));
            Assert.Equal(
                run.PossiblePointsMilli,
                results.Sum(item => item.MaximumPointsMilli));

            foreach (var result in results)
            {
                Assert.Equal(0, result.ProposedPointsMilli);
                Assert.Equal(
                    seeded.QuestionMaximums[result.QuestionId],
                    result.MaximumPointsMilli);
                Assert.Equal("unreadable", result.Outcome);
                Assert.Equal("manual", result.Method);
                Assert.Equal(0, result.ConfidenceBasisPoints);
                Assert.Equal("not_applicable", result.KanjiCheck);
                Assert.Equal(
                    "provider_free_no_transcription",
                    result.ReasonCode);
                Assert.True(result.ReviewRequired);
                Assert.Equal("pending", result.ReviewStatus);
                Assert.Null(result.TranscribedAnswer);
                Assert.Null(result.NormalizedAnswer);
                Assert.Null(result.Explanation);

                var revision = Assert.Single(result.Revisions);
                Assert.Equal(result.CurrentRevisionId, revision.Id);
                Assert.Equal(1, revision.RevisionNumber);
                Assert.Equal(0, revision.AwardedPointsMilli);
                Assert.Equal("unreadable", revision.Outcome);
                Assert.Equal("initial", revision.Source);
                Assert.Equal(
                    "provider_free_no_transcription",
                    revision.ReasonCode);
            }

            Assert.Equal(
                run.EarnedPointsMilli,
                results.Sum(item => item.Revisions.Single().AwardedPointsMilli));
            Assert.Contains(
                await db.AuditEvents.AsNoTracking().ToListAsync(),
                item => item.EventType == "grading.provider_free_created"
                    && item.ObjectId == seeded.SubmissionId);
            Assert.Equal(
                2,
                await db.OutboxEvents
                    .AsNoTracking()
                    .CountAsync(item => item.AggregateId == seeded.SubmissionId));
        }

        await fixture.RequeueAsync(seeded.JobId);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());

        await using (var db = await fixture.CreateDbContextAsync())
        {
            Assert.Equal(
                1,
                await db.GradingRuns
                    .AsNoTracking()
                    .CountAsync(item => item.SubmissionId == seeded.SubmissionId));
            Assert.Equal(
                seeded.QuestionMaximums.Count,
                await db.QuestionResults.AsNoTracking().CountAsync());
            Assert.Equal(
                seeded.QuestionMaximums.Count,
                await db.ResultRevisions.AsNoTracking().CountAsync());
            Assert.Equal(
                1,
                await db.AuditEvents
                    .AsNoTracking()
                    .CountAsync(
                        item => item.EventType
                            == "grading.provider_free_created"));
            Assert.Equal(
                2,
                await db.OutboxEvents
                    .AsNoTracking()
                    .CountAsync(item => item.AggregateId == seeded.SubmissionId));

            var job = await db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.JobId);
            Assert.Equal("succeeded", job.State);
            Assert.Equal(2, job.AttemptCount);
            Assert.Null(job.ErrorCode);
        }
    }

    private sealed class ProviderFreeWorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        private ProviderFreeWorkerFixture(
            SqliteConnection connection,
            ServiceProvider services)
        {
            _connection = connection;
            _services = services;
            Worker = services.GetRequiredService<ProviderFreeJobWorker>();
        }

        public ProviderFreeJobWorker Worker { get; }

        public static async Task<ProviderFreeWorkerFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IClock>(SystemClock.Instance);
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<IWriteCoordinator, SemaphoreWriteCoordinator>();
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<ProviderFreeJobWorker>();
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
                return new ProviderFreeWorkerFixture(connection, provider);
            }
            catch
            {
                await provider.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync()
        {
            return _services
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();
        }

        public async Task<SeededWorkflow> SeedPreprocessJobAsync()
        {
            await using var db = await CreateDbContextAsync();
            var graph = CreateSubmissionGraph(
                submissionState: "validating",
                assignedStudent: false,
                questionMaximums: [1_000]);
            db.AddRange(
                graph.Template,
                graph.Version,
                graph.Questions[0],
                graph.Session,
                graph.FileObject,
                graph.Submission);

            var job = CreateJob(
                graph.Now,
                "submission.preprocess",
                $"submission:{graph.Submission.Id}:preprocess",
                JsonSerializer.Serialize(new
                {
                    submissionId = graph.Submission.Id,
                }));
            db.BackgroundJobs.Add(job);
            await db.SaveChangesAsync();

            return new SeededWorkflow(
                graph.Submission.Id,
                job.Id,
                string.Empty,
                graph.Questions.ToDictionary(
                    item => item.Id,
                    item => item.MaxPointsMilli,
                    StringComparer.Ordinal));
        }

        public async Task<SeededWorkflow> SeedGradingJobAsync(
            IReadOnlyList<long> questionMaximums)
        {
            await using var db = await CreateDbContextAsync();
            var graph = CreateSubmissionGraph(
                submissionState: "grading",
                assignedStudent: true,
                questionMaximums);
            db.Add(graph.Template);
            db.Add(graph.Version);
            db.AddRange(graph.Questions);
            db.Add(graph.Session);
            db.Add(graph.Student!);
            db.Add(graph.FileObject);
            db.Add(graph.Submission);

            var manifestHash = ProviderFreeJobWorker.ComputeManifestHash(
                graph.Submission,
                graph.Version,
                graph.Questions);
            var job = CreateJob(
                graph.Now,
                "provider_free_grade",
                $"submission:{graph.Submission.Id}:provider-free:{manifestHash}",
                JsonSerializer.Serialize(new
                {
                    submissionId = graph.Submission.Id,
                    templateVersionId = graph.Version.Id,
                    manifestHash,
                }));
            db.BackgroundJobs.Add(job);
            await db.SaveChangesAsync();

            return new SeededWorkflow(
                graph.Submission.Id,
                job.Id,
                manifestHash,
                graph.Questions.ToDictionary(
                    item => item.Id,
                    item => item.MaxPointsMilli,
                    StringComparer.Ordinal));
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

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static SubmissionGraph CreateSubmissionGraph(
            string submissionState,
            bool assignedStudent,
            IReadOnlyList<long> questionMaximums)
        {
            var now = DateTimeOffset.UtcNow;
            var staffId = UlidId.New(now);
            var template = new TestTemplateEntity
            {
                Id = UlidId.New(now),
                Title = "Provider-free integration template",
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var version = new TemplateVersionEntity
            {
                Id = UlidId.New(now),
                TestTemplateId = template.Id,
                VersionNumber = 1,
                State = "published",
                TargetTotalPointsMilli = questionMaximums.Sum(),
                PipelineVersion = "manual-template-v1",
                PublishedByStaffUserId = staffId,
                PublishedAt = now,
                ContentHash = new string('a', 64),
                CreatedAt = now,
                UpdatedAt = now,
            };
            var questions = questionMaximums
                .Select((maximum, index) => new QuestionEntity
                {
                    Id = UlidId.New(now),
                    TemplateVersionId = version.Id,
                    LogicalQuestionId = UlidId.New(now),
                    OrderIndex = index,
                    DisplayLabel = $"Question {index + 1}",
                    QuestionText = $"Integration question {index + 1}",
                    QuestionType = "exact_short_text",
                    GradingMode = "manual",
                    MaxPointsMilli = maximum,
                    TeacherVerified = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                })
                .ToArray();
            var session = new TestSessionEntity
            {
                Id = UlidId.New(now),
                TemplateVersionId = version.Id,
                TestDate = DateOnly.FromDateTime(now.UtcDateTime),
                Priority = "economy",
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var student = assignedStudent
                ? new StudentEntity
                {
                    Id = UlidId.New(now),
                    StudentNumber = "S-001",
                    StudentNumberNormalized = "s-001",
                    FamilyName = "Test",
                    GivenName = "Student",
                    FamilyNameNormalized = "test",
                    GivenNameNormalized = "student",
                    DisplayName = "Test Student",
                    Status = "active",
                    CreatedAt = now,
                    UpdatedAt = now,
                }
                : null;
            var fileObject = new FileObjectEntity
            {
                Id = UlidId.New(now),
                Sha256 = new string('b', 64),
                Bytes = 1_024,
                VerifiedMime = "application/pdf",
                Extension = ".pdf",
                RelativeObjectPath = "objects/provider-free-test.pdf",
                StorageClass = "managed_scan",
                RetentionClass = "submission_scan",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
            };
            var submission = new SubmissionEntity
            {
                Id = UlidId.New(now),
                TestSessionId = session.Id,
                State = submissionState,
                ScanPayloadState = "scan_available",
                AssignedStudentId = student?.Id,
                AssignmentMethod = student is null ? "none" : "teacher",
                AttemptNumber = 1,
                CanonicalForSession = student is not null,
                UploadedByStaffUserId = staffId,
                OriginalFileName = "completed-test.pdf",
                OriginalFileObjectId = fileObject.Id,
                UploadCompletedAt = now,
                QualitySummaryJson =
                    """{"pipeline":"safe-ingest-v1","status":"accepted"}""",
                CreatedAt = now,
                UpdatedAt = now,
            };

            return new SubmissionGraph(
                now,
                template,
                version,
                questions,
                session,
                student,
                fileObject,
                submission);
        }

        private static BackgroundJobEntity CreateJob(
            DateTimeOffset now,
            string type,
            string deduplicationKey,
            string payloadJson)
        {
            return new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = type,
                SchemaVersion = 1,
                DeduplicationKey = deduplicationKey,
                Priority = 0,
                PayloadJson = payloadJson,
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now.AddMinutes(-1),
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
    }

    private sealed record SeededWorkflow(
        string SubmissionId,
        string JobId,
        string ManifestHash,
        IReadOnlyDictionary<string, long> QuestionMaximums);

    private sealed record SubmissionGraph(
        DateTimeOffset Now,
        TestTemplateEntity Template,
        TemplateVersionEntity Version,
        QuestionEntity[] Questions,
        TestSessionEntity Session,
        StudentEntity? Student,
        FileObjectEntity FileObject,
        SubmissionEntity Submission);

}

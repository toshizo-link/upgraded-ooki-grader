using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed partial class ProviderFreeJobWorker(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    TimeProvider timeProvider,
    ILogger<ProviderFreeJobWorker> logger) : BackgroundService
{
    private const string GradingJobType = "provider_free_grade";
    private const string GradingPipelineVersion = "provider-free-unreadable-v1";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
        };
    private readonly string _workerId = $"provider-free-{Guid.NewGuid():N}";

    public static string ComputeManifestHash(
        SubmissionEntity submission,
        TemplateVersionEntity templateVersion,
        IEnumerable<QuestionEntity> questions)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(templateVersion);
        ArgumentNullException.ThrowIfNull(questions);

        var canonical = new StringBuilder();
        AppendManifestValue(canonical, "submission", submission.Id);
        AppendManifestValue(
            canonical,
            "identity",
            submission.AssignedStudentId
                ?? (IsExplicitlyUnidentified(submission)
                    ? "unidentified"
                    : string.Empty));
        AppendManifestValue(
            canonical,
            "scan",
            submission.OriginalFileObjectId ?? string.Empty);
        AppendManifestValue(canonical, "template", templateVersion.Id);
        AppendManifestValue(
            canonical,
            "template-content",
            templateVersion.ContentHash ?? string.Empty);
        AppendManifestValue(
            canonical,
            "template-pipeline",
            templateVersion.PipelineVersion);

        foreach (var question in questions
                     .OrderBy(item => item.OrderIndex)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendManifestValue(canonical, "question", question.Id);
            AppendManifestValue(
                canonical,
                "question-order",
                question.OrderIndex.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifestValue(
                canonical,
                "question-maximum",
                question.MaxPointsMilli.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifestValue(
                canonical,
                "question-revision",
                question.Revision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var lease = await LeaseNextAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        try
        {
            await ProcessGradingAsync(lease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JobHandlingException exception)
        {
            LogJobFailure(
                lease.Id,
                lease.Type,
                exception.ErrorCode,
                exception.GetType().Name);
            await RecordFailureAsync(
                    lease,
                    exception.ErrorCode,
                    exception.IsPermanent,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogJobFailure(
                lease.Id,
                lease.Type,
                "provider_free_worker_error",
                exception.GetType().Name);
            await RecordFailureAsync(
                    lease,
                    "provider_free_worker_error",
                    isPermanent: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private Task<JobLease?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs
                .Where(item =>
                    item.Type == GradingJobType
                    && item.AttemptCount < item.MaxAttempts
                    && ((item.State == "queued" && item.NextAttemptAt <= now)
                        || (item.State == "retry_waiting"
                            && item.NextAttemptAt <= now)
                        || (item.State == "leased"
                            && item.LeaseExpiresAt <= now)))
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.NextAttemptAt)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (job is null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            job.State = "leased";
            job.LeaseOwner = _workerId;
            job.LeaseExpiresAt = now.Add(LeaseDuration);
            job.AttemptCount = checked(job.AttemptCount + 1);
            job.StartedAt ??= now;
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new JobLease(
                job.Id,
                job.Type,
                job.SchemaVersion,
                job.PayloadJson,
                job.Revision);
        }, cancellationToken);
    }

    private Task ProcessPreprocessAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token).ConfigureAwait(false);
            var payload = DeserializePayload<PreprocessPayload>(
                lease.PayloadJson,
                "preprocess_payload_invalid");
            if (string.IsNullOrWhiteSpace(payload.SubmissionId))
            {
                throw Permanent("preprocess_payload_invalid");
            }

            var submission = await db.Submissions
                .SingleOrDefaultAsync(
                    item => item.Id == payload.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("preprocess_submission_missing");

            if (IsAtOrBeyondNameReview(submission.State))
            {
                CompleteJob(job, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            if (submission.State is not ("validating" or "preprocessing")
                || submission.ScanPayloadState != "scan_available"
                || submission.OriginalFileObjectId is null
                || !QualityWasAccepted(submission.QualitySummaryJson))
            {
                throw Permanent("preprocess_input_invalid");
            }

            var fileAvailable = await db.FileObjects
                .AsNoTracking()
                .AnyAsync(
                    item => item.Id == submission.OriginalFileObjectId
                        && item.State == "available",
                    token)
                .ConfigureAwait(false);
            if (!fileAvailable)
            {
                throw Permanent("preprocess_scan_unavailable");
            }

            var now = timeProvider.GetUtcNow();
            submission.State = "needs_name_review";
            AddSystemAudit(
                db,
                now,
                job.CorrelationId,
                "submission.preprocessed",
                submission.Id,
                "safe_ingest_accepted");
            AddStatusOutbox(
                db,
                now,
                job.CorrelationId,
                submission.Id,
                submission.State);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task ProcessGradingAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token).ConfigureAwait(false);
            var payload = DeserializePayload<GradingPayload>(
                lease.PayloadJson,
                "grading_payload_invalid");
            if (string.IsNullOrWhiteSpace(payload.SubmissionId)
                || string.IsNullOrWhiteSpace(payload.TemplateVersionId)
                || !IsSha256(payload.ManifestHash))
            {
                throw Permanent("grading_payload_invalid");
            }

            var submission = await db.Submissions
                .Include(item => item.TestSession)
                    .ThenInclude(session => session.TemplateVersion)
                        .ThenInclude(version => version.Questions)
                .Include(item => item.GradingRuns)
                    .ThenInclude(run => run.QuestionResults)
                        .ThenInclude(result => result.Revisions)
                .SingleOrDefaultAsync(
                    item => item.Id == payload.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("grading_submission_missing");

            var existingRun = submission.GradingRuns.SingleOrDefault(
                run => run.PipelineVersion == GradingPipelineVersion
                    && run.CanonicalInputManifestHash == payload.ManifestHash);
            if (existingRun is not null)
            {
                if (submission.CurrentGradingRunId != existingRun.Id)
                {
                    throw Permanent("grading_run_conflict");
                }

                CompleteJob(job, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var version = submission.TestSession.TemplateVersion;
            var questions = version.Questions
                .OrderBy(item => item.OrderIndex)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            ValidateGradingInput(submission, version, questions, payload);

            var scanAvailable = await db.FileObjects
                .AsNoTracking()
                .AnyAsync(
                    item => item.Id == submission.OriginalFileObjectId
                        && item.State == "available",
                    token)
                .ConfigureAwait(false);
            if (!scanAvailable)
            {
                throw Permanent("grading_scan_unavailable");
            }

            var now = timeProvider.GetUtcNow();
            var possiblePoints = questions.Aggregate(
                0L,
                static (total, question) =>
                    checked(total + question.MaxPointsMilli));
            var run = new GradingRunEntity
            {
                Id = UlidId.New(now),
                SubmissionId = submission.Id,
                RunNumber = checked(
                    submission.GradingRuns.Select(item => item.RunNumber)
                        .DefaultIfEmpty()
                        .Max() + 1),
                TemplateVersionId = version.Id,
                Reason = "provider_free_initial",
                State = "needs_grade_review",
                PipelineVersion = GradingPipelineVersion,
                CanonicalInputManifestHash = payload.ManifestHash,
                EarnedPointsMilli = 0,
                PossiblePointsMilli = possiblePoints,
                ResultSourceRevision = 1,
                CreatedAt = now,
                FinishedAt = now,
            };
            db.GradingRuns.Add(run);

            var results = new List<QuestionResultEntity>(questions.Length);
            foreach (var question in questions)
            {
                var result = new QuestionResultEntity
                {
                    Id = UlidId.New(now),
                    GradingRunId = run.Id,
                    QuestionId = question.Id,
                    ProposedPointsMilli = 0,
                    MaximumPointsMilli = question.MaxPointsMilli,
                    Outcome = "unreadable",
                    Method = "manual",
                    ConfidenceBasisPoints = 0,
                    KanjiCheck = "not_applicable",
                    ReasonCode = "provider_free_no_transcription",
                    ReviewRequired = true,
                    ReviewStatus = "pending",
                    CreatedAt = now,
                };
                db.QuestionResults.Add(result);
                results.Add(result);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            foreach (var result in results)
            {
                var revision = new ResultRevisionEntity
                {
                    Id = UlidId.New(now),
                    QuestionResultId = result.Id,
                    RevisionNumber = 1,
                    AwardedPointsMilli = 0,
                    Outcome = "unreadable",
                    ReasonCode = "provider_free_no_transcription",
                    Source = "initial",
                    CreatedAt = now,
                };
                db.ResultRevisions.Add(revision);
                result.CurrentRevisionId = revision.Id;
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            submission.CurrentGradingRunId = run.Id;
            submission.State = "needs_grade_review";
            AddSystemAudit(
                db,
                now,
                job.CorrelationId,
                "grading.provider_free_created",
                submission.Id,
                "provider_free_no_transcription");
            AddOutbox(
                db,
                now,
                job.CorrelationId,
                submission.Id,
                "grading.runCreated",
                JsonSerializer.Serialize(new
                {
                    submissionId = submission.Id,
                    gradingRunId = run.Id,
                    resultSourceRevision = run.ResultSourceRevision,
                }));
            AddStatusOutbox(
                db,
                now,
                job.CorrelationId,
                submission.Id,
                submission.State);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordFailureAsync(
        JobLease lease,
        string errorCode,
        bool isPermanent,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs
                .SingleOrDefaultAsync(item => item.Id == lease.Id, token)
                .ConfigureAwait(false);
            if (job is null
                || job.State != "leased"
                || job.LeaseOwner != _workerId
                || job.Revision != lease.Revision)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.ErrorCode = errorCode;
            job.SafeErrorDetail =
                "The provider-free worker could not process this job.";
            if (!isPermanent && job.AttemptCount < job.MaxAttempts)
            {
                job.State = "retry_waiting";
                job.NextAttemptAt = now.Add(RetryDelay(job.AttemptCount));
            }
            else
            {
                job.State = "failed";
                job.CompletedAt = now;
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == lease.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("job_missing");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.Revision != lease.Revision
            || job.LeaseExpiresAt <= timeProvider.GetUtcNow())
        {
            throw Permanent("job_lease_lost");
        }

        if (job.SchemaVersion != 1 || lease.SchemaVersion != 1)
        {
            throw Permanent("job_schema_unsupported");
        }

        return job;
    }

    private static void ValidateGradingInput(
        SubmissionEntity submission,
        TemplateVersionEntity version,
        QuestionEntity[] questions,
        GradingPayload payload)
    {
        if ((submission.AssignedStudentId is null
                && !IsExplicitlyUnidentified(submission))
            || submission.CurrentGradingRunId is not null
            || submission.State is not ("grading" or "awaiting_grading")
            || submission.ScanPayloadState != "scan_available"
            || submission.OriginalFileObjectId is null)
        {
            throw Permanent("grading_submission_state_invalid");
        }

        if (version.Id != payload.TemplateVersionId
            || version.Id != submission.TestSession.TemplateVersionId
            || !TemplateVersionUsePolicy.IsImmutablePublishedSnapshot(version.State)
            || questions.Length == 0
            || questions.Any(question => question.MaxPointsMilli < 0)
            || questions.Select(question => question.Id).Distinct(
                    StringComparer.Ordinal)
                .Count() != questions.Length)
        {
            throw Permanent("grading_template_invalid");
        }

        var actualManifest = ComputeManifestHash(submission, version, questions);
        if (!string.Equals(
                actualManifest,
                payload.ManifestHash,
                StringComparison.Ordinal))
        {
            throw Permanent("grading_manifest_mismatch");
        }
    }

    private static T DeserializePayload<T>(string json, string errorCode)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(
                       json,
                       PayloadSerializerOptions)
                   ?? throw Permanent(errorCode);
        }
        catch (JsonException)
        {
            throw Permanent(errorCode);
        }
    }

    private static bool QualityWasAccepted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("pipeline", out var pipeline)
                && pipeline.ValueEquals("safe-ingest-v1")
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueEquals("accepted");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAtOrBeyondNameReview(string state)
    {
        return state is "needs_name_review"
            or "awaiting_grading"
            or "grading"
            or "needs_grade_review"
            or "ready_to_finalize"
            or "finalized";
    }

    private static bool IsExplicitlyUnidentified(SubmissionEntity submission)
    {
        return submission.AssignedStudentId is null
            && submission.AssignmentMethod == "none"
            && submission.AssignmentEvidenceJson
                == """{"disposition":"unidentified"}""";
    }

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(character =>
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }

    private static TimeSpan RetryDelay(int attemptCount)
    {
        return attemptCount switch
        {
            <= 1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(2),
            3 => TimeSpan.FromMinutes(10),
            4 => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(2),
        };
    }

    private static void CompleteJob(
        BackgroundJobEntity job,
        DateTimeOffset completedAt)
    {
        job.State = "succeeded";
        job.ProgressBasisPoints = 10_000;
        job.CompletedAt = completedAt;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = null;
        job.SafeErrorDetail = null;
    }

    private static void AddSystemAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string eventType,
        string submissionId,
        string reasonCode)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            EventType = eventType,
            ObjectType = "submission",
            ObjectId = submissionId,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
        });
    }

    private static void AddStatusOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string submissionId,
        string state)
    {
        AddOutbox(
            db,
            now,
            correlationId,
            submissionId,
            "submission.status",
            JsonSerializer.Serialize(new { submissionId, state }));
    }

    private static void AddOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string submissionId,
        string eventType,
        string payloadJson)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "submission",
            AggregateId = submissionId,
            EventType = eventType,
            SchemaVersion = 1,
            PayloadJson = payloadJson,
            CorrelationId = correlationId,
            OccurredAt = now,
        });
    }

    private static void AppendManifestValue(
        StringBuilder builder,
        string name,
        string value)
    {
        builder.Append(name);
        builder.Append(':');
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }

    private static JobHandlingException Permanent(string errorCode) =>
        new(errorCode, isPermanent: true);

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Warning,
        Message =
            "Provider-free job {JobId} of type {JobType} failed with " +
            "{ErrorCode} ({ExceptionType}).")]
    private partial void LogJobFailure(
        string jobId,
        string jobType,
        string errorCode,
        string exceptionType);

    private sealed record JobLease(
        string Id,
        string Type,
        int SchemaVersion,
        string PayloadJson,
        long Revision);

    private sealed record PreprocessPayload(string SubmissionId);

    private sealed record GradingPayload(
        string SubmissionId,
        string TemplateVersionId,
        string ManifestHash);

    private sealed class JobHandlingException(
        string errorCode,
        bool isPermanent) : Exception
    {
        public string ErrorCode { get; } = errorCode;
        public bool IsPermanent { get; } = isPermanent;
    }
}

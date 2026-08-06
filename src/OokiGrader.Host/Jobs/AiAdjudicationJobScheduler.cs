using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Grading;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed record AiAdjudicationJobWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumMediaBytes { get; init; } = 8 * 1024 * 1024;
    public int EstimatedImageTokensPerTile { get; init; } = 2_048;
    public int MinimumConfidenceBasisPoints { get; init; } = 8_000;

    internal void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(100)
            || PollInterval > TimeSpan.FromMinutes(1)
            || LeaseDuration < TimeSpan.FromMinutes(2)
            || LeaseDuration > TimeSpan.FromHours(1)
            || MaximumMediaBytes is < 1_024 or > 18 * 1024 * 1024
            || EstimatedImageTokensPerTile is < 256 or > 32_768
            || MinimumConfidenceBasisPoints is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AiAdjudicationJobWorkerOptions),
                "One or more adjudication worker options are invalid.");
        }
    }
}

/// <summary>
/// Adds one durable recheck job per ambiguous initial result. The scheduler is
/// registered only when adjudication is enabled, so its optional injection into
/// the initial-grading worker is also the enqueue feature gate.
/// </summary>
public sealed class AiAdjudicationJobScheduler
{
    private readonly AiAdjudicationJobWorkerOptions _options;
    private readonly IAiProviderFeaturePolicy _providerFeaturePolicy;

    public AiAdjudicationJobScheduler(
        IOptions<AiAdjudicationJobWorkerOptions> options,
        IAiProviderFeaturePolicy? providerFeaturePolicy = null)
    {
        _options = options.Value;
        _providerFeaturePolicy = providerFeaturePolicy
            ?? AiProviderFeaturePolicy.AllowAll;
        _options.Validate();
    }

    public async Task<int> EnqueueAmbiguousAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        GradingRunEntity run,
        IReadOnlyCollection<QuestionResultEntity> results,
        ValidatedAiGradingResponse response,
        IReadOnlyCollection<AiAdjudicationArtifactCandidate> artifacts,
        string? correlationId,
        string causationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(artifacts);

        var activeProfile = await db.AiTaskProfiles
            .AsNoTracking()
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(
                item => item.TaskType == AiTaskTypes.Adjudication
                    && item.Active,
                cancellationToken)
            .ConfigureAwait(false);
        if (activeProfile is null
            || !_providerFeaturePolicy.IsEnabled(
                activeProfile.AiConnection.Provider))
        {
            return 0;
        }

        var observations = response.Observations.ToDictionary(
            item => item.QuestionId,
            StringComparer.Ordinal);
        var cropCountByQuestion = artifacts
            .Where(item => item.ProviderDisclosureAllowed)
            .GroupBy(item => item.QuestionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
        var added = 0;
        foreach (var result in results
                     .OrderBy(item => item.QuestionId, StringComparer.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            observations.TryGetValue(result.QuestionId, out var observation);
            if (!NeedsAdjudication(result, observation, response.UnexpectedContent)
                || !cropCountByQuestion.TryGetValue(result.QuestionId, out var cropCount)
                || cropCount != 1
                || string.IsNullOrWhiteSpace(result.CurrentRevisionId))
            {
                continue;
            }

            var deduplicationKey =
                $"question-result:{result.Id}:adjudication:{result.CurrentRevisionId}";
            var exists = db.BackgroundJobs.Local.Any(
                    item => item.DeduplicationKey == deduplicationKey)
                || await db.BackgroundJobs
                    .AsNoTracking()
                    .AnyAsync(
                        item => item.DeduplicationKey == deduplicationKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (exists)
            {
                continue;
            }

            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = AiAdjudicationJobWorker.JobType,
                SchemaVersion = AiAdjudicationJobWorker.JobSchemaVersion,
                DeduplicationKey = deduplicationKey,
                Priority = submission.TestSession.Priority == "expedite" ? 100 : 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    submissionId = submission.Id,
                    gradingRunId = run.Id,
                    questionResultId = result.Id,
                    sourceRevisionId = result.CurrentRevisionId,
                }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CorrelationId = correlationId,
                CausationId = causationId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            added++;
        }

        return added;
    }

    private bool NeedsAdjudication(
        QuestionResultEntity result,
        ValidatedAiQuestionObservation? observation,
        bool unexpectedContent)
    {
        if (!result.ReviewRequired || result.ReviewStatus != "pending")
        {
            return false;
        }

        return unexpectedContent
            || observation is null
            || observation.ProviderReviewRecommended
            || observation.Observation.Quality != AnswerQuality.Clear
            || observation.ProposedOutcome is "review" or "unreadable"
            || observation.ProviderConfidenceBasisPoints
                < _options.MinimumConfidenceBasisPoints;
    }
}

public sealed record AiAdjudicationArtifactCandidate(
    string QuestionId,
    bool ProviderDisclosureAllowed);

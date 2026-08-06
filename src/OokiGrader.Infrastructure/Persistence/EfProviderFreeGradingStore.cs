using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Grading;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Persistence;

public sealed class EfProviderFreeGradingStore(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IClock clock) : IProviderFreeGradingStore
{
    public Task<ProviderFreeGradingRunSnapshot> CreateAsync(
        ProviderFreeGradingRunDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var totals = ProviderFreeGradingValidator.Validate(draft.Questions, draft.Judgments);

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);

            var existing = await LoadSnapshotAsync(dbContext, draft.GradingRunId, token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.SubmissionId != draft.SubmissionId ||
                    existing.TemplateVersionId != draft.TemplateVersionId)
                {
                    throw new InvalidOperationException(
                        "The grading run ID was reused for a different input.");
                }

                return existing;
            }

            var submission = await dbContext.Submissions
                .Include(entity => entity.TestSession)
                .SingleOrDefaultAsync(entity => entity.Id == draft.SubmissionId, token)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"Submission '{draft.SubmissionId}' does not exist.");

            if (submission.TestSession.TemplateVersionId != draft.TemplateVersionId)
            {
                throw new InvalidOperationException(
                    "The grading run template does not match the submission session.");
            }

            var questionIds = draft.Questions.Select(question => question.QuestionId).ToArray();
            var persistedQuestions = await dbContext.Questions
                .Where(question =>
                    question.TemplateVersionId == draft.TemplateVersionId &&
                    questionIds.Contains(question.Id))
                .ToDictionaryAsync(question => question.Id, StringComparer.Ordinal, token)
                .ConfigureAwait(false);

            if (persistedQuestions.Count != draft.Questions.Count)
            {
                throw new InvalidOperationException(
                    "Every grading question must belong to the exact template version.");
            }

            foreach (var definition in draft.Questions)
            {
                if (persistedQuestions[definition.QuestionId].MaxPointsMilli !=
                    definition.MaxPointsMilli)
                {
                    throw new InvalidOperationException(
                        $"Question '{definition.QuestionId}' maximum does not match its snapshot.");
                }
            }

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = clock.UtcNow;
            var run = new GradingRunEntity
            {
                Id = draft.GradingRunId,
                SubmissionId = draft.SubmissionId,
                RunNumber = draft.RunNumber,
                TemplateVersionId = draft.TemplateVersionId,
                Reason = draft.Reason,
                State = "ready_to_finalize",
                PipelineVersion = "provider-free-v1",
                CanonicalInputManifestHash = draft.CanonicalInputManifestHash,
                EarnedPointsMilli = totals.EarnedPointsMilli,
                PossiblePointsMilli = totals.PossiblePointsMilli,
                ResultSourceRevision = 1,
                CreatedAt = now,
                FinishedAt = now
            };
            dbContext.GradingRuns.Add(run);

            var resultRows = new List<(QuestionResultEntity Result, QuestionJudgment Judgment)>();
            foreach (var judgment in draft.Judgments)
            {
                var definition = persistedQuestions[judgment.QuestionId];
                var result = new QuestionResultEntity
                {
                    Id = UlidId.New(now),
                    GradingRunId = run.Id,
                    QuestionId = judgment.QuestionId,
                    TranscribedAnswer = judgment.TranscribedAnswer,
                    NormalizedAnswer = judgment.NormalizedAnswer,
                    ProposedPointsMilli = judgment.AwardedPointsMilli,
                    MaximumPointsMilli = definition.MaxPointsMilli,
                    Outcome = judgment.Outcome,
                    Method = judgment.Method,
                    ConfidenceBasisPoints = judgment.ConfidenceBasisPoints,
                    ReasonCode = judgment.ReasonCode,
                    ReviewRequired = false,
                    ReviewStatus = "not_required",
                    CreatedAt = now
                };
                dbContext.QuestionResults.Add(result);
                resultRows.Add((result, judgment));
            }

            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            foreach (var pair in resultRows)
            {
                var revision = new ResultRevisionEntity
                {
                    Id = UlidId.New(now),
                    QuestionResultId = pair.Result.Id,
                    RevisionNumber = 1,
                    AwardedPointsMilli = pair.Judgment.AwardedPointsMilli,
                    Outcome = pair.Judgment.Outcome,
                    ReasonCode = pair.Judgment.ReasonCode,
                    Source = "initial",
                    CreatedAt = now
                };
                dbContext.ResultRevisions.Add(revision);
                pair.Result.CurrentRevisionId = revision.Id;
            }

            submission.CurrentGradingRunId = run.Id;
            submission.State = "ready_to_finalize";
            dbContext.OutboxEvents.Add(new OutboxEventEntity
            {
                Id = UlidId.New(now),
                AggregateType = "submission",
                AggregateId = submission.Id,
                EventType = "grading.runCreated",
                SchemaVersion = 1,
                PayloadJson = $$"""{"submissionId":"{{submission.Id}}","gradingRunId":"{{run.Id}}"}""",
                OccurredAt = now
            });

            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);

            return await LoadSnapshotAsync(dbContext, run.Id, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The committed grading run was not found.");
        }, cancellationToken);
    }

    public async Task<ProviderFreeGradingRunSnapshot?> GetAsync(
        string gradingRunId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await LoadSnapshotAsync(dbContext, gradingRunId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ProviderFreeGradingRunSnapshot?> LoadSnapshotAsync(
        OokiGraderDbContext dbContext,
        string gradingRunId,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.GradingRuns
            .AsNoTracking()
            .Include(entity => entity.QuestionResults)
                .ThenInclude(entity => entity.Revisions)
            .SingleOrDefaultAsync(entity => entity.Id == gradingRunId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var judgments = run.QuestionResults
            .OrderBy(result => result.QuestionId, StringComparer.Ordinal)
            .Select(result =>
            {
                var revision = result.Revisions.SingleOrDefault(
                    candidate => candidate.Id == result.CurrentRevisionId);
                return new QuestionJudgment(
                    result.QuestionId,
                    revision?.AwardedPointsMilli ?? result.ProposedPointsMilli,
                    revision?.Outcome ?? result.Outcome,
                    result.Method,
                    result.ConfidenceBasisPoints,
                    result.TranscribedAnswer,
                    result.NormalizedAnswer,
                    revision?.ReasonCode ?? result.ReasonCode);
            })
            .ToArray();

        return new ProviderFreeGradingRunSnapshot(
            run.Id,
            run.SubmissionId,
            run.TemplateVersionId,
            run.RunNumber,
            run.State,
            run.EarnedPointsMilli,
            run.PossiblePointsMilli,
            judgments);
    }
}

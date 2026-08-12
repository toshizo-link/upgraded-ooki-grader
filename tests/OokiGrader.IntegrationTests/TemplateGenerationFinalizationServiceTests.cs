using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Application.Templates;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateGenerationFinalizationServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    [Fact]
    public async Task NonStepFinalCheckEditResolvesNameGradeAndBlockingWarnings()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Other,
            unitCount: 1,
            warnings:
            [
                Warning("TEST_NAME_REQUIRED"),
                Warning("GRADE_REQUIRED"),
            ],
            finalNamesReady: false,
            gradesReady: false);
        var unit = Assert.Single(seeded.Units);

        var result = await fixture.Service.UpdateUnitAsync(
            new UpdateTemplateGenerationUnitCommand(
                seeded.BatchId,
                unit.Id,
                BaseTestName: "　夏期　確認　第４回　",
                ResolvedGrade: GradeLevel.Grade5,
                GradeConfirmedByUser: true,
                TeacherNote: "  記述式の採点基準を確認  ",
                ExpectedRowVersion: unit.RowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "edit-other-unit",
                CorrelationId: "finalization-edit"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.NeedsFinalCheck, result.Status);
        var updated = Assert.Single(result.Units);
        Assert.Equal("夏期 確認 第4回", updated.UserConfirmedBaseName);
        Assert.Equal("夏期 確認 第4回", updated.FinalTemplateName);
        Assert.Equal(GradeLevel.Grade5, updated.ResolvedGrade);
        Assert.Equal(GradeEvidence.User, updated.GradeEvidence);
        Assert.True(updated.GradeConfirmedByUser);
        Assert.DoesNotContain(
            updated.Warnings.EnumerateArray(),
            item => item.GetProperty("code").GetString() is
                "TEST_NAME_REQUIRED" or "GRADE_REQUIRED");
        Assert.True(updated.RowVersion > unit.RowVersion);

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.TemplateGenerationUnits
            .AsNoTracking()
            .SingleAsync(item => item.Id == unit.Id);
        Assert.Equal("記述式の採点基準を確認", stored.TeacherNote);
        var gradeAudit = Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateGenerationGradeResolved"
                && item.ObjectId == unit.Id);
        using (var metadata = JsonDocument.Parse(gradeAudit.SafeMetadataJson!))
        {
            Assert.Equal(
                TemplateGenerationBatchService.ExtractionPromptVersion,
                metadata.RootElement.GetProperty("promptVersion").GetString());
            Assert.Equal(
                TemplateGenerationBatchService.ExtractionSchemaVersion,
                metadata.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(
                unit.RowVersion,
                metadata.RootElement
                    .GetProperty("expectedUnitRowVersion")
                    .GetInt64());
            Assert.True(metadata.RootElement
                .GetProperty("nextUnitRowVersion")
                .GetInt64() > unit.RowVersion);
        }

        fixture.Db.ChangeTracker.Clear();
        var replayed = await fixture.Service.UpdateUnitAsync(
            new UpdateTemplateGenerationUnitCommand(
                seeded.BatchId,
                unit.Id,
                BaseTestName: "　夏期　確認　第４回　",
                ResolvedGrade: GradeLevel.Grade5,
                GradeConfirmedByUser: true,
                TeacherNote: "  記述式の採点基準を確認  ",
                ExpectedRowVersion: unit.RowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "edit-other-unit",
                CorrelationId: "finalization-edit-replay"),
            CancellationToken.None);
        Assert.Equal(updated.RowVersion, Assert.Single(replayed.Units).RowVersion);
    }

    [Fact]
    public async Task KnownTestNamesCannotBeEditedThroughStepSetEndpoint()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Step,
            unitCount: 3,
            warnings: [Warning("STEP_NAME_MISMATCH")],
            finalNamesReady: false);
        var immutable = await Assert.ThrowsAsync<TemplateGenerationBatchServiceException>(() =>
            fixture.Service.UpdateStepSetAsync(
                new UpdateTemplateGenerationStepSetCommand(
                    seeded.BatchId,
                    SetIndex: 1,
                    BaseTestName: "STEP国語 第4回",
                    seeded.Units.ToDictionary(
                        item => item.Id,
                        item => item.RowVersion,
                        StringComparer.Ordinal),
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: "step-name-edit",
                    CorrelationId: "step-name-edit"),
                CancellationToken.None));
        Assert.Equal(
            TemplateNamePolicy.KnownTestNameImmutableErrorCode,
            immutable.Code);
    }

    [Fact]
    public async Task ConfirmationIsTransactionalCreatesIndependentGraphsAndIsIdempotent()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(TestType.Hop, unitCount: 2);

        var stale = await Assert.ThrowsAsync<TemplateGenerationBatchServiceException>(() =>
            fixture.Service.ConfirmAsync(
                new ConfirmTemplateGenerationBatchCommand(
                    seeded.BatchId,
                    seeded.BatchRowVersion + 1,
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: "confirm-stale",
                    CorrelationId: "confirm-stale"),
                CancellationToken.None));
        Assert.Equal("STALE_ROW_VERSION", stale.Code);
        Assert.Empty(await fixture.Db.TestTemplates.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.TemplateVersions.AsNoTracking().ToArrayAsync());

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-valid",
                CorrelationId: "confirm-valid"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Completed, completed.Status);
        Assert.Equal(2, completed.CreatedTemplates.Count);
        Assert.Equal(2, completed.CreatedTemplates.Select(item => item.TemplateId).Distinct().Count());
        Assert.All(completed.Units, item =>
        {
            Assert.Equal(TemplateGenerationUnitStatus.Confirmed, item.Status);
            Assert.NotNull(item.CreatedTemplateId);
            Assert.NotNull(item.CreatedTemplateVersionId);
        });

        var confirmationAudit = await fixture.Db.AuditEvents
            .AsNoTracking()
            .SingleAsync(item => item.EventType
                == "TemplateGenerationBatchConfirmed");
        using (var metadata = JsonDocument.Parse(
                   confirmationAudit.SafeMetadataJson!))
        {
            Assert.Equal(
                seeded.BatchRowVersion,
                metadata.RootElement
                    .GetProperty("expectedBatchRowVersion")
                    .GetInt64());
            Assert.Equal(
                TemplateGenerationBatchService.ExtractionPromptVersion,
                metadata.RootElement.GetProperty("promptVersion").GetString());
            Assert.Equal(
                TemplateGenerationBatchService.ExtractionSchemaVersion,
                metadata.RootElement.GetProperty("schemaVersion").GetString());
        }

        fixture.Db.ChangeTracker.Clear();
        var units = await fixture.Db.TemplateGenerationUnits
            .AsNoTracking()
            .Include(item => item.DerivedSource)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync();
        var templates = await fixture.Db.TestTemplates
            .AsNoTracking()
            .OrderBy(item => item.Title)
            .ToArrayAsync();
        var versions = await fixture.Db.TemplateVersions
            .AsNoTracking()
            .OrderBy(item => item.OriginatingUnitId)
            .ToArrayAsync();
        var questions = await fixture.Db.Questions
            .AsNoTracking()
            .OrderBy(item => item.TemplateVersionId)
            .ToArrayAsync();
        var answers = await fixture.Db.AcceptedAnswers
            .AsNoTracking()
            .OrderBy(item => item.QuestionId)
            .ThenBy(item => item.VariantType)
            .ToArrayAsync();
        var sources = await fixture.Db.TemplateSources
            .AsNoTracking()
            .OrderBy(item => item.TemplateVersionId)
            .ToArrayAsync();

        Assert.Equal(2, templates.Length);
        Assert.Equal(
            ["国語4年HOP1", "国語4年HOP2"],
            templates.Select(item => item.Title));
        Assert.Equal(2, versions.Length);
        Assert.Equal(2, questions.Length);
        Assert.Equal(4, answers.Length);
        Assert.Equal(2, sources.Length);
        Assert.Equal(2, versions.Select(item => item.TestTemplateId).Distinct().Count());
        Assert.Equal(2, versions.Select(item => item.OriginatingUnitId).Distinct().Count());
        Assert.Equal(2, questions.Select(item => item.TemplateVersionId).Distinct().Count());
        Assert.Equal(2, questions.Select(item => item.LogicalQuestionId).Distinct().Count());
        Assert.All(questions, item =>
        {
            Assert.True(item.RequiresCompleteAnswer);
            Assert.True(item.AnswerOrderInsensitive);
        });
        Assert.Equal(2, sources.Select(item => item.FileReferenceId).Distinct().Count());
        Assert.All(versions, item =>
        {
            Assert.Equal(seeded.BatchId, item.OriginatingBatchId);
            var profile = JsonSerializer.Deserialize<TemplateGenerationProfile>(
                item.GenerationProfileJson!,
                JsonOptions)!;
            Assert.Equal(
                TemplateGenerationBatchService.ExtractionPromptVersion,
                profile.ExtractionPromptVersion);
            Assert.Equal(profile.ComputeHash(), item.GenerationProfileHash);
        });
        Assert.All(sources, item => Assert.Equal("contains_model_answers", item.SourceRole));
        Assert.All(
            answers.Where(item => item.VariantType == "canonical"),
            item =>
            {
                Assert.Equal("ABC 123", item.NormalizedText);
                Assert.Equal("provided_model_answer", item.AnswerProvenance);
                Assert.NotNull(item.SourceFileReferenceId);
                Assert.Equal(1, item.SourcePageNumber);
            });
        Assert.All(units, item =>
        {
            var version = Assert.Single(
                versions,
                candidate => candidate.Id == item.CreatedTemplateVersionId);
            Assert.Equal(item.Id, version.OriginatingUnitId);
            Assert.Equal(item.GenerationProfileHash, version.GenerationProfileHash);
            Assert.Contains(
                sources,
                source => source.TemplateVersionId == version.Id
                    && source.FileReferenceId == item.DerivedSource!.FileReferenceId);
        });

        var graphCounts = await fixture.ReadGraphCountsAsync();
        fixture.Db.ChangeTracker.Clear();
        var staleUnit = await fixture.Db.TemplateGenerationUnits
            .Include(item => item.DerivedSource)
            .SingleAsync(item => item.BatchId == seeded.BatchId
                && item.Sequence == 1);
        var staleTemplate = await fixture.Db.TestTemplates
            .SingleAsync(item => item.Id == staleUnit.CreatedTemplateId);
        var staleSource = await fixture.Db.TemplateSources
            .SingleAsync(item => item.TemplateVersionId
                == staleUnit.CreatedTemplateVersionId);
        staleUnit.UserConfirmedBaseName = "AIの基本名";
        staleUnit.FinalTemplateName = "AIが付けた名前";
        staleTemplate.Title = "AIが付けた名前";
        staleSource.DisplayName = "AIが付けた名前.pdf";
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var repeated = await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-repeated",
                CorrelationId: "confirm-repeated"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Completed, repeated.Status);
        Assert.Equal(graphCounts, await fixture.ReadGraphCountsAsync());
        Assert.Equal(
            "国語4年HOP1",
            repeated.Units.Single(item => item.Sequence == 1).FinalTemplateName);
        Assert.Equal(
            "国語4年HOP1",
            (await fixture.Db.TestTemplates.AsNoTracking()
                .SingleAsync(item => item.Id == staleTemplate.Id)).Title);
        Assert.Equal(
            "国語4年HOP1.pdf",
            (await fixture.Db.TemplateSources.AsNoTracking()
                .SingleAsync(item => item.Id == staleSource.Id)).DisplayName);
        var reconciliationAudit = Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateGenerationNamesReconciled");
        using var reconciliationMetadata = JsonDocument.Parse(
            reconciliationAudit.SafeMetadataJson!);
        Assert.Equal(
            1,
            reconciliationMetadata.RootElement
                .GetProperty("renamedUnitCount")
                .GetInt32());
    }

    [Theory]
    [InlineData("multiple_choice")]
    [InlineData("numeric")]
    [InlineData("exact_short_text")]
    [InlineData("semantic_short_text")]
    [InlineData("subjective")]
    public async Task ConfirmationDefaultsSupportedQuestionsToAiRubric(
        string questionType)
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Hop,
            unitCount: 1,
            draftFactory: (_, unitId) =>
                CreateAiDefaultDraft(unitId, questionType));

        await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: $"confirm-ai-default-{questionType}",
                CorrelationId: $"confirm-ai-default-{questionType}"),
            CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var question = await fixture.Db.Questions
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(questionType, question.QuestionType);
        Assert.Equal("ai_rubric", question.GradingMode);
        Assert.Contains("模範解答", question.RubricText, StringComparison.Ordinal);
        Assert.Equal(1_000, question.PointIncrementMilli);
        Assert.False(question.RequiresReviewAlways);
    }

    [Fact]
    public async Task ConfirmationAcceptsPageLocalOrdinalsIndependentOfPerQuestionSlotCount()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Step,
            unitCount: 3,
            draftFactory: (sequence, unitId) =>
                CreateMultiQuestionStepDraft(sequence, unitId));

        var completed = await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-page-local-ordinals",
                CorrelationId: "confirm-page-local-ordinals"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Completed, completed.Status);
        Assert.Equal(3, completed.CreatedTemplates.Count);
        Assert.Equal(
            15,
            await fixture.Db.Questions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ConfirmationAcceptsReviewedInventoryMismatchAndKeepsQuestionsFlagged()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Step,
            unitCount: 3,
            draftFactory: (sequence, unitId) => sequence == 1
                ? CreateReviewedInventoryMismatchStepDraft(unitId)
                : CreateMultiQuestionStepDraft(sequence, unitId));

        var completed = await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-reviewed-inventory-mismatch",
                CorrelationId: "confirm-reviewed-inventory-mismatch"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Completed, completed.Status);
        var questions = await fixture.Db.Questions
            .AsNoTracking()
            .ToArrayAsync();
        var inventoryReviewQuestions = questions
            .Where(item => item.TeacherNote?.Contains(
                "question.answer_slot_inventory_mismatch",
                StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(3, inventoryReviewQuestions.Length);
        Assert.All(
            inventoryReviewQuestions,
            item => Assert.False(item.RequiresReviewAlways));
    }

    [Fact]
    public async Task ConfirmationAcceptsReviewedEmbeddedBlankAnomalyAndKeepsItUnverified()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Step,
            unitCount: 3,
            draftFactory: (sequence, unitId) => sequence == 1
                ? CreateReviewedEmbeddedBlankAnomalyStepDraft(unitId)
                : CreateMultiQuestionStepDraft(sequence, unitId));

        var completed = await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-reviewed-embedded-blank",
                CorrelationId: "confirm-reviewed-embedded-blank"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Completed, completed.Status);
        var flaggedQuestion = Assert.Single(
            await fixture.Db.Questions
                .AsNoTracking()
                .Where(item => item.TeacherNote != null
                    && item.TeacherNote.Contains(
                        "question.fill_blank_placeholder_invalid"))
                .ToArrayAsync());
        Assert.Contains(
            "question.filled_answer_removal_unconfirmed",
            flaggedQuestion.TeacherNote!,
            StringComparison.Ordinal);
        Assert.False(flaggedQuestion.RequiresReviewAlways);
        Assert.False(flaggedQuestion.TeacherVerified);

        var answer = await fixture.Db.AcceptedAnswers
            .AsNoTracking()
            .SingleAsync(item => item.QuestionId == flaggedQuestion.Id
                && item.VariantType == "canonical");
        Assert.False(answer.TeacherVerified);
    }

    [Theory]
    [InlineData("ordinal-zero")]
    [InlineData("count-zero")]
    public async Task ConfirmationRejectsNonPositiveAnswerSlotValues(string mutation)
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Step,
            unitCount: 3,
            draftFactory: (sequence, unitId) => sequence == 1
                ? CreateNonPositiveSlotStepDraft(unitId, mutation)
                : CreateMultiQuestionStepDraft(sequence, unitId));

        var error = await Assert.ThrowsAsync<TemplateGenerationBatchServiceException>(
            () => fixture.Service.ConfirmAsync(
                new ConfirmTemplateGenerationBatchCommand(
                    seeded.BatchId,
                    seeded.BatchRowVersion,
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: $"confirm-non-positive-{mutation}",
                    CorrelationId: $"confirm-non-positive-{mutation}"),
                CancellationToken.None));

        Assert.Equal("TEMPLATE_DRAFT_INVALID", error.Code);
        Assert.Equal(
            "AIが生成した下書きの形式を確認できませんでした",
            error.Title);
        Assert.Equal(
            "入力内容ではなく生成結果の問題です。失敗した項目だけ再試行してください。",
            error.Detail);
        Assert.Empty(await fixture.Db.TestTemplates.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task RetryRequeuesOnlyFailedWorkAndCancelStopsTheReplacementJob()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Hop,
            unitCount: 1,
            status: TemplateGenerationBatchStatus.Failed,
            unitStatus: TemplateGenerationUnitStatus.Failed,
            warnings:
            [
                Warning("TEMPLATE_EXTRACTION_FAILED"),
                Warning("GRADE_REQUIRED"),
            ],
            includeExtractionArtifacts: false);

        var retried = await fixture.Service.RetryAsync(
            new RetryTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "retry-failed",
                CorrelationId: "retry-failed"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Generating, retried.Status);
        var queued = Assert.Single(retried.Units);
        Assert.Equal(TemplateGenerationUnitStatus.Queued, queued.Status);
        Assert.NotNull(queued.ExtractionJobId);
        Assert.DoesNotContain(
            queued.Warnings.EnumerateArray(),
            item => item.GetProperty("code").GetString()
                == "TEMPLATE_EXTRACTION_FAILED");
        Assert.Contains(
            queued.Warnings.EnumerateArray(),
            item => item.GetProperty("code").GetString() == "GRADE_REQUIRED");

        fixture.Db.ChangeTracker.Clear();
        var cancelled = await fixture.Service.CancelAsync(
            new CancelTemplateGenerationBatchCommand(
                seeded.BatchId,
                retried.RowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "cancel-retry",
                CorrelationId: "cancel-retry"),
            CancellationToken.None);
        Assert.Equal(TemplateGenerationBatchStatus.Cancelled, cancelled.Status);
        Assert.Equal(
            "cancelled",
            (await fixture.Db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync()).State);

        fixture.Db.ChangeTracker.Clear();
        var repeated = await fixture.Service.CancelAsync(
            new CancelTemplateGenerationBatchCommand(
                seeded.BatchId,
                ExpectedRowVersion: seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "cancel-repeated",
                CorrelationId: "cancel-repeated"),
            CancellationToken.None);
        Assert.Equal(TemplateGenerationBatchStatus.Cancelled, repeated.Status);
        Assert.Single(await fixture.Db.BackgroundJobs.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task RetryThenConfirmReconcilesOwnedFailuresAndPreservesHistory()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Hop,
            unitCount: 1,
            status: TemplateGenerationBatchStatus.Failed,
            unitStatus: TemplateGenerationUnitStatus.Failed);
        var unitId = Assert.Single(seeded.Units).Id;
        var currentFailedJobId = await fixture.SeedUnitJobAsync(
            unitId,
            attemptSuffix: "current-failed",
            state: "failed",
            makeCurrent: true);
        var legacyFailedJobId = await fixture.SeedUnitJobAsync(
            unitId,
            attemptSuffix: "legacy-failed",
            state: "failed",
            makeCurrent: false);
        var unrelatedFailedJobId = await fixture.SeedUnitJobAsync(
            fixture.NewId(),
            attemptSuffix: "unrelated-failed",
            state: "failed",
            makeCurrent: false);
        var currentBeforeRetry = await fixture.Db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == currentFailedJobId);

        fixture.Db.ChangeTracker.Clear();
        var retried = await fixture.Service.RetryAsync(
            new RetryTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "retry-with-history",
                CorrelationId: "retry-with-history"),
            CancellationToken.None);
        var replacementJobId = Assert.Single(retried.Units).ExtractionJobId!;
        var jobsAfterRetry = await fixture.Db.BackgroundJobs
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal);
        Assert.Equal("cancelled", jobsAfterRetry[currentFailedJobId].State);
        Assert.Equal("failed", jobsAfterRetry[legacyFailedJobId].State);
        Assert.Equal("queued", jobsAfterRetry[replacementJobId].State);
        Assert.Equal(
            currentBeforeRetry.AttemptCount,
            jobsAfterRetry[currentFailedJobId].AttemptCount);
        Assert.Equal(
            currentBeforeRetry.ErrorCode,
            jobsAfterRetry[currentFailedJobId].ErrorCode);
        Assert.Equal(
            currentBeforeRetry.SafeErrorDetail,
            jobsAfterRetry[currentFailedJobId].SafeErrorDetail);
        Assert.Equal(
            currentBeforeRetry.CompletedAt,
            jobsAfterRetry[currentFailedJobId].CompletedAt);
        var retryAudit = Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateGenerationStarted");
        using (var metadata = JsonDocument.Parse(retryAudit.SafeMetadataJson!))
        {
            Assert.Equal(
                1,
                metadata.RootElement.GetProperty("supersededJobCount").GetInt32());
            Assert.Equal(
                1,
                metadata.RootElement
                    .GetProperty("supersededJobPreviousStates")
                    .GetProperty("failed")
                    .GetInt32());
        }

        fixture.Db.ChangeTracker.Clear();
        var storedBatch = await fixture.Db.TemplateGenerationBatches
            .SingleAsync(item => item.Id == seeded.BatchId);
        var storedUnit = await fixture.Db.TemplateGenerationUnits
            .SingleAsync(item => item.Id == unitId);
        var replacementJob = await fixture.Db.BackgroundJobs
            .SingleAsync(item => item.Id == replacementJobId);
        storedBatch.Status = TemplateGenerationBatchStatus.NeedsFinalCheck;
        storedBatch.CompletedUnitCount = 1;
        storedBatch.FailedUnitCount = 0;
        storedBatch.LastErrorCode = null;
        storedUnit.Status = TemplateGenerationUnitStatus.Extracted;
        replacementJob.State = "succeeded";
        replacementJob.ProgressBasisPoints = 10_000;
        replacementJob.CompletedAt = new DateTimeOffset(
            2026, 8, 9, 3, 3, 0, TimeSpan.Zero);
        await fixture.Db.SaveChangesAsync();
        var finalCheckRevision = storedBatch.Revision;

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                finalCheckRevision,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-after-retry",
                CorrelationId: "confirm-after-retry"),
            CancellationToken.None);
        Assert.Equal(TemplateGenerationBatchStatus.Completed, completed.Status);

        var jobsAfterConfirmation = await fixture.Db.BackgroundJobs
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal);
        Assert.Equal("cancelled", jobsAfterConfirmation[legacyFailedJobId].State);
        Assert.Equal("succeeded", jobsAfterConfirmation[replacementJobId].State);
        Assert.Equal("failed", jobsAfterConfirmation[unrelatedFailedJobId].State);
        Assert.DoesNotContain(
            jobsAfterConfirmation.Values,
            item => item.State is "failed" or "blocked"
                && item.DeduplicationKey.Contains(unitId, StringComparison.Ordinal));
        var confirmationAudit = Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateGenerationBatchConfirmed");
        using (var metadata = JsonDocument.Parse(
                   confirmationAudit.SafeMetadataJson!))
        {
            Assert.Equal(
                1,
                metadata.RootElement.GetProperty("cancelledJobCount").GetInt32());
            Assert.Equal(
                1,
                metadata.RootElement
                    .GetProperty("previousJobStates")
                    .GetProperty("failed")
                    .GetInt32());
        }

        var lateLegacyBlockedJobId = await fixture.SeedUnitJobAsync(
            unitId,
            attemptSuffix: "late-legacy-blocked",
            state: "blocked",
            makeCurrent: false);
        fixture.Db.ChangeTracker.Clear();
        var replayed = await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                ExpectedRowVersion: 0,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-reconcile-completed",
                CorrelationId: "confirm-reconcile-completed"),
            CancellationToken.None);
        Assert.Equal(TemplateGenerationBatchStatus.Completed, replayed.Status);
        Assert.Equal(
            "cancelled",
            (await fixture.Db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync(item => item.Id == lateLegacyBlockedJobId)).State);
        var replayAudit = Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType
                == "TemplateGenerationConfirmedJobsReconciled");
        using var replayMetadata = JsonDocument.Parse(replayAudit.SafeMetadataJson!);
        Assert.Equal(
            1,
            replayMetadata.RootElement
                .GetProperty("cancelledJobCount")
                .GetInt32());
        Assert.Equal(
            1,
            replayMetadata.RootElement
                .GetProperty("previousJobStates")
                .GetProperty("blocked")
                .GetInt32());
    }

    [Fact]
    public async Task CancelReconcilesEveryOwnedAttemptAndPreservesSucceededAndHistory()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Hop,
            unitCount: 1,
            status: TemplateGenerationBatchStatus.Failed,
            unitStatus: TemplateGenerationUnitStatus.Failed,
            includeExtractionArtifacts: false);
        var unitId = Assert.Single(seeded.Units).Id;
        var jobIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in new[]
                 {
                     "queued",
                     "retry_waiting",
                     "leased",
                     "failed",
                     "blocked",
                     "succeeded",
                     "cancelled",
                 })
        {
            jobIds[state] = await fixture.SeedUnitJobAsync(
                unitId,
                attemptSuffix: state,
                state,
                makeCurrent: state == "queued");
        }

        var unrelatedFailedJobId = await fixture.SeedUnitJobAsync(
            fixture.NewId(),
            attemptSuffix: "unrelated-failed",
            state: "failed",
            makeCurrent: false);
        var failedBefore = await fixture.Db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == jobIds["failed"]);

        fixture.Db.ChangeTracker.Clear();
        var cancelled = await fixture.Service.CancelAsync(
            new CancelTemplateGenerationBatchCommand(
                seeded.BatchId,
                seeded.BatchRowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "cancel-all-attempts",
                CorrelationId: "cancel-all-attempts"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Cancelled, cancelled.Status);
        var jobs = await fixture.Db.BackgroundJobs
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal);
        Assert.Equal("succeeded", jobs[jobIds["succeeded"]].State);
        Assert.Equal("cancelled", jobs[jobIds["cancelled"]].State);
        foreach (var state in new[]
                 {
                     "queued",
                     "retry_waiting",
                     "leased",
                     "failed",
                     "blocked",
                 })
        {
            Assert.Equal("cancelled", jobs[jobIds[state]].State);
        }

        Assert.Null(jobs[jobIds["leased"]].LeaseOwner);
        Assert.Null(jobs[jobIds["leased"]].LeaseExpiresAt);
        Assert.Equal(failedBefore.AttemptCount, jobs[jobIds["failed"]].AttemptCount);
        Assert.Equal(failedBefore.ErrorCode, jobs[jobIds["failed"]].ErrorCode);
        Assert.Equal(
            failedBefore.SafeErrorDetail,
            jobs[jobIds["failed"]].SafeErrorDetail);
        Assert.Equal(failedBefore.CompletedAt, jobs[jobIds["failed"]].CompletedAt);
        Assert.Equal("failed", jobs[unrelatedFailedJobId].State);
        Assert.Equal(
            1,
            jobs.Values.Count(item => item.State is "failed" or "blocked"));

        var audit = Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateGenerationBatchCancelled");
        using var metadata = JsonDocument.Parse(audit.SafeMetadataJson!);
        Assert.Equal(
            5,
            metadata.RootElement.GetProperty("cancelledJobCount").GetInt32());
        var previousStates = metadata.RootElement.GetProperty("previousJobStates");
        Assert.Equal(1, previousStates.GetProperty("blocked").GetInt32());
        Assert.Equal(1, previousStates.GetProperty("failed").GetInt32());
        Assert.Equal(1, previousStates.GetProperty("leased").GetInt32());
        Assert.Equal(1, previousStates.GetProperty("queued").GetInt32());
        Assert.Equal(1, previousStates.GetProperty("retry_waiting").GetInt32());
    }

    [Fact]
    public async Task RepeatedCancelRepairsLegacyTerminalJobsExactlyOnce()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Hop,
            unitCount: 1,
            status: TemplateGenerationBatchStatus.Cancelled,
            unitStatus: TemplateGenerationUnitStatus.Failed,
            includeExtractionArtifacts: false);
        var failedJobId = await fixture.SeedUnitJobAsync(
            Assert.Single(seeded.Units).Id,
            attemptSuffix: "legacy-failed",
            state: "failed",
            makeCurrent: true);

        fixture.Db.ChangeTracker.Clear();
        var repaired = await fixture.Service.CancelAsync(
            new CancelTemplateGenerationBatchCommand(
                seeded.BatchId,
                ExpectedRowVersion: seeded.BatchRowVersion + 99,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "legacy-cancel",
                CorrelationId: "legacy-cancel"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Cancelled, repaired.Status);
        Assert.Equal(seeded.BatchRowVersion, repaired.RowVersion);
        Assert.Equal(
            "cancelled",
            (await fixture.Db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync(item => item.Id == failedJobId)).State);
        var reconciliationAudit = Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType
                == "TemplateGenerationCancelledJobsReconciled");
        using (var metadata = JsonDocument.Parse(
                   reconciliationAudit.SafeMetadataJson!))
        {
            Assert.Equal(
                1,
                metadata.RootElement
                    .GetProperty("cancelledJobCount")
                    .GetInt32());
            Assert.Equal(
                1,
                metadata.RootElement
                    .GetProperty("previousJobStates")
                    .GetProperty("failed")
                    .GetInt32());
        }

        fixture.Db.ChangeTracker.Clear();
        var replayed = await fixture.Service.CancelAsync(
            new CancelTemplateGenerationBatchCommand(
                seeded.BatchId,
                ExpectedRowVersion: 0,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "legacy-cancel",
                CorrelationId: "legacy-cancel-replay"),
            CancellationToken.None);
        Assert.Equal(seeded.BatchRowVersion, replayed.RowVersion);
        Assert.Single(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType
                == "TemplateGenerationCancelledJobsReconciled");
    }

    [Fact]
    public async Task OrientationRetryExhaustionRequiresReuploadAndCreatesNoJob()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.Hop,
            unitCount: 1,
            status: TemplateGenerationBatchStatus.Failed,
            unitStatus: TemplateGenerationUnitStatus.Failed,
            warnings: [Warning("ORIENTATION_RETRY_EXHAUSTED")],
            includeExtractionArtifacts: false);

        var error = await Assert.ThrowsAsync<TemplateGenerationBatchServiceException>(() =>
            fixture.Service.RetryAsync(
                new RetryTemplateGenerationBatchCommand(
                    seeded.BatchId,
                    seeded.BatchRowVersion,
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: "retry-orientation",
                    CorrelationId: "retry-orientation"),
                CancellationToken.None));

        Assert.Equal("ORIENTATION_RETRY_EXHAUSTED", error.Code);
        Assert.Empty(await fixture.Db.BackgroundJobs.AsNoTracking().ToArrayAsync());
        Assert.Equal(
            TemplateGenerationBatchStatus.Failed,
            (await fixture.Db.TemplateGenerationBatches
                .AsNoTracking()
                .SingleAsync()).Status);
    }

    [Fact]
    public async Task ConfirmationAllowsMixedRotationPreservesCrossPageAnswerAndRejectsUnavailableDerivedObject()
    {
        await using var fixture = await FinalizationFixture.CreateAsync();
        var seeded = await fixture.SeedBatchAsync(
            TestType.ClassPlacement,
            unitCount: 1,
            sourcePageCountOverride: 2,
            draftFactory: (_, unitId) => CreateCrossPageDraft(unitId));

        fixture.Db.ChangeTracker.Clear();
        var storedUnit = await fixture.Db.TemplateGenerationUnits
            .SingleAsync(item => item.BatchId == seeded.BatchId);
        var rotationsJson = JsonSerializer.Serialize(
            new[]
            {
                new AppliedPageRotation(
                    $"{storedUnit.Id}:page:1",
                    OriginalPageNumber: 1,
                    ClockwiseDegrees: 0,
                    Source: "gemini",
                    Confidence: 0.99),
                new AppliedPageRotation(
                    $"{storedUnit.Id}:page:2",
                    OriginalPageNumber: 2,
                    ClockwiseDegrees: 90,
                    Source: "gemini",
                    Confidence: 0.98),
            },
            JsonOptions);
        storedUnit.OrientationAttemptCount = 1;
        storedUnit.AppliedRotationsJson = rotationsJson;
        var derivedSource = await fixture.Db.TemplateGenerationDerivedSources
            .SingleAsync(item => item.UnitId == storedUnit.Id);
        derivedSource.DerivationType = "pageRangeAndRotation";
        derivedSource.AppliedRotationsJson = rotationsJson;
        var fileObject = await fixture.Db.FileObjects
            .SingleAsync(item => item.StorageClass
                == ContentStorageClass.TemplateDerived.ToString());
        fileObject.State = "deleted";
        await fixture.Db.SaveChangesAsync();
        var currentBatchRevision = await fixture.Db.TemplateGenerationBatches
            .AsNoTracking()
            .Where(item => item.Id == seeded.BatchId)
            .Select(item => item.Revision)
            .SingleAsync();
        fixture.Db.ChangeTracker.Clear();

        var unavailable = await Assert.ThrowsAsync<TemplateGenerationBatchServiceException>(
            () => fixture.Service.ConfirmAsync(
                new ConfirmTemplateGenerationBatchCommand(
                    seeded.BatchId,
                    currentBatchRevision,
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: "confirm-unavailable-derived",
                    CorrelationId: "confirm-unavailable-derived"),
                CancellationToken.None));
        Assert.Equal("FINAL_CHECK_INCOMPLETE", unavailable.Code);
        Assert.Empty(await fixture.Db.TestTemplates.AsNoTracking().ToArrayAsync());

        fixture.Db.ChangeTracker.Clear();
        fileObject = await fixture.Db.FileObjects
            .SingleAsync(item => item.StorageClass
                == ContentStorageClass.TemplateDerived.ToString());
        fileObject.State = "available";
        await fixture.Db.SaveChangesAsync();
        currentBatchRevision = await fixture.Db.TemplateGenerationBatches
            .AsNoTracking()
            .Where(item => item.Id == seeded.BatchId)
            .Select(item => item.Revision)
            .SingleAsync();
        fixture.Db.ChangeTracker.Clear();

        await fixture.Service.ConfirmAsync(
            new ConfirmTemplateGenerationBatchCommand(
                seeded.BatchId,
                currentBatchRevision,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: "confirm-cross-page",
                CorrelationId: "confirm-cross-page"),
            CancellationToken.None);
        var answer = await fixture.Db.AcceptedAnswers
            .AsNoTracking()
            .SingleAsync(item => item.VariantType == "canonical");
        Assert.Equal(2, answer.SourcePageNumber);
    }

    private static GenerationWarning Warning(string code) =>
        new(code, GenerationWarningSeverity.Blocking, $"{code} の確認が必要です。");

    private static CanonicalTemplateGenerationDraft CreateMultiQuestionStepDraft(
        int sequence,
        string unitId)
    {
        const int firstPageQuestionCount = 3;
        const int secondPageQuestionCount = 2;
        return new CanonicalTemplateGenerationDraft(
            TemplateGenerationBatchService.ExtractionSchemaVersion,
            new CanonicalTemplateGenerationMetadata(
                $"STEP国語 第{sequence}回",
                "小学4年",
                GradeConfidence: 0.99,
                Warnings: []),
            [
                CreateStepDraftPage(
                    sequence,
                    unitId,
                    pageNumber: 1,
                    questionCount: firstPageQuestionCount),
                CreateStepDraftPage(
                    sequence,
                    unitId,
                    pageNumber: 2,
                    questionCount: secondPageQuestionCount),
            ],
            ReviewIssues: [],
            TotalPointsMilli:
                (firstPageQuestionCount + secondPageQuestionCount) * 1_000L);
    }

    private static CanonicalTemplateGenerationPage CreateStepDraftPage(
        int sequence,
        string unitId,
        int pageNumber,
        int questionCount) =>
        new(
            unitId,
            pageNumber,
            DetectedAnswerSlotCount: questionCount,
            Questions: Enumerable.Range(1, questionCount)
                .Select(ordinal => new CanonicalTemplateGenerationQuestion(
                    $"step-{sequence}-page-{pageNumber}-question-{ordinal}",
                    DisplayLabel: $"{pageNumber}-{ordinal}",
                    QuestionText: $"第{pageNumber}ページの設問{ordinal}",
                    AnswerSlotOrdinal: ordinal,
                    AnswerSlotCount: 1,
                    FilledAnswerRemoved: true,
                    IsEmbeddedFillBlank: false,
                    QuestionType: "exact_short_text",
                    ExpectedAnswer: $"解答{pageNumber}-{ordinal}",
                    AnswerProvenance: "provided_model_answer",
                    AnswerSource: new TemplateExtractionAnswerSource(
                        unitId,
                        pageNumber),
                    AcceptedVariants: [],
                    SuggestedPointsMilli: 1_000,
                    AllowNonKanjiSuggestion: false,
                    RequiresCompleteAnswerSuggestion: false,
                    AnswerOrderInsensitiveSuggestion: false,
                    RequiresTeacherAnswer: false,
                    Confidence: 0.99,
                    Warnings: [],
                    ReviewIssues: []))
                .ToArray());

    private static CanonicalTemplateGenerationDraft
        CreateReviewedInventoryMismatchStepDraft(string unitId)
    {
        var draft = CreateMultiQuestionStepDraft(
            sequence: 1,
            unitId: unitId);
        var pages = draft.Pages.ToArray();
        var firstPage = pages[0];
        var inventoryIssue = new TemplateExtractionReviewIssue(
            "question.answer_slot_inventory_mismatch",
            "解答欄の分割と順番を先生が確認してください。",
            Blocking: true);
        var questions = firstPage.Questions
            .Select(question => question with
            {
                ReviewIssues = [inventoryIssue],
            })
            .ToArray();
        questions[1] = questions[1] with
        {
            AnswerSlotOrdinal = firstPage.Questions.Count + 1,
        };
        pages[0] = firstPage with
        {
            DetectedAnswerSlotCount = firstPage.DetectedAnswerSlotCount + 1,
            Questions = questions,
        };

        var templateIssue = new TemplateExtractionReviewIssue(
            "template.answer_slot_inventory_mismatch",
            "解答欄数と個別問題数が一致しません。",
            Blocking: true);
        return draft with
        {
            Pages = pages,
            ReviewIssues = [templateIssue],
        };
    }

    private static CanonicalTemplateGenerationDraft
        CreateReviewedEmbeddedBlankAnomalyStepDraft(string unitId)
    {
        var draft = CreateMultiQuestionStepDraft(
            sequence: 1,
            unitId: unitId);
        var pages = draft.Pages.ToArray();
        var firstPage = pages[0];
        var questions = firstPage.Questions.ToArray();
        questions[0] = questions[0] with
        {
            QuestionText = "空欄の位置を原稿で確認する必要がある設問です。",
            FilledAnswerRemoved = false,
            IsEmbeddedFillBlank = true,
            QuestionType = "multiple_choice",
            ReviewIssues =
            [
                new TemplateExtractionReviewIssue(
                    "question.fill_blank_placeholder_invalid",
                    "対象となる空欄の位置を確認してください。",
                    Blocking: true),
                new TemplateExtractionReviewIssue(
                    "question.filled_answer_removal_unconfirmed",
                    "記入済み内容を除外できたか確認してください。",
                    Blocking: true),
            ],
        };
        pages[0] = firstPage with { Questions = questions };
        return draft with { Pages = pages };
    }

    private static CanonicalTemplateGenerationDraft
        CreateNonPositiveSlotStepDraft(string unitId, string mutation)
    {
        var draft = CreateMultiQuestionStepDraft(
            sequence: 1,
            unitId: unitId);
        var pages = draft.Pages.ToArray();
        var firstPage = pages[0];
        var questions = firstPage.Questions.ToArray();
        questions[1] = mutation switch
        {
            "ordinal-zero" => questions[1] with { AnswerSlotOrdinal = 0 },
            "count-zero" => questions[1] with { AnswerSlotCount = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        pages[0] = firstPage with { Questions = questions };

        return draft with { Pages = pages };
    }

    private static CanonicalTemplateGenerationDraft CreateCrossPageDraft(
        string unitId) =>
        new(
            TemplateGenerationBatchService.ExtractionSchemaVersion,
            new CanonicalTemplateGenerationMetadata(
                "クラス分け 国語",
                "小学4年",
                GradeConfidence: 0.99,
                Warnings: []),
            [
                new CanonicalTemplateGenerationPage(
                    unitId,
                    PageNumber: 1,
                    DetectedAnswerSlotCount: 1,
                    Questions:
                    [
                        new CanonicalTemplateGenerationQuestion(
                            "cross-page-q1",
                            DisplayLabel: "1",
                            QuestionText: "次の問いに答えなさい。",
                            AnswerSlotOrdinal: 1,
                            AnswerSlotCount: 1,
                            FilledAnswerRemoved: false,
                            IsEmbeddedFillBlank: false,
                            QuestionType: "exact_short_text",
                            ExpectedAnswer: "模範解答",
                            AnswerProvenance: "provided_model_answer",
                            AnswerSource: new TemplateExtractionAnswerSource(
                                unitId,
                                PageNumber: 2),
                            AcceptedVariants: [],
                            SuggestedPointsMilli: 1_000,
                            AllowNonKanjiSuggestion: false,
                            RequiresCompleteAnswerSuggestion: false,
                            AnswerOrderInsensitiveSuggestion: false,
                            RequiresTeacherAnswer: false,
                            Confidence: 0.99,
                            Warnings: [],
                            ReviewIssues: []),
                    ]),
                new CanonicalTemplateGenerationPage(
                    unitId,
                    PageNumber: 2,
                    DetectedAnswerSlotCount: 0,
                    Questions: []),
            ],
            ReviewIssues: [],
            TotalPointsMilli: 1_000);

    private static CanonicalTemplateGenerationDraft CreateAiDefaultDraft(
        string unitId,
        string questionType) =>
        new(
            TemplateGenerationBatchService.ExtractionSchemaVersion,
            new CanonicalTemplateGenerationMetadata(
                "国語 記述確認",
                "小学4年",
                GradeConfidence: 0.99,
                Warnings: []),
            [
                new CanonicalTemplateGenerationPage(
                    unitId,
                    PageNumber: 1,
                    DetectedAnswerSlotCount: 1,
                    Questions:
                    [
                        new CanonicalTemplateGenerationQuestion(
                            "ai-default-q1",
                            DisplayLabel: "1",
                            QuestionText: "理由を説明しなさい。",
                            AnswerSlotOrdinal: 1,
                            AnswerSlotCount: 1,
                            FilledAnswerRemoved: true,
                            IsEmbeddedFillBlank: false,
                            QuestionType: questionType,
                            ExpectedAnswer: "模範解答",
                            AnswerProvenance: "provided_model_answer",
                            AnswerSource: new TemplateExtractionAnswerSource(
                                unitId,
                                PageNumber: 1),
                            AcceptedVariants: [],
                            SuggestedPointsMilli: 1_000,
                            AllowNonKanjiSuggestion: false,
                            RequiresCompleteAnswerSuggestion: false,
                            AnswerOrderInsensitiveSuggestion: false,
                            RequiresTeacherAnswer: false,
                            Confidence: 0.99,
                            Warnings: [],
                            ReviewIssues: []),
                    ]),
            ],
            ReviewIssues: [],
            TotalPointsMilli: 1_000);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record SeededBatch(
        string BatchId,
        long BatchRowVersion,
        IReadOnlyList<SeededUnit> Units);

    private sealed record SeededUnit(string Id, long RowVersion);

    private sealed record GraphCounts(
        int Templates,
        int Versions,
        int Sources,
        int Questions,
        int Answers);

    private sealed class FinalizationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly TestUlidGenerator _ids;
        private readonly DateTimeOffset _now;

        private FinalizationFixture(
            SqliteConnection connection,
            OokiGraderDbContext db,
            TemplateGenerationFinalizationService service,
            TestUlidGenerator ids,
            DateTimeOffset now,
            string staffId,
            string uploadId)
        {
            _connection = connection;
            _ids = ids;
            _now = now;
            Db = db;
            Service = service;
            StaffId = staffId;
            UploadId = uploadId;
        }

        public OokiGraderDbContext Db { get; }
        public TemplateGenerationFinalizationService Service { get; }
        public string StaffId { get; }
        public string UploadId { get; }

        public static async Task<FinalizationFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new OokiGraderDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 8, 9, 3, 0, 0, TimeSpan.Zero);
            var ids = new TestUlidGenerator(now);
            var staffId = ids.NewId();
            var uploadId = ids.NewId();
            var sourceObjectId = ids.NewId();
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "finalization.teacher",
                UsernameNormalized = "finalization.teacher",
                DisplayName = "最終確認担当",
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = staffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = "国語_小学4年.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 100,
                CurrentBytes = 100,
                FinalSha256 = new string('a', 64),
                IncomingRelativePath = "incoming/finalization-source.part",
                State = "completed",
                ExpiresAt = now.AddHours(24),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = sourceObjectId,
                Sha256 = new string('a', 64),
                Bytes = 100,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = "template/source/aa/source.pdf",
                StorageClass = ContentStorageClass.TemplateSource.ToString(),
                RetentionClass = "template_source",
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = ids.NewId(),
                FileObjectId = sourceObjectId,
                OwnerType = "upload_session",
                OwnerId = uploadId,
                Purpose = "template_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var batchService = new TemplateGenerationBatchService(
                db,
                new UnusedContentStore(),
                new UnusedPageCountReader(),
                new TemplateUnitPlanner(),
                ids,
                new FixedTimeProvider(now.AddMinutes(1)),
                Options.Create(new TemplateGenerationBatchOptions()),
                new ApprovedPromptBundleCatalog(),
                AiProviderFeaturePolicy.AllowAll);
            var service = new TemplateGenerationFinalizationService(
                db,
                ids,
                new FixedTimeProvider(now.AddMinutes(1)),
                batchService);
            return new FinalizationFixture(
                connection,
                db,
                service,
                ids,
                now,
                staffId,
                uploadId);
        }

        public async Task<SeededBatch> SeedBatchAsync(
            TestType testType,
            int unitCount,
            TemplateGenerationBatchStatus status =
                TemplateGenerationBatchStatus.NeedsFinalCheck,
            TemplateGenerationUnitStatus unitStatus =
                TemplateGenerationUnitStatus.Extracted,
            IReadOnlyList<GenerationWarning>? warnings = null,
            bool finalNamesReady = true,
            bool gradesReady = true,
            bool includeExtractionArtifacts = true,
            int? sourcePageCountOverride = null,
            Func<int, string, CanonicalTemplateGenerationDraft>? draftFactory = null)
        {
            var sourcePageCount = sourcePageCountOverride
                ?? (testType == TestType.Step
                    ? checked(unitCount * 2)
                    : unitCount);
            AnswerStyle? answerStyle = testType == TestType.Other
                ? AnswerStyle.Normal
                : null;
            var promptSystem = TemplatePromptRouter.Resolve(testType, answerStyle);
            var batchId = _ids.NewId();
            var batch = new TemplateGenerationBatchEntity
            {
                Id = batchId,
                Status = status,
                TestType = testType,
                Subject = "国語",
                AnswerStyle = answerStyle,
                PromptSystem = promptSystem,
                SourceId = UploadId,
                SourcePageCount = sourcePageCount,
                ExpectedUnitCount = unitCount,
                CompletedUnitCount = unitStatus == TemplateGenerationUnitStatus.Extracted
                    ? unitCount
                    : 0,
                FailedUnitCount = unitStatus == TemplateGenerationUnitStatus.Failed
                    ? unitCount
                    : 0,
                PlanHash = Sha256($"plan:{batchId}"),
                CreatedByUserId = StaffId,
                CreatedAt = _now,
                UpdatedAt = _now,
                LastErrorCode = status == TemplateGenerationBatchStatus.Failed
                    ? "TEMPLATE_EXTRACTION_FAILED"
                    : null,
            };
            Db.TemplateGenerationBatches.Add(batch);
            var units = new List<TemplateGenerationUnitEntity>(unitCount);
            for (var index = 0; index < unitCount; index++)
            {
                var sequence = index + 1;
                var firstPage = testType == TestType.Step
                    ? checked(index * 2 + 1)
                    : sequence;
                var lastPage = testType switch
                {
                    TestType.Step => checked(firstPage + 1),
                    TestType.ClassPlacement or TestType.Other => sourcePageCount,
                    _ => firstPage,
                };
                var suffix = testType == TestType.Step ? $"-{sequence}" : null;
                var profile = new TemplateGenerationProfile(
                    TemplateGenerationProfile.CurrentProfileVersion,
                    testType,
                    "国語",
                    answerStyle,
                    promptSystem,
                    sourcePageCount,
                    sequence,
                    firstPage,
                    lastPage,
                    StepSetIndex: testType == TestType.Step ? 1 : null,
                    StepVariationIndex: testType == TestType.Step ? sequence : null,
                    DeterministicSuffix: suffix,
                    TemplateGenerationProfile.CurrentSplitPolicyVersion,
                    TemplateGenerationProfile.CurrentNamingPolicyVersion,
                    TemplateGenerationBatchService.ExtractionPromptVersion,
                    TemplateGenerationBatchService.ExtractionSchemaVersion);
                var unitId = _ids.NewId();
                var profileJson = JsonSerializer.Serialize(profile, JsonOptions);
                var draft = draftFactory?.Invoke(sequence, unitId)
                    ?? CreateDraft(sequence, unitId);
                var draftJson = JsonSerializer.Serialize(draft, JsonOptions);
                var derivedSha = Sha256($"derived:{unitId}");
                var unit = new TemplateGenerationUnitEntity
                {
                    Id = unitId,
                    BatchId = batchId,
                    Sequence = sequence,
                    Status = unitStatus,
                    TestType = testType,
                    AnswerStyle = answerStyle,
                    FirstPage = firstPage,
                    LastPage = lastPage,
                    StepSetIndex = testType == TestType.Step ? 1 : null,
                    StepVariationIndex = testType == TestType.Step ? sequence : null,
                    DeterministicSuffix = suffix,
                    PromptSystem = promptSystem,
                    GenerationProfileJson = profileJson,
                    GenerationProfileHash = profile.ComputeHash(),
                    OrientationAttemptCount = 0,
                    AppliedRotationsJson = "[]",
                    DerivedSourceObjectKey = includeExtractionArtifacts
                        ? $"template/derived/{derivedSha}.pdf"
                        : null,
                    DerivedSourceSha256 = includeExtractionArtifacts ? derivedSha : null,
                    ExtractionDraftJson = includeExtractionArtifacts ? draftJson : null,
                    ExtractionDraftHash = includeExtractionArtifacts
                        ? Sha256(draftJson)
                        : null,
                    PrintedTestName = $"国語 確認 {sequence}",
                    UserConfirmedBaseName = finalNamesReady
                        ? testType == TestType.Step
                            ? "STEP 国語"
                            : $"国語 確認 {sequence}"
                        : null,
                    FinalTemplateName = finalNamesReady
                        ? testType == TestType.Step
                            ? $"STEP 国語{suffix}"
                            : $"国語 確認 {sequence}"
                        : null,
                    FilenameGrade = GradeLevel.Grade4,
                    PaperGrade = GradeLevel.Grade4,
                    ResolvedGrade = gradesReady
                        ? GradeLevel.Grade4
                        : GradeLevel.Unknown,
                    GradeEvidence = gradesReady
                        ? GradeEvidence.FileNameAndPaper
                        : GradeEvidence.None,
                    GradeConfirmedByUser = false,
                    WarningsJson = JsonSerializer.Serialize(warnings ?? [], JsonOptions),
                    CreatedAt = _now,
                    UpdatedAt = _now,
                };
                Db.TemplateGenerationUnits.Add(unit);
                units.Add(unit);

                if (includeExtractionArtifacts)
                {
                    var objectId = _ids.NewId();
                    var referenceId = _ids.NewId();
                    Db.FileObjects.Add(new FileObjectEntity
                    {
                        Id = objectId,
                        Sha256 = derivedSha,
                        Bytes = 50,
                        VerifiedMime = "application/pdf",
                        Extension = "pdf",
                        RelativeObjectPath = $"template/derived/{derivedSha}.pdf",
                        StorageClass = ContentStorageClass.TemplateDerived.ToString(),
                        RetentionClass = "template_source",
                        State = "available",
                        CreatedAt = _now,
                        VerifiedAt = _now,
                        ReferenceCountCache = 1,
                    });
                    Db.FileReferences.Add(new FileReferenceEntity
                    {
                        Id = referenceId,
                        FileObjectId = objectId,
                        OwnerType = "template_generation_unit",
                        OwnerId = unitId,
                        Purpose = "derived_source",
                        RetentionAnchorAt = _now,
                        CreatedAt = _now,
                    });
                    Db.TemplateGenerationDerivedSources.Add(
                        new TemplateGenerationDerivedSourceEntity
                        {
                            Id = _ids.NewId(),
                            UnitId = unitId,
                            ParentSourceId = UploadId,
                            ParentFirstPage = firstPage,
                            ParentLastPage = lastPage,
                            OriginalContentSha256 = new string('a', 64),
                            DerivationType = "pageRange",
                            AppliedRotationsJson = "[]",
                            DerivationPolicyVersion = "page-range-quarter-turn-v1",
                            DerivedContentSha256 = derivedSha,
                            FileReferenceId = referenceId,
                            CreatedAt = _now,
                        });
                }
            }

            await Db.SaveChangesAsync();
            var seeded = new SeededBatch(
                batch.Id,
                batch.Revision,
                units.Select(item => new SeededUnit(item.Id, item.Revision)).ToArray());
            Db.ChangeTracker.Clear();
            return seeded;
        }

        public string NewId() => _ids.NewId();

        public async Task<string> SeedUnitJobAsync(
            string unitId,
            string attemptSuffix,
            string state,
            bool makeCurrent)
        {
            var jobId = _ids.NewId();
            var isTerminal = state is "succeeded" or "failed" or "cancelled";
            Db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = TemplateGenerationBatchService.UnitJobType,
                SchemaVersion = TemplateGenerationBatchService.UnitJobSchemaVersion,
                DeduplicationKey =
                    $"template-generation-unit:{unitId}:test:{attemptSuffix}",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new { unitId }, JsonOptions),
                State = state,
                AttemptCount = state is "queued" ? 0 : 3,
                MaxAttempts = 8,
                NextAttemptAt = _now,
                LeaseOwner = state == "leased" ? "test-worker" : null,
                LeaseExpiresAt = state == "leased" ? _now.AddMinutes(5) : null,
                ProgressBasisPoints = state == "succeeded" ? 10_000 : 2_500,
                ErrorCode = state is "failed" or "blocked"
                    ? $"SEEDED_{state.ToUpperInvariant()}"
                    : null,
                SafeErrorDetail = state is "failed" or "blocked"
                    ? $"seeded_{state}"
                    : null,
                CreatedAt = _now,
                UpdatedAt = _now,
                StartedAt = state == "queued" ? null : _now.AddMinutes(1),
                CompletedAt = isTerminal ? _now.AddMinutes(2) : null,
            });
            if (makeCurrent)
            {
                var unit = await Db.TemplateGenerationUnits
                    .SingleAsync(item => item.Id == unitId);
                unit.ExtractionJobId = jobId;
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return jobId;
        }

        public async Task<GraphCounts> ReadGraphCountsAsync() =>
            new(
                await Db.TestTemplates.AsNoTracking().CountAsync(),
                await Db.TemplateVersions.AsNoTracking().CountAsync(),
                await Db.TemplateSources.AsNoTracking().CountAsync(),
                await Db.Questions.AsNoTracking().CountAsync(),
                await Db.AcceptedAnswers.AsNoTracking().CountAsync());

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static CanonicalTemplateGenerationDraft CreateDraft(
            int sequence,
            string unitId) =>
            new(
                TemplateGenerationBatchService.ExtractionSchemaVersion,
                new CanonicalTemplateGenerationMetadata(
                    $"国語 確認 {sequence}",
                    "小学4年",
                    GradeConfidence: 0.99,
                    Warnings: []),
                [
                    new CanonicalTemplateGenerationPage(
                        unitId,
                        PageNumber: 1,
                        DetectedAnswerSlotCount: 1,
                        Questions:
                        [
                            new CanonicalTemplateGenerationQuestion(
                                $"unit-{sequence}-q1",
                                DisplayLabel: "1",
                                QuestionText: $"第{sequence}問",
                                AnswerSlotOrdinal: 1,
                                AnswerSlotCount: 1,
                                FilledAnswerRemoved: false,
                                IsEmbeddedFillBlank: false,
                                QuestionType: "exact_short_text",
                                ExpectedAnswer: "　ＡＢＣ　１２３　",
                                AnswerProvenance: "provided_model_answer",
                                AnswerSource: new TemplateExtractionAnswerSource(
                                    unitId,
                                    PageNumber: 1),
                                AcceptedVariants: ["ABC123"],
                                SuggestedPointsMilli: 1_000,
                                AllowNonKanjiSuggestion: false,
                                RequiresCompleteAnswerSuggestion: true,
                                AnswerOrderInsensitiveSuggestion: true,
                                RequiresTeacherAnswer: false,
                                Confidence: 0.99,
                                Warnings: [],
                                ReviewIssues: []),
                        ]),
                ],
                ReviewIssues: [],
                TotalPointsMilli: 1_000);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestUlidGenerator(DateTimeOffset now) : IUlidGenerator
    {
        private long _sequence;

        public string NewId() => UlidId.New(now.AddTicks(_sequence++));
    }

    private sealed class UnusedContentStore : IContentStore
    {
        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPageCountReader : IPdfPageCountReader
    {
        public Task<int> GetPageCountAsync(
            Stream source,
            int maximumPages,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

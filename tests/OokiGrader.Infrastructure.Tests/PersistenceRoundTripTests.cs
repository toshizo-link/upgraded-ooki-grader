using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Grading;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class PersistenceRoundTripTests
{
    [Fact]
    public async Task ProviderFreeWorkflowPreservesTemplateUploadAndGradeProvenance()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = database.Clock.UtcNow;
        var staffId = UlidId.New(now);
        var studentId = UlidId.New(now);
        var templateId = UlidId.New(now);
        var versionId = UlidId.New(now);
        var questionId = UlidId.New(now);
        var uploadId = UlidId.New(now);

        await using (var context = database.Factory.CreateDbContext())
        {
            context.StaffUsers.Add(CreateStaff(staffId, now));
            context.Students.Add(CreateStudent(studentId, now));
            context.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = staffId,
                Purpose = "template_source",
                DestinationType = "template_version",
                DestinationId = versionId,
                OriginalFileName = "漢字テスト.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 128,
                CurrentBytes = 128,
                ExpectedSha256 = new string('a', 64),
                FinalSha256 = new string('a', 64),
                IncomingRelativePath = $"incoming/uploads/{uploadId}.part",
                State = "completed",
                ExpiresAt = now.AddHours(24),
                IdempotencyKey = Guid.NewGuid().ToString(),
                CreatedAt = now,
                UpdatedAt = now
            });
            context.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "漢字確認テスト",
                Subject = "国語",
                Category = "漢字",
                State = "draft",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "draft",
                TargetTotalPointsMilli = 2_000,
                PipelineVersion = "local-v1",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.TemplateSources.Add(new TemplateSourceEntity
            {
                Id = UlidId.New(now),
                TemplateVersionId = versionId,
                UploadSessionId = uploadId,
                SourceRole = "blank_test",
                DisplayName = "空欄テスト",
                Ordinal = 0,
                UploadedByStaffUserId = staffId,
                CreatedAt = now
            });
            context.Questions.Add(new QuestionEntity
            {
                Id = questionId,
                TemplateVersionId = versionId,
                LogicalQuestionId = UlidId.New(now),
                OrderIndex = 0,
                DisplayLabel = "問1",
                QuestionText = "「おおき」を漢字で書きなさい。",
                QuestionType = "exact_short_text",
                GradingMode = "deterministic",
                MaxPointsMilli = 2_000,
                AllowNonKanji = false,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.AcceptedAnswers.Add(new AcceptedAnswerEntity
            {
                Id = UlidId.New(now),
                QuestionId = questionId,
                AnswerText = "大木",
                NormalizedText = "大木",
                VariantType = "canonical",
                TeacherVerified = true,
                AnswerProvenance = "teacher_entered",
                Locale = "ja-JP",
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync();

            var version = await context.TemplateVersions.SingleAsync(
                entity => entity.Id == versionId);
            version.State = "published";
            version.PublishedByStaffUserId = staffId;
            version.PublishedAt = now;
            version.ContentHash = new string('b', 64);
            await context.SaveChangesAsync();

            var template = await context.TestTemplates.SingleAsync(
                entity => entity.Id == templateId);
            template.State = "active";
            template.ActiveVersionId = versionId;
            await context.SaveChangesAsync();
        }

        var sessionId = UlidId.New(now);
        var submissionId = UlidId.New(now);
        var fileObjectId = UlidId.New(now);
        await using (var context = database.Factory.CreateDbContext())
        {
            context.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = new DateOnly(2026, 7, 27),
                Priority = "economy",
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = new string('c', 64),
                Bytes = 512,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = "scan/original/cc/cc/value.pdf",
                StorageClass = "managed_scan_original",
                RetentionClass = "scan_three_months",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1
            });
            context.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "awaiting_grading",
                ScanPayloadState = "scan_available",
                AssignedStudentId = studentId,
                AssignmentMethod = "teacher",
                AttemptNumber = 1,
                CanonicalForSession = true,
                UploadedByStaffUserId = staffId,
                OriginalFileName = "答案.pdf",
                OriginalFileObjectId = fileObjectId,
                UploadCompletedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync();
        }

        using var coordinator = new SemaphoreWriteCoordinator();
        var gradingStore = new EfProviderFreeGradingStore(
            database.Factory,
            coordinator,
            database.Clock);
        var gradingRunId = UlidId.New(now);
        var created = await gradingStore.CreateAsync(new ProviderFreeGradingRunDraft(
            gradingRunId,
            submissionId,
            versionId,
            1,
            "initial",
            new string('d', 64),
            [new QuestionDefinition(questionId, 2_000)],
            [
                new QuestionJudgment(
                    questionId,
                    2_000,
                    "correct",
                    "deterministic",
                    10_000,
                    "大木",
                    "大木",
                    "exact_match")
            ]));
        var loaded = await gradingStore.GetAsync(gradingRunId);

        Assert.Equal(created.GradingRunId, loaded!.GradingRunId);
        Assert.Equal(created.SubmissionId, loaded.SubmissionId);
        Assert.Equal(created.TemplateVersionId, loaded.TemplateVersionId);
        Assert.Equal(created.RunNumber, loaded.RunNumber);
        Assert.Equal(created.State, loaded.State);
        Assert.Equal(created.Judgments, loaded.Judgments);
        Assert.Equal(2_000, loaded!.EarnedPointsMilli);
        Assert.Equal(2_000, loaded.PossiblePointsMilli);
        Assert.Single(loaded.Judgments);

        await using var verify = database.Factory.CreateDbContext();
        var persistedSubmission = await verify.Submissions
            .AsNoTracking()
            .SingleAsync(entity => entity.Id == submissionId);
        var source = await verify.TemplateSources
            .AsNoTracking()
            .Include(entity => entity.UploadSession)
            .SingleAsync();
        var result = await verify.QuestionResults
            .AsNoTracking()
            .Include(entity => entity.Revisions)
            .SingleAsync();

        Assert.Equal(gradingRunId, persistedSubmission.CurrentGradingRunId);
        Assert.Equal("ready_to_finalize", persistedSubmission.State);
        Assert.Equal("blank_test", source.SourceRole);
        Assert.Equal("漢字テスト.pdf", source.UploadSession.OriginalFileName);
        Assert.Single(result.Revisions);
        Assert.Equal(result.Revisions.Single().Id, result.CurrentRevisionId);
        Assert.Single(await verify.OutboxEvents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task PublishedQuestionIsImmutable()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ids = await SeedPublishedTemplateAsync(database);

        await using var context = database.Factory.CreateDbContext();
        var question = await context.Questions.SingleAsync(entity => entity.Id == ids.QuestionId);
        question.QuestionText = "Mutated after publication";

        await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task PublishedQuestionRegionIsImmutable()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ids = await SeedPublishedTemplateAsync(database);

        await using var context = database.Factory.CreateDbContext();
        var region = await context.Regions.SingleAsync(
            entity => entity.Id == ids.RegionId);
        region.WidthMillionths = 250_000;

        await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());
    }

    private static StaffUserEntity CreateStaff(string id, DateTimeOffset now)
    {
        return new StaffUserEntity
        {
            Id = id,
            Username = "teacher",
            UsernameNormalized = "teacher",
            DisplayName = "先生",
            PasswordHash = "argon2id:test",
            PasswordAlgorithm = "argon2id",
            PasswordAlgorithmVersion = 1,
            Status = "active",
            CredentialChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static StudentEntity CreateStudent(string id, DateTimeOffset now)
    {
        return new StudentEntity
        {
            Id = id,
            StudentNumber = "S-1042",
            StudentNumberNormalized = "S-1042",
            FamilyName = "大木",
            GivenName = "花子",
            FamilyNameNormalized = "大木",
            GivenNameNormalized = "花子",
            DisplayName = "大木 花子",
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static async Task<(
        string QuestionId,
        string VersionId,
        string RegionId)> SeedPublishedTemplateAsync(
        TestDatabase database)
    {
        var now = database.Clock.UtcNow;
        var templateId = UlidId.New(now);
        var versionId = UlidId.New(now);
        var questionId = UlidId.New(now);
        var regionId = UlidId.New(now);

        await using var context = database.Factory.CreateDbContext();
        context.TestTemplates.Add(new TestTemplateEntity
        {
            Id = templateId,
            Title = "Test",
            State = "draft",
            CreatedByStaffUserId = UlidId.New(now),
            CreatedAt = now,
            UpdatedAt = now
        });
        context.TemplateVersions.Add(new TemplateVersionEntity
        {
            Id = versionId,
            TestTemplateId = templateId,
            VersionNumber = 1,
            State = "draft",
            PipelineVersion = "v1",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Regions.Add(new RegionEntity
        {
            Id = regionId,
            OwnerType = "question",
            OwnerId = questionId,
            PageNumber = 1,
            RegionType = "question",
            XMillionths = 100_000,
            YMillionths = 100_000,
            WidthMillionths = 200_000,
            HeightMillionths = 100_000,
            CreatedSource = "teacher",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Questions.Add(new QuestionEntity
        {
            Id = questionId,
            TemplateVersionId = versionId,
            LogicalQuestionId = UlidId.New(now),
            OrderIndex = 0,
            DisplayLabel = "Q1",
            QuestionText = "Question",
            QuestionType = "boolean",
            GradingMode = "deterministic",
            MaxPointsMilli = 1_000,
            QuestionRegionId = regionId,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var version = await context.TemplateVersions.SingleAsync(
            entity => entity.Id == versionId);
        version.State = "published";
        version.PublishedByStaffUserId = UlidId.New(now);
        version.PublishedAt = now;
        version.ContentHash = new string('e', 64);
        await context.SaveChangesAsync();
        return (questionId, versionId, regionId);
    }
}

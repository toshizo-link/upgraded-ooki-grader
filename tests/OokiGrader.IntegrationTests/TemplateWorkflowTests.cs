using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateWorkflowTests
{
    [Theory]
    [InlineData("中1社会_問題用紙.pdf", "blank_test", 9_000)]
    [InlineData("中1社会_模範解答.pdf", "separate_answer_key", 9_500)]
    [InlineData("中1社会_模範解答記入済み.pdf", "contains_model_answers", 9_500)]
    [InlineData("中1社会_模範解答入り.pdf", "contains_model_answers", 9_500)]
    [InlineData("model_answer_filled.pdf", "contains_model_answers", 9_500)]
    [InlineData("中1社会_解答付き.pdf", "contains_non_model_answers", 9_500)]
    [InlineData("中1社会_生徒答案_記入済み.pdf", "contains_non_model_answers", 9_500)]
    [InlineData("中1社会_非模範解答.pdf", "contains_non_model_answers", 9_500)]
    [InlineData("student_answers.pdf", "contains_non_model_answers", 9_500)]
    [InlineData("non_model_answers.pdf", "contains_non_model_answers", 9_500)]
    [InlineData("filled_exam.pdf", "contains_non_model_answers", 9_500)]
    [InlineData("completed_exam.pdf", "contains_non_model_answers", 9_500)]
    [InlineData("scan-2026-07-28.pdf", "blank_test", 5_000)]
    public void SourceRoleInferenceNeverElevatesAmbiguousFiles(
        string displayName,
        string expectedRole,
        int expectedConfidence)
    {
        var inferred = TemplateSourceRoleInference.Infer(displayName);

        Assert.Equal(expectedRole, inferred.SourceRole);
        Assert.Equal(expectedConfidence, inferred.ConfidenceBasisPoints);
    }

    [Fact]
    public async Task RoutesRequireAuthenticationAndTeacherMutationPolicy()
    {
        await using var application = await TemplateTestApplication.CreateAsync();

        var anonymous = await application.SendAsync(
            HttpMethod.Get,
            "/api/v1/templates",
            role: null);
        var readOnlyMutation = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/templates",
            "readOnlyReviewer",
            TemplateRequest());
        var teacherMutation = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/templates",
            "teacher",
            TemplateRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readOnlyMutation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, teacherMutation.StatusCode);
        Assert.NotNull(teacherMutation.Headers.ETag);
    }

    [Fact]
    public async Task AttachesNonModelAnswerSourceWithoutElevatingItsAnswers()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var upload = await AddCompletedTemplateSourceUploadAsync(
            application,
            "中1社会_生徒答案.pdf",
            'c');

        var response = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/sources",
            "teacher",
            new
            {
                uploadId = upload.UploadId,
                sourceRole = "containsNonModelAnswers",
                displayName = "中1社会_生徒答案.pdf",
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var attached = await ReadJsonAsync(response);
        Assert.Equal(
            "containsNonModelAnswers",
            RequiredString(attached, "sourceRole"));
        Assert.False(attached.GetProperty("sourceRoleInferred").GetBoolean());

        await application.WithDatabaseAsync(async db =>
        {
            var source = await db.TemplateSources.AsNoTracking().SingleAsync();
            Assert.Equal("contains_non_model_answers", source.SourceRole);
        });
    }

    [Fact]
    public async Task AttachedTemplateSourceCanBePreviewedInTheDraftEditor()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var upload = await AddCompletedTemplateSourceUploadAsync(
            application,
            "中1社会_問題用紙.pdf",
            'f');
        var bytes = Enumerable.Repeat((byte)0x25, 456).ToArray();
        application.AddContent(
            new ContentObjectLocator(
                ContentStorageClass.TemplateSource,
                new string('f', 64),
                bytes.Length,
                "pdf"),
            bytes);

        var attachedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/sources",
            "teacher",
            new
            {
                uploadId = upload.UploadId,
                sourceRole = "blankTest",
                displayName = "中1社会_問題用紙.pdf",
            });
        Assert.Equal(HttpStatusCode.Created, attachedResponse.StatusCode);
        var attached = await ReadJsonAsync(attachedResponse);
        var sourceId = RequiredString(attached, "id");
        var expectedUrl =
            $"/api/v1/templates/{templateId}/versions/{versionId}" +
            $"/sources/{sourceId}/content";
        Assert.Equal(expectedUrl, RequiredString(attached, "contentUrl"));
        Assert.Equal("application/pdf", RequiredString(attached, "mimeType"));

        var versionResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
        var version = await ReadJsonAsync(versionResponse);
        var source = Assert.Single(version.GetProperty("sources").EnumerateArray());
        Assert.Equal(expectedUrl, RequiredString(source, "contentUrl"));
        Assert.Equal("application/pdf", RequiredString(source, "mimeType"));

        var previewResponse = await application.SendAsync(
            HttpMethod.Get,
            expectedUrl,
            "teacher");
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.Equal(
            "application/pdf",
            previewResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await previewResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", previewResponse.Headers.CacheControl?.ToString());

        var anonymousResponse = await application.SendAsync(
            HttpMethod.Get,
            expectedUrl,
            role: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var wrongTemplateResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{UlidId.New()}/versions/{versionId}" +
            $"/sources/{sourceId}/content",
            "teacher");
        Assert.Equal(HttpStatusCode.NotFound, wrongTemplateResponse.StatusCode);
    }

    [Fact]
    public async Task SourceAttachmentAndManualGenerationSupportQuestionReordering()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var uploadId = UlidId.New();
        var fileReferenceId = UlidId.New();

        await application.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var fileObjectId = UlidId.New(now);
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = new string('a', 64),
                Bytes = 123,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = "template-source/aa/source.pdf",
                StorageClass = "TemplateSource",
                RetentionClass = "template_source",
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = "問題用紙.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 123,
                CurrentBytes = 123,
                FinalSha256 = new string('a', 64),
                IncomingRelativePath = "test/source",
                State = "completed",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = fileReferenceId,
                FileObjectId = fileObjectId,
                OwnerType = "upload_session",
                OwnerId = uploadId,
                Purpose = "template_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var attachedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/sources",
            "teacher",
            new
            {
                uploadId,
                displayName = "問題用紙.pdf",
            });
        Assert.Equal(HttpStatusCode.Created, attachedResponse.StatusCode);
        var attached = await ReadJsonAsync(attachedResponse);
        Assert.Equal("blankTest", RequiredString(attached, "sourceRole"));
        Assert.True(
            attached.GetProperty("sourceRoleInferred").GetBoolean());
        Assert.Equal(
            9_000,
            attached.GetProperty(
                "sourceRoleConfidenceBasisPoints").GetInt32());
        Assert.Equal(
            "filename_blank_test",
            RequiredString(attached, "sourceRoleInferenceReason"));

        var generationResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}/generation",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, generationResponse.StatusCode);
        var generation = await ReadJsonAsync(generationResponse);
        Assert.Equal("manual", RequiredString(generation, "state"));

        var unavailableGeneration = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:generateDraft",
            "teacher",
            new { priority = "economy" });
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            unavailableGeneration.StatusCode);

        var firstResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            ExactQuestionRequest("第一問", 1000));
        var secondResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            ExactQuestionRequest("第二問", 1000, "問2", 2));
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var firstId = RequiredString(await ReadJsonAsync(firstResponse), "id");
        var secondId = RequiredString(await ReadJsonAsync(secondResponse), "id");

        var reorderedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions:reorder",
            "teacher",
            new { questionIds = new[] { secondId, firstId } });
        Assert.Equal(HttpStatusCode.OK, reorderedResponse.StatusCode);
        var reordered = await ReadJsonAsync(reorderedResponse);
        var items = reordered.GetProperty("items");
        Assert.Equal(secondId, RequiredString(items[0], "id"));
        Assert.Equal(1, items[0].GetProperty("order").GetInt32());
        Assert.Equal(firstId, RequiredString(items[1], "id"));
        Assert.Equal(2, items[1].GetProperty("order").GetInt32());

        await application.WithDatabaseAsync(async db =>
        {
            var source = await db.TemplateSources
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(fileReferenceId, source.FileReferenceId);
            Assert.Equal("blank_test", source.SourceRole);
        });
    }

    [Fact]
    public async Task ExactPublishedSourceMatchRequiresIdenticalFileAndRole()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var original = await AddCompletedTemplateSourceUploadAsync(
            application,
            "中1社会_問題用紙.pdf",
            'b');

        var attachedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/sources",
            "teacher",
            new
            {
                uploadId = original.UploadId,
                sourceRole = "containsModelAnswers",
                displayName = "中1社会_模範解答記入済み.pdf",
            });
        Assert.Equal(HttpStatusCode.Created, attachedResponse.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions.SingleAsync(item =>
                item.Id == versionId);
            version.State = "published";
            version.PublishedByStaffUserId = TestAuthenticationHandler.StaffId;
            version.PublishedAt = DateTimeOffset.UtcNow;
            version.ContentHash = new string('a', 64);
            await db.SaveChangesAsync();
        });

        var duplicate = await AddCompletedTemplateSourceUploadAsync(
            application,
            "scan-copy.pdf",
            'b',
            original.FileObjectId);
        var missingRoleResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/source-match?uploadIds={duplicate.UploadId}",
            "teacher");
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            missingRoleResponse.StatusCode);

        var conflictingRoleResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/source-match?uploadIds={duplicate.UploadId}" +
            "&sourceRoles=containsNonModelAnswers",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, conflictingRoleResponse.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await ReadJsonAsync(conflictingRoleResponse))
            .GetProperty("exactMatch")
            .ValueKind);

        var exactMatchResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/source-match?uploadIds={duplicate.UploadId}" +
            "&sourceRoles=containsModelAnswers",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, exactMatchResponse.StatusCode);
        var exactMatch = (await ReadJsonAsync(exactMatchResponse))
            .GetProperty("exactMatch");
        Assert.Equal(templateId, RequiredString(exactMatch, "templateId"));
        Assert.Equal(versionId, RequiredString(exactMatch, "versionId"));
        Assert.Equal(
            "containsModelAnswers",
            RequiredString(exactMatch.GetProperty("sources")[0], "sourceRole"));
        Assert.Matches(
            "^[0-9a-f]{64}$",
            RequiredString(exactMatch, "contentHash"));

        var different = await AddCompletedTemplateSourceUploadAsync(
            application,
            "別の問題用紙.pdf",
            'c');
        var noMatchResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/source-match?uploadIds={different.UploadId}" +
            "&sourceRoles=containsModelAnswers",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, noMatchResponse.StatusCode);
        var noMatch = await ReadJsonAsync(noMatchResponse);
        Assert.Equal(
            JsonValueKind.Null,
            noMatch.GetProperty("exactMatch").ValueKind);
    }

    [Fact]
    public async Task BulkProposalVerificationConfirmsSafeItemsAndLeavesBlockersDraft()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var source = await AddCompletedTemplateSourceUploadAsync(
            application,
            "中1社会_問題用紙.pdf",
            'd');
        var attachedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/sources",
            "teacher",
            new
            {
                uploadId = source.UploadId,
                sourceRole = "blankTest",
                displayName = "中1社会_問題用紙.pdf",
            });
        Assert.Equal(HttpStatusCode.Created, attachedResponse.StatusCode);

        var safeResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            AiProposedQuestionRequest(
                displayLabel: "問1",
                order: 1,
                questionText: "東南アジアの地域協力機構を答えなさい。"));
        var blockedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            AiProposedQuestionRequest(
                displayLabel: "問2",
                order: 2,
                questionText: "インドで主に信仰される宗教を答えなさい。"));
        var subjectiveResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            AiProposedQuestionRequest(
                displayLabel: "問3",
                order: 3,
                questionText: "資料から読み取れる変化を説明しなさい。"));
        Assert.Equal(HttpStatusCode.Created, safeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, blockedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, subjectiveResponse.StatusCode);
        var safeQuestionId = RequiredString(
            await ReadJsonAsync(safeResponse),
            "id");
        var blockedQuestionId = RequiredString(
            await ReadJsonAsync(blockedResponse),
            "id");
        var subjectiveQuestionId = RequiredString(
            await ReadJsonAsync(subjectiveResponse),
            "id");

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions
                .Include(item => item.Questions)
                .SingleAsync(item => item.Id == versionId);
            version.AiGenerationProvenanceId = "ai-request-template-test";
            var safe = version.Questions
                .Single(item => item.Id == safeQuestionId);
            safe.AiConfidenceBasisPoints = 9_800;
            safe.TeacherNote =
                "[AI確認] [question.ocr_noise_corrected] 明らかなOCRノイズを補正しました。";
            version.Questions
                .Single(item => item.Id == blockedQuestionId)
                .AiConfidenceBasisPoints = 9_000;
            var subjective = version.Questions
                .Single(item => item.Id == subjectiveQuestionId);
            subjective.AiConfidenceBasisPoints = 9_800;
            subjective.QuestionType = "subjective";
            await db.SaveChangesAsync();
        });

        var versionBeforeVerification = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var verifiedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions:verifyProposals",
            "teacher",
            new { selectionMode = "allNonBlocking" },
            RequiredEtag(versionBeforeVerification),
            UlidId.New());
        Assert.Equal(HttpStatusCode.OK, verifiedResponse.StatusCode);
        Assert.NotNull(verifiedResponse.Headers.ETag);
        var verified = await ReadJsonAsync(verifiedResponse);
        Assert.Equal(
            1,
            verified.GetProperty("verifiedQuestionCount").GetInt32());
        Assert.Equal(
            1,
            verified.GetProperty("verifiedAnswerCount").GetInt32());
        Assert.Equal(
            2,
            verified.GetProperty("skippedQuestionCount").GetInt32());
        var issues = verified.GetProperty("issues")
            .EnumerateArray()
            .ToDictionary(
                issue => RequiredString(issue, "questionId"),
                StringComparer.Ordinal);
        Assert.Equal(
            "question.low_confidence",
            RequiredString(issues[blockedQuestionId], "code"));
        Assert.Equal(
            "question.individual_review_required",
            RequiredString(issues[subjectiveQuestionId], "code"));
        Assert.All(issues.Values, issue =>
            Assert.True(issue.GetProperty("blocking").GetBoolean()));

        var questions = verified.GetProperty("questions")
            .EnumerateArray()
            .ToDictionary(
                item => RequiredString(item, "id"),
                StringComparer.Ordinal);
        Assert.True(
            questions[safeQuestionId].GetProperty("teacherVerified").GetBoolean());
        Assert.True(
            questions[safeQuestionId]
                .GetProperty("acceptedAnswers")[0]
                .GetProperty("teacherVerified")
                .GetBoolean());
        Assert.False(
            questions[blockedQuestionId]
                .GetProperty("teacherVerified")
                .GetBoolean());
        Assert.False(
            questions[blockedQuestionId]
                .GetProperty("acceptedAnswers")[0]
                .GetProperty("teacherVerified")
                .GetBoolean());
        Assert.False(
            questions[subjectiveQuestionId]
                .GetProperty("teacherVerified")
                .GetBoolean());

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions
                .AsNoTracking()
                .SingleAsync(item => item.Id == versionId);
            Assert.Equal("draft", version.State);
            Assert.Null(version.PublishedAt);
            Assert.Null(version.ContentHash);

            var persisted = await db.Questions
                .AsNoTracking()
                .Include(item => item.AcceptedAnswers)
                .Where(item => item.TemplateVersionId == versionId)
                .ToDictionaryAsync(item => item.Id);
            Assert.True(persisted[safeQuestionId].TeacherVerified);
            Assert.All(
                persisted[safeQuestionId].AcceptedAnswers,
                answer => Assert.True(answer.TeacherVerified));
            Assert.False(persisted[blockedQuestionId].TeacherVerified);
            Assert.All(
                persisted[blockedQuestionId].AcceptedAnswers,
                answer => Assert.False(answer.TeacherVerified));
            Assert.False(persisted[subjectiveQuestionId].TeacherVerified);
            Assert.All(
                persisted[subjectiveQuestionId].AcceptedAnswers,
                answer => Assert.False(answer.TeacherVerified));

            Assert.True(
                await db.AuditEvents
                    .AsNoTracking()
                    .AnyAsync(item =>
                        item.EventType == "template.proposals_verified"
                        && item.ObjectId == versionId));
        });
    }

    [Fact]
    public async Task BulkProposalVerificationAcceptsPageLevelModelAnswerEvidence()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var upload = await AddCompletedTemplateSourceUploadAsync(
            application,
            "中1理科_模範解答記入済み.png",
            'e');
        var attachedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/sources",
            "teacher",
            new
            {
                uploadId = upload.UploadId,
                sourceRole = "containsModelAnswers",
                displayName = "中1理科_模範解答記入済み.png",
            });
        Assert.Equal(HttpStatusCode.Created, attachedResponse.StatusCode);

        var questionResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "①",
                order = 1,
                questionText = "私たちは、［　］がないと物を見ることができない。",
                questionType = "exact_short_text",
                gradingMode = "transcribe_then_rules",
                maxPointsMilli = 1_000,
                pointIncrementMilli = 1_000,
                allowNonKanji = false,
                acceptedAnswers = new[]
                {
                    new
                    {
                        text = "光",
                        variantType = "canonical",
                        provenance = "provided_model_answer",
                        sourceFileReferenceId = upload.FileReferenceId,
                        sourcePageNumber = 1,
                        teacherVerified = false,
                    },
                },
                requiresReviewAlways = false,
                teacherVerified = false,
            });
        Assert.Equal(HttpStatusCode.Created, questionResponse.StatusCode);
        var questionId = RequiredString(
            await ReadJsonAsync(questionResponse),
            "id");

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions
                .Include(item => item.Questions)
                .SingleAsync(item => item.Id == versionId);
            version.AiGenerationProvenanceId = "ai-request-template-page-evidence";
            version.Questions.Single(item => item.Id == questionId)
                .AiConfidenceBasisPoints = 9_800;
            await db.SaveChangesAsync();
        });

        var versionResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var verifiedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions:verifyProposals",
            "teacher",
            new { selectionMode = "allNonBlocking" },
            RequiredEtag(versionResponse),
            UlidId.New());

        Assert.Equal(HttpStatusCode.OK, verifiedResponse.StatusCode);
        var verified = await ReadJsonAsync(verifiedResponse);
        Assert.Equal(
            1,
            verified.GetProperty("verifiedQuestionCount").GetInt32());
        Assert.Equal(
            1,
            verified.GetProperty("verifiedAnswerCount").GetInt32());
        Assert.Equal(
            0,
            verified.GetProperty("skippedQuestionCount").GetInt32());
        Assert.Empty(verified.GetProperty("issues").EnumerateArray());
    }

    [Fact]
    public async Task QuestionPersistsDefaultPointsRubricNotesAndRegions()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            defaultPointsMilli: 1_750);

        var createdResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "問1",
                order = 1,
                questionText = "説明しなさい。",
                questionType = "semantic_short_text",
                gradingMode = "ai_rubric",
                allowNonKanji = true,
                canonicalAnswer = "説明",
                rubric = "要点Aを含む場合は1点。",
                teacherNote = "採点者だけが確認するメモ",
                questionRegion = new
                {
                    pageNumber = 1,
                    xMillionths = 100_000,
                    yMillionths = 150_000,
                    widthMillionths = 600_000,
                    heightMillionths = 100_000,
                },
                answerRegion = new
                {
                    pageNumber = 1,
                    xMillionths = 100_000,
                    yMillionths = 300_000,
                    widthMillionths = 700_000,
                    heightMillionths = 200_000,
                },
                requiresReviewAlways = true,
            });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await ReadJsonAsync(createdResponse);
        Assert.Equal(1_750, created.GetProperty("maxPointsMilli").GetInt64());
        Assert.Equal(
            "要点Aを含む場合は1点。",
            created.GetProperty("rubric").GetString());
        Assert.Equal(
            "採点者だけが確認するメモ",
            created.GetProperty("teacherNote").GetString());
        Assert.Equal(
            700_000,
            created.GetProperty("answerRegion")
                .GetProperty("widthMillionths")
                .GetInt32());

        var versionResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var version = await ReadJsonAsync(versionResponse);
        Assert.Equal(
            1_750,
            version.GetProperty("defaultPointsMilli").GetInt64());
        Assert.Equal(
            150_000,
            version.GetProperty("questions")[0]
                .GetProperty("questionRegion")
                .GetProperty("yMillionths")
                .GetInt32());

        await application.WithDatabaseAsync(async db =>
        {
            var regions = await db.Regions
                .AsNoTracking()
                .OrderBy(item => item.RegionType)
                .ToListAsync();
            Assert.Equal(2, regions.Count);
            Assert.All(regions, region => Assert.Equal("question", region.OwnerType));
        });
    }

    [Fact]
    public async Task ManualWorkflowUsesEtagsPublishesHashAndBecomesImmutable()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var questionBody = ExactQuestionRequest(
            questionText: "次の語を漢字で書きなさい。",
            maximumPointsMilli: 1500);

        var createdQuestion = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            questionBody);
        Assert.Equal(HttpStatusCode.Created, createdQuestion.StatusCode);
        var question = await ReadJsonAsync(createdQuestion);
        var questionId = RequiredString(question, "id");
        var questionEtag = RequiredEtag(createdQuestion);

        var missingRevision = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            ExactQuestionRequest("変更", 1500));
        Assert.Equal((HttpStatusCode)428, missingRevision.StatusCode);

        var staleRevision = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            ExactQuestionRequest("変更", 1500),
            "\"rev-999\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleRevision.StatusCode);
        Assert.Equal(questionEtag, staleRevision.Headers.ETag?.Tag);

        var updatedQuestion = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            ExactQuestionRequest("更新した問題文", 1500),
            questionEtag);
        Assert.Equal(HttpStatusCode.OK, updatedQuestion.StatusCode);
        var updatedQuestionEtag = RequiredEtag(updatedQuestion);
        Assert.NotEqual(questionEtag, updatedQuestionEtag);

        var validation = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:validate",
            "teacher",
            new { });
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        var validationJson = await ReadJsonAsync(validation);
        Assert.True(validationJson.GetProperty("valid").GetBoolean());
        Assert.Equal(1, validationJson.GetProperty("questionCount").GetInt32());
        Assert.Equal(1500, validationJson.GetProperty("totalPointsMilli").GetInt64());
        Assert.Equal(1, validationJson.GetProperty("kanjiRequiredCount").GetInt32());

        var publishWithoutRevision = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            new { });
        Assert.Equal((HttpStatusCode)428, publishWithoutRevision.StatusCode);

        var versionResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var versionEtag = RequiredEtag(versionResponse);
        var publishedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            new { },
            versionEtag);
        Assert.Equal(HttpStatusCode.OK, publishedResponse.StatusCode);
        var published = await ReadJsonAsync(publishedResponse);
        Assert.Equal("published", RequiredString(published, "state"));
        Assert.Matches("^[0-9a-f]{64}$", RequiredString(published, "contentHash"));

        var addAfterPublish = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            ExactQuestionRequest("追加不可", 1000));
        var deleteAfterPublish = await application.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            body: null,
            etag: updatedQuestionEtag);
        Assert.Equal(HttpStatusCode.Conflict, addAfterPublish.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteAfterPublish.StatusCode);

        var templateResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}",
            "teacher");
        var template = await ReadJsonAsync(templateResponse);
        Assert.Equal("active", RequiredString(template, "lifecycleState"));
        Assert.Equal(versionId, RequiredString(template, "activeVersionId"));

        await application.WithDatabaseAsync(async db =>
        {
            var events = await db.AuditEvents
                .AsNoTracking()
                .OrderBy(item => item.OccurredAt)
                .Select(item => new { item.EventType, item.SafeMetadataJson })
                .ToListAsync();
            Assert.Contains(events, item => item.EventType == "template.created");
            Assert.Contains(events, item => item.EventType == "template_version.created");
            Assert.Contains(events, item => item.EventType == "template.question_created");
            Assert.Contains(events, item => item.EventType == "template.question_updated");
            Assert.Contains(events, item => item.EventType == "template_version.published");
            Assert.All(
                events,
                item => Assert.DoesNotContain(
                    "漢字",
                    item.SafeMetadataJson ?? string.Empty,
                    StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task SuppliedAnswerRequiresAuthorityAndSurvivesEditAndClone()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);

        var missingAuthority = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "問1",
                order = 1,
                questionText = "次の語を書きなさい。",
                questionType = "exact_short_text",
                gradingMode = "transcribe_then_rules",
                maxPointsMilli = 1000,
                allowNonKanji = false,
                acceptedAnswers = new[]
                {
                    new
                    {
                        text = "大きい",
                        variantType = "canonical",
                        provenance = "provided_model_answer",
                    },
                },
                requiresReviewAlways = false,
            });
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            missingAuthority.StatusCode);

        var sourceFileReferenceId = UlidId.New();
        await application.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var uploadId = UlidId.New(now);
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                Purpose = "template_source",
                OriginalFileName = "模範解答.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 0,
                CurrentBytes = 0,
                IncomingRelativePath = "test/source",
                State = "completed",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TemplateSources.Add(new TemplateSourceEntity
            {
                Id = UlidId.New(now.AddTicks(1)),
                TemplateVersionId = versionId,
                UploadSessionId = uploadId,
                FileReferenceId = sourceFileReferenceId,
                SourceRole = "contains_model_answers",
                DisplayName = "模範解答.pdf",
                Ordinal = 0,
                UploadedByStaffUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var createdResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "問1",
                order = 1,
                questionText = "次の語を書きなさい。",
                questionType = "exact_short_text",
                gradingMode = "transcribe_then_rules",
                maxPointsMilli = 1000,
                allowNonKanji = false,
                acceptedAnswers = new[]
                {
                    new
                    {
                        text = "大きい",
                        variantType = "canonical",
                        provenance = "provided_model_answer",
                        sourceFileReferenceId,
                        sourcePageNumber = 1,
                    },
                },
                requiresReviewAlways = false,
            });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await ReadJsonAsync(createdResponse);
        var questionId = RequiredString(created, "id");

        var editedResponse = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            new
            {
                displayLabel = "問1",
                order = 1,
                questionText = "次の語を書きなさい。",
                questionType = "exact_short_text",
                gradingMode = "transcribe_then_rules",
                maxPointsMilli = 1000,
                allowNonKanji = false,
                canonicalAnswer = "大きな",
                acceptedAnswers = new[]
                {
                    new { text = "大きな", variantType = "canonical" },
                },
                requiresReviewAlways = false,
            },
            RequiredEtag(createdResponse));
        Assert.Equal(HttpStatusCode.OK, editedResponse.StatusCode);
        var edited = await ReadJsonAsync(editedResponse);
        var suppliedAnswer = edited
            .GetProperty("acceptedAnswers")
            .EnumerateArray()
            .Single();
        Assert.Equal(
            "provided_model_answer",
            RequiredString(suppliedAnswer, "provenance"));
        Assert.Equal(
            sourceFileReferenceId,
            RequiredString(suppliedAnswer, "sourceFileReferenceId"));
        Assert.Equal(1, suppliedAnswer.GetProperty("sourcePageNumber").GetInt32());

        var versionResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var publishResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            new { },
            RequiredEtag(versionResponse));
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

        var cloneResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions",
            "teacher",
            new { sourceVersionId = versionId });
        Assert.Equal(HttpStatusCode.Created, cloneResponse.StatusCode);
        var clone = await ReadJsonAsync(cloneResponse);
        Assert.Equal("draft", RequiredString(clone, "state"));
        Assert.Empty(clone.GetProperty("pages").EnumerateArray());
        Assert.Single(clone.GetProperty("sources").EnumerateArray());
        var clonedAnswer = clone
            .GetProperty("questions")[0]
            .GetProperty("acceptedAnswers")[0];
        Assert.Equal(
            "provided_model_answer",
            RequiredString(clonedAnswer, "provenance"));
        Assert.Equal(
            sourceFileReferenceId,
            RequiredString(clonedAnswer, "sourceFileReferenceId"));
    }

    [Fact]
    public async Task PublishBlocksUnverifiedContentAndAcceptsIntegerMilliPoints()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var createResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "問1",
                order = 1,
                questionText = "答えを書きなさい。",
                questionType = "exact_short_text",
                gradingMode = "transcribe_then_rules",
                maxPointsMilli = 1001,
                allowNonKanji = false,
                teacherVerified = false,
                acceptedAnswers = new[]
                {
                    new
                    {
                        text = "漢字",
                        variantType = "canonical",
                        provenance = "teacher_entered",
                        teacherVerified = false,
                    },
                },
                requiresReviewAlways = false,
            });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadJsonAsync(createResponse);
        var questionId = RequiredString(created, "id");

        var invalidReportResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:validate",
            "teacher",
            new { });
        var invalidReport = await ReadJsonAsync(invalidReportResponse);
        Assert.False(invalidReport.GetProperty("valid").GetBoolean());
        var issueCodes = invalidReport
            .GetProperty("issues")
            .EnumerateArray()
            .Select(issue => RequiredString(issue, "code"))
            .ToArray();
        Assert.Contains("question.not_teacher_verified", issueCodes);
        Assert.Contains("answer.not_teacher_verified", issueCodes);

        var versionBeforeRejectedPublish = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var rejectedPublish = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            new { },
            RequiredEtag(versionBeforeRejectedPublish));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            rejectedPublish.StatusCode);

        var corrected = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            new
            {
                teacherVerified = true,
                acceptedAnswers = new[]
                {
                    new
                    {
                        text = "漢字",
                        variantType = "canonical",
                        provenance = "teacher_entered",
                        teacherVerified = true,
                    },
                },
            },
            RequiredEtag(createResponse));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        var validReportResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:validate",
            "teacher",
            new { });
        var validReport = await ReadJsonAsync(validReportResponse);
        Assert.True(validReport.GetProperty("valid").GetBoolean());
        Assert.Equal(1001, validReport.GetProperty("totalPointsMilli").GetInt64());
    }

    [Fact]
    public async Task VerifiedQuestionCannotHideMissingPhysicalAnswerSlot()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var createResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "⑧",
                order = 1,
                questionText = "光の性質を［　］という。",
                questionType = "exact_short_text",
                gradingMode = "transcribe_then_rules",
                maxPointsMilli = 1_000,
                pointIncrementMilli = 1_000,
                allowNonKanji = false,
                teacherVerified = true,
                acceptedAnswers = new[]
                {
                    new
                    {
                        text = "反射",
                        variantType = "canonical",
                        provenance = "teacher_entered",
                        teacherVerified = true,
                    },
                },
                requiresReviewAlways = false,
            });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions
                .Include(item => item.Questions)
                .SingleAsync(item => item.Id == versionId);
            version.AiGenerationProvenanceId = "ai-request-missing-slot";
            version.Questions.Single().TeacherNote =
                "[AI確認] [template.answer_slot_inventory_mismatch] " +
                "検出した解答欄は2個ですが、個別問題は1件です。";
            await db.SaveChangesAsync();
        });

        var validationResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:validate",
            "teacher",
            new { });
        Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
        var validation = await ReadJsonAsync(validationResponse);
        Assert.False(validation.GetProperty("valid").GetBoolean());
        var issue = Assert.Single(
            validation.GetProperty("issues").EnumerateArray(),
            item => RequiredString(item, "code") ==
                "template.answer_slot_inventory_mismatch");
        Assert.True(issue.GetProperty("blocking").GetBoolean());
    }

    private static object TemplateRequest(long defaultPointsMilli = 1_000) =>
        new
        {
            title = "漢字確認テスト",
            subject = "国語",
            category = "漢字",
            gradeLabel = "中学1年",
            course = "標準",
            defaultPointsMilli,
        };

    private static object ExactQuestionRequest(
        string questionText,
        long maximumPointsMilli,
        string displayLabel = "問1",
        int order = 1) =>
        new
        {
            displayLabel,
            order,
            questionText,
            questionType = "exact_short_text",
            gradingMode = "transcribe_then_rules",
            maxPointsMilli = maximumPointsMilli,
            allowNonKanji = false,
            canonicalAnswer = "漢字",
            acceptedAnswers = new object[]
            {
                new
                {
                    text = "漢字",
                    variantType = "canonical",
                    provenance = "teacher_entered",
                },
                new
                {
                    text = "かんじ",
                    variantType = "explicitException",
                    provenance = "teacher_entered",
                    isExplicitNonKanjiException = true,
                },
            },
            requiresReviewAlways = false,
            teacherVerified = true,
        };

    private static object AiProposedQuestionRequest(
        string displayLabel,
        int order,
        string questionText) =>
        new
        {
            displayLabel,
            order,
            questionText,
            questionType = "exact_short_text",
            gradingMode = "transcribe_then_rules",
            maxPointsMilli = 1_000,
            pointIncrementMilli = 1_000,
            allowNonKanji = false,
            acceptedAnswers = new[]
            {
                new
                {
                    text = "ASEAN",
                    variantType = "canonical",
                    provenance = "ai_proposed",
                    teacherVerified = false,
                },
            },
            questionRegion = new
            {
                pageNumber = 1,
                xMillionths = 50_000,
                yMillionths = 50_000 + (order - 1) * 300_000,
                widthMillionths = 850_000,
                heightMillionths = 100_000,
            },
            answerRegion = new
            {
                pageNumber = 1,
                xMillionths = 50_000,
                yMillionths = 170_000 + (order - 1) * 300_000,
                widthMillionths = 850_000,
                heightMillionths = 100_000,
            },
            requiresReviewAlways = false,
            teacherVerified = false,
        };

    private static async Task<(
        string UploadId,
        string FileReferenceId,
        string FileObjectId)> AddCompletedTemplateSourceUploadAsync(
            TemplateTestApplication application,
            string originalFileName,
            char shaCharacter,
            string? existingFileObjectId = null)
    {
        var uploadId = UlidId.New();
        var fileReferenceId = UlidId.New();
        var fileObjectId = existingFileObjectId ?? UlidId.New();
        await application.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            if (existingFileObjectId is null)
            {
                db.FileObjects.Add(new FileObjectEntity
                {
                    Id = fileObjectId,
                    Sha256 = new string(shaCharacter, 64),
                    Bytes = 456,
                    VerifiedMime = "application/pdf",
                    Extension = "pdf",
                    RelativeObjectPath =
                        $"template-source/{shaCharacter}/source.pdf",
                    StorageClass = "TemplateSource",
                    RetentionClass = "template_source",
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 1,
                });
            }

            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = originalFileName,
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 456,
                CurrentBytes = 456,
                FinalSha256 = new string(shaCharacter, 64),
                IncomingRelativePath = $"test/{uploadId}",
                State = "completed",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = fileReferenceId,
                FileObjectId = fileObjectId,
                OwnerType = "upload_session",
                OwnerId = uploadId,
                Purpose = "template_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        return (uploadId, fileReferenceId, fileObjectId);
    }

    private static async Task<(string TemplateId, string VersionId)>
        CreateTemplateAndVersionAsync(
            TemplateTestApplication application,
            bool addBlankSource = true,
            long defaultPointsMilli = 1_000)
    {
        var templateResponse = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/templates",
            "teacher",
            TemplateRequest(defaultPointsMilli));
        Assert.Equal(HttpStatusCode.Created, templateResponse.StatusCode);
        var template = await ReadJsonAsync(templateResponse);
        var templateId = RequiredString(template, "id");

        var versionResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions",
            "teacher",
            new { sourceVersionId = (string?)null });
        Assert.Equal(HttpStatusCode.Created, versionResponse.StatusCode);
        var version = await ReadJsonAsync(versionResponse);
        var versionId = RequiredString(version, "id");
        if (addBlankSource)
        {
            await application.WithDatabaseAsync(async db =>
            {
                var now = DateTimeOffset.UtcNow;
                var uploadId = UlidId.New(now);
                db.UploadSessions.Add(new UploadSessionEntity
                {
                    Id = uploadId,
                    CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                    Purpose = "template_source",
                    DestinationType = "template_source",
                    OriginalFileName = "問題用紙.pdf",
                    DeclaredMimeType = "application/pdf",
                    ExpectedBytes = 0,
                    CurrentBytes = 0,
                    IncomingRelativePath = "test/default-source",
                    State = "completed",
                    ExpiresAt = now.AddHours(1),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.TemplateSources.Add(new TemplateSourceEntity
                {
                    Id = UlidId.New(now.AddTicks(1)),
                    TemplateVersionId = versionId,
                    UploadSessionId = uploadId,
                    SourceRole = "blank_test",
                    DisplayName = "問題用紙.pdf",
                    Ordinal = 0,
                    UploadedByStaffUserId = TestAuthenticationHandler.StaffId,
                    CreatedAt = now,
                });
                await db.SaveChangesAsync();
            });
        }

        return (templateId, versionId);
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Undefined, value.ValueKind);
        return value;
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        var result = value.GetProperty(propertyName).GetString();
        Assert.False(string.IsNullOrWhiteSpace(result));
        return result!;
    }

    private static string RequiredEtag(HttpResponseMessage response)
    {
        var value = response.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(value));
        return value!;
    }

    private sealed class TemplateTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private TemplateTestApplication(
            IHost host,
            SqliteConnection connection)
        {
            _host = host;
            _connection = connection;
            Client = host.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(5);
            ContentStore = host.Services.GetRequiredService<TestContentStore>();
        }

        public HttpClient Client { get; }
        private TestContentStore ContentStore { get; }

        public static async Task<TemplateTestApplication> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var hostBuilder = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddDataProtection();
                        services.AddSingleton<ProtectedCursorCodec>();
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton<TestContentStore>();
                        services.AddSingleton<IContentStore>(provider =>
                            provider.GetRequiredService<TestContentStore>());
                        services.AddSingleton(connection);
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services
                            .AddAuthentication(TestAuthenticationHandler.SchemeName)
                            .AddScheme<
                                AuthenticationSchemeOptions,
                                TestAuthenticationHandler>(
                                TestAuthenticationHandler.SchemeName,
                                _ => { });
                        services.AddAuthorizationBuilder()
                            .SetFallbackPolicy(
                                new AuthorizationPolicyBuilder(
                                    TestAuthenticationHandler.SchemeName)
                                    .RequireAuthenticatedUser()
                                    .Build())
                            .AddPolicy(
                                "teacher",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole("administrator", "teacher"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseEndpoints(
                            endpoints =>
                            {
                                endpoints.MapTemplatesEndpoints();
                                endpoints.MapTemplateAutomationEndpoints();
                            });
                    });
                });

            var host = hostBuilder.Build();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return new TemplateTestApplication(host, connection);
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            string? role,
            object? body = null,
            string? etag = null,
            string? idempotencyKey = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (role is not null)
            {
                request.Headers.Add(TestAuthenticationHandler.RoleHeader, role);
            }

            if (etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", etag);
            }

            if (idempotencyKey is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    "Idempotency-Key",
                    idempotencyKey);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return await Client.SendAsync(request);
        }

        public async Task WithDatabaseAsync(
            Func<OokiGraderDbContext, Task> action)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OokiGraderDbContext>();
            await action(db);
        }

        public void AddContent(ContentObjectLocator locator, byte[] bytes) =>
            ContentStore.Add(locator, bytes);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestContentStore : IContentStore
    {
        private readonly Dictionary<ContentObjectLocator, byte[]> _objects = [];
        private readonly Lock _lock = new();

        public void Add(ContentObjectLocator locator, byte[] bytes)
        {
            lock (_lock)
            {
                _objects[locator] = bytes.ToArray();
            }
        }

        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_objects.TryGetValue(locator, out var bytes))
                {
                    throw new FileNotFoundException();
                }

                return Task.FromResult<Stream>(
                    new MemoryStream(bytes.ToArray(), writable: false));
            }
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                return Task.FromResult(_objects.ContainsKey(locator));
            }
        }

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _objects.Remove(locator);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string SchemeName = "TemplateIntegrationTest";
        public const string RoleHeader = "X-Test-Role";
        public const string StaffId = "01J00000000000000000000000";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, StaffId),
                new Claim(ClaimTypes.Name, "integration-teacher"),
                new Claim(ClaimTypes.Role, role),
            };
            var identity = new ClaimsIdentity(
                claims,
                SchemeName,
                ClaimTypes.Name,
                ClaimTypes.Role);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(
                AuthenticateResult.Success(
                    new AuthenticationTicket(principal, SchemeName)));
        }
    }
}

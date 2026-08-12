using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
using OokiGrader.Application.Templates;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateWorkflowTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(
        JsonSerializerDefaults.Web);

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
    public async Task DeleteArchivesDraftAndRestoreIsAuditedIdempotentAndRecoverable()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var detailResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}",
            "teacher");
        var originalEtag = RequiredEtag(detailResponse);

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions.SingleAsync(
                item => item.Id == versionId);
            version.State = "generating";
            await db.SaveChangesAsync();
        });
        var extractionInProgress = await application.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/templates/{templateId}",
            "teacher",
            etag: originalEtag);
        Assert.Equal(HttpStatusCode.Conflict, extractionInProgress.StatusCode);
        Assert.Equal(
            "TEMPLATE_EXTRACTION_IN_PROGRESS",
            RequiredString(await ReadJsonAsync(extractionInProgress), "code"));
        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions.SingleAsync(
                item => item.Id == versionId);
            version.State = "draft";
            await db.SaveChangesAsync();
        });

        var missingRevision = await application.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/templates/{templateId}",
            "teacher");
        var staleRevision = await application.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/templates/{templateId}",
            "teacher",
            etag: "\"rev-999\"");
        var archivedResponse = await application.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/templates/{templateId}",
            "teacher",
            etag: originalEtag);

        Assert.Equal(
            HttpStatusCode.PreconditionRequired,
            missingRevision.StatusCode);
        Assert.Equal(
            HttpStatusCode.PreconditionFailed,
            staleRevision.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, archivedResponse.StatusCode);
        var archivedEtag = RequiredEtag(archivedResponse);

        var createVersionWhileArchived = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions",
            "teacher",
            new { sourceVersionId = (string?)null });
        var createQuestionWhileArchived = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new { });
        var versionWhileArchived = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var publishWhileArchived = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            new { revision = (long?)null },
            RequiredEtag(versionWhileArchived));
        Assert.Equal(HttpStatusCode.Conflict, createVersionWhileArchived.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, createQuestionWhileArchived.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, publishWhileArchived.StatusCode);
        foreach (var blocked in new[]
                 {
                     createVersionWhileArchived,
                     createQuestionWhileArchived,
                     publishWhileArchived,
                 })
        {
            var problem = await ReadJsonAsync(blocked);
            Assert.Equal("TEMPLATE_ARCHIVED", RequiredString(problem, "code"));
        }

        var ordinaryList = await application.SendAsync(
            HttpMethod.Get,
            "/api/v1/templates",
            "teacher");
        var ordinaryBody = await ReadJsonAsync(ordinaryList);
        Assert.Empty(ordinaryBody.GetProperty("items").EnumerateArray());

        var archivedList = await application.SendAsync(
            HttpMethod.Get,
            "/api/v1/templates?state=archived",
            "teacher");
        var archivedBody = await ReadJsonAsync(archivedList);
        Assert.Equal(
            templateId,
            RequiredString(
                Assert.Single(
                    archivedBody.GetProperty("items").EnumerateArray()),
                "id"));

        var repeatedArchive = await application.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/templates/{templateId}",
            "teacher");
        Assert.Equal(HttpStatusCode.NoContent, repeatedArchive.StatusCode);
        Assert.Equal(archivedEtag, RequiredEtag(repeatedArchive));

        var missingRestoreRevision = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}:restore",
            "teacher");
        Assert.Equal(
            HttpStatusCode.PreconditionRequired,
            missingRestoreRevision.StatusCode);

        var restoredResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}:restore",
            "teacher",
            new { revision = (long?)null },
            archivedEtag);
        Assert.Equal(HttpStatusCode.OK, restoredResponse.StatusCode);
        var restored = await ReadJsonAsync(restoredResponse);
        Assert.Equal("draft", RequiredString(restored, "lifecycleState"));
        var restoredEtag = RequiredEtag(restoredResponse);

        var repeatedRestore = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}:restore",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, repeatedRestore.StatusCode);
        Assert.Equal(restoredEtag, RequiredEtag(repeatedRestore));

        await application.WithDatabaseAsync(async db =>
        {
            var template = await db.TestTemplates.AsNoTracking().SingleAsync();
            var version = await db.TemplateVersions
                .AsNoTracking()
                .SingleAsync(item => item.Id == versionId);
            Assert.Equal("draft", template.State);
            Assert.Equal("draft", version.State);
            Assert.Equal(
                1,
                await db.AuditEvents.CountAsync(
                    item => item.EventType == "template.archived"));
            Assert.Equal(
                1,
                await db.AuditEvents.CountAsync(
                    item => item.EventType == "template.restored"));
        });
    }

    [Fact]
    public async Task ArchivePreservesPublishedVersionAndRestoreReturnsTemplateToActive()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var sessionId = UlidId.New(DateTimeOffset.UtcNow.AddMinutes(1));
        await application.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var template = await db.TestTemplates.SingleAsync(
                item => item.Id == templateId);
            var version = await db.TemplateVersions.SingleAsync(
                item => item.Id == versionId);
            version.State = "published";
            version.PublishedAt = now;
            version.PublishedByStaffUserId = TestAuthenticationHandler.StaffId;
            version.ContentHash = new string('a', 64);
            template.State = "active";
            template.ActiveVersionId = version.Id;
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = version.Id,
                TitleOverride = "保存済みテスト実施",
                TestDate = new DateOnly(2026, 8, 10),
                Priority = "economy",
                State = "closed",
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
                UpdatedAt = now,
                ClosedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var detailResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}",
            "teacher");
        var archiveResponse = await application.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/templates/{templateId}",
            "teacher",
            etag: RequiredEtag(detailResponse));
        Assert.Equal(HttpStatusCode.NoContent, archiveResponse.StatusCode);

        var historicalSession = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/test-sessions/{sessionId}",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, historicalSession.StatusCode);
        var historicalSessionBody = await ReadJsonAsync(historicalSession);
        Assert.Equal(sessionId, RequiredString(historicalSessionBody, "id"));
        Assert.Equal(
            versionId,
            RequiredString(historicalSessionBody, "templateVersionId"));
        Assert.Equal(
            "保存済みテスト実施",
            RequiredString(historicalSessionBody, "sessionName"));

        var newSession = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/test-sessions",
            "teacher",
            new
            {
                templateVersionId = versionId,
                testDate = "2026-08-11",
                sessionName = "作成不可",
                classLabel = (string?)null,
                course = (string?)null,
                priority = "economy",
            });
        Assert.Equal(HttpStatusCode.Conflict, newSession.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(
                "published",
                (await db.TemplateVersions.AsNoTracking().SingleAsync(
                    item => item.Id == versionId)).State);
            Assert.True(await db.TestSessions.AsNoTracking().AnyAsync(
                item => item.Id == sessionId));
        });

        var restoreResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}:restore",
            "teacher",
            new { revision = (long?)null },
            RequiredEtag(archiveResponse));
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        var restored = await ReadJsonAsync(restoreResponse);
        Assert.Equal("active", RequiredString(restored, "lifecycleState"));

        await application.WithDatabaseAsync(async db =>
        {
            var template = await db.TestTemplates.AsNoTracking().SingleAsync(
                item => item.Id == templateId);
            var version = await db.TemplateVersions.AsNoTracking().SingleAsync(
                item => item.Id == versionId);
            Assert.Equal("active", template.State);
            Assert.Equal(versionId, template.ActiveVersionId);
            Assert.Equal("published", version.State);
            Assert.NotNull(version.PublishedAt);
            Assert.Equal(new string('a', 64), version.ContentHash);
        });
    }

    [Fact]
    public async Task SessionArchiveWaitsForTerminalWorkThenFreezesMutationsButKeepsHistory()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var now = DateTimeOffset.UtcNow;
        var sessionId = UlidId.New(now.AddTicks(1));
        var submissionId = UlidId.New(now.AddTicks(2));
        var jobId = UlidId.New(now.AddTicks(3));
        var uploadId = UlidId.New(now.AddTicks(4));
        var batchId = UlidId.New(now.AddTicks(5));
        await application.WithDatabaseAsync(async db =>
        {
            var template = await db.TestTemplates.SingleAsync(
                item => item.Id == templateId);
            var version = await db.TemplateVersions.SingleAsync(
                item => item.Id == versionId);
            version.State = "published";
            version.PublishedAt = now;
            version.PublishedByStaffUserId = TestAuthenticationHandler.StaffId;
            version.ContentHash = new string('b', 64);
            template.State = "active";
            template.ActiveVersionId = version.Id;
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = TestAuthenticationHandler.StaffId,
                Username = "session-archive-test-teacher",
                UsernameNormalized = "session-archive-test-teacher",
                DisplayName = "Session Archive Test Teacher",
                PasswordHash = "test",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = version.Id,
                TitleOverride = "アーカイブ確認",
                TestDate = new DateOnly(2026, 8, 10),
                Priority = "economy",
                State = "closed",
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
                UpdatedAt = now,
                ClosedAt = now,
            });
            db.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "needs_grade_review",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                UploadedByStaffUserId = TestAuthenticationHandler.StaffId,
                OriginalFileName = "答案.pdf",
                UploadCompletedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var incomplete = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/test-sessions/{sessionId}:archive",
            "teacher");
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        Assert.Equal(
            "TEST_SESSION_ARCHIVE_SUBMISSIONS_INCOMPLETE",
            RequiredString(await ReadJsonAsync(incomplete), "code"));

        await application.WithDatabaseAsync(async db =>
        {
            var submission = await db.Submissions.SingleAsync(
                item => item.Id == submissionId);
            submission.State = "finalized";
            submission.FinalizedAt = now;
            submission.FinalizedByStaffUserId = TestAuthenticationHandler.StaffId;
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = AiInitialGradingJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey = $"submission:{submissionId}:grade:r1",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new { submissionId }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var gradingActive = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/test-sessions/{sessionId}:archive",
            "teacher");
        Assert.Equal(HttpStatusCode.Conflict, gradingActive.StatusCode);
        Assert.Equal(
            "TEST_SESSION_ARCHIVE_GRADING_ACTIVE",
            RequiredString(await ReadJsonAsync(gradingActive), "code"));

        await application.WithDatabaseAsync(async db =>
        {
            var job = await db.BackgroundJobs.SingleAsync(item => item.Id == jobId);
            job.State = "succeeded";
            job.ProgressBasisPoints = 10_000;
            job.CompletedAt = now;
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                Purpose = "completed_test",
                TestSessionId = sessionId,
                OriginalFileName = "処理中.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 1,
                CurrentBytes = 0,
                IncomingRelativePath = $"test/{uploadId}.part",
                State = "uploading",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var uploadActive = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/test-sessions/{sessionId}:archive",
            "teacher");
        Assert.Equal(HttpStatusCode.Conflict, uploadActive.StatusCode);
        Assert.Equal(
            "TEST_SESSION_ARCHIVE_UPLOADS_ACTIVE",
            RequiredString(await ReadJsonAsync(uploadActive), "code"));

        await application.WithDatabaseAsync(async db =>
        {
            var upload = await db.UploadSessions.SingleAsync(
                item => item.Id == uploadId);
            upload.State = "cancelled";
            db.OrderedScanBatches.Add(new OrderedScanBatchEntity
            {
                Id = batchId,
                TestSessionId = sessionId,
                ExpectedPageCount = 1,
                Status = OrderedScanBatchStatus.NeedsReview,
                AssemblyPolicyVersion = "ordered-scan-assembly-v1",
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                ExpiresAt = now.AddHours(24),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var scanBatchActive = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/test-sessions/{sessionId}:archive",
            "teacher");
        Assert.Equal(HttpStatusCode.Conflict, scanBatchActive.StatusCode);
        Assert.Equal(
            "TEST_SESSION_ARCHIVE_SCAN_BATCHES_ACTIVE",
            RequiredString(await ReadJsonAsync(scanBatchActive), "code"));

        await application.WithDatabaseAsync(async db =>
        {
            var batch = await db.OrderedScanBatches.SingleAsync(
                item => item.Id == batchId);
            batch.Status = OrderedScanBatchStatus.Cancelled;
            batch.CompletedAt = now;
            await db.SaveChangesAsync();
        });

        var archived = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/test-sessions/{sessionId}:archive",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(
            "archived",
            RequiredString(await ReadJsonAsync(archived), "state"));

        var priorityMutation = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/test-sessions/{sessionId}",
            "teacher",
            new { priority = "expedite", revision = (long?)null });
        var rosterMutation = await application.SendAsync(
            HttpMethod.Put,
            $"/api/v1/test-sessions/{sessionId}/roster",
            "teacher",
            new { studentIds = Array.Empty<string>() });
        foreach (var blocked in new[] { priorityMutation, rosterMutation })
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal(
                "TEST_SESSION_ARCHIVED_READ_ONLY",
                RequiredString(await ReadJsonAsync(blocked), "code"));
        }

        var history = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/test-sessions/{sessionId}",
            "teacher");
        var summary = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/test-sessions/{sessionId}/summary",
            "teacher");
        var uploadHistory = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/test-sessions/{sessionId}/upload-status",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, uploadHistory.StatusCode);
        Assert.Equal(
            1,
            (await ReadJsonAsync(summary)).GetProperty("finalizedCount").GetInt32());
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
    public async Task ClonedGeneratedVersionRetainsDerivedSourceAccessAndProvenance()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var now = DateTimeOffset.UtcNow;
        var batchId = UlidId.New(now);
        var unitId = UlidId.New(now.AddTicks(1));
        var uploadId = UlidId.New(now.AddTicks(2));
        var fileObjectId = UlidId.New(now.AddTicks(3));
        var fileReferenceId = UlidId.New(now.AddTicks(4));
        var sourceId = UlidId.New(now.AddTicks(5));
        var sourceSha = new string('a', 64);
        var derivedSha = new string('b', 64);
        var derivedBytes = Enumerable.Repeat((byte)0x25, 321).ToArray();
        var profile = new TemplateGenerationProfile(
            TemplateGenerationProfile.CurrentProfileVersion,
            TestType.Hop,
            "算数",
            AnswerStyle: null,
            TemplatePromptSystem.Standard,
            SourcePageCount: 1,
            UnitSequence: 1,
            FirstPage: 1,
            LastPage: 1,
            StepSetIndex: null,
            StepVariationIndex: null,
            DeterministicSuffix: null,
            TemplateGenerationProfile.CurrentSplitPolicyVersion,
            TemplateGenerationProfile.CurrentNamingPolicyVersion,
            "template-extract-v2.0.0",
            "template_extract_v5");
        var profileJson = JsonSerializer.Serialize(profile);
        var profileHash = profile.ComputeHash();
        application.AddContent(
            new ContentObjectLocator(
                ContentStorageClass.TemplateDerived,
                derivedSha,
                derivedBytes.Length,
                "pdf"),
            derivedBytes);

        await application.WithDatabaseAsync(async db =>
        {
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = "HOP算数_小学4年.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = derivedBytes.Length,
                CurrentBytes = derivedBytes.Length,
                FinalSha256 = sourceSha,
                IncomingRelativePath = "test/generated-source",
                State = "completed",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = derivedSha,
                Bytes = derivedBytes.Length,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = $"template/derived/{derivedSha}.pdf",
                StorageClass = ContentStorageClass.TemplateDerived.ToString(),
                RetentionClass = "template_source",
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = fileReferenceId,
                FileObjectId = fileObjectId,
                OwnerType = "template_generation_unit",
                OwnerId = unitId,
                Purpose = "derived_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            db.TemplateSources.Add(new TemplateSourceEntity
            {
                Id = sourceId,
                TemplateVersionId = versionId,
                UploadSessionId = uploadId,
                FileReferenceId = fileReferenceId,
                SourceRole = "blank_test",
                DisplayName = "HOP算数 第1回.pdf",
                Ordinal = 0,
                UploadedByStaffUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
            });
            var version = await db.TemplateVersions.SingleAsync(
                item => item.Id == versionId);
            version.TestType = TestType.Hop;
            version.PromptSystem = TemplatePromptSystem.Standard;
            version.OriginatingBatchId = batchId;
            version.OriginatingUnitId = unitId;
            version.GenerationProfileVersion = profile.ProfileVersion;
            version.GenerationProfileJson = profileJson;
            version.GenerationProfileHash = profileHash;
            version.PrintedTestName = "HOP算数 第1回";
            version.ResolvedGrade = GradeLevel.Grade4;
            await db.SaveChangesAsync();
        });

        await application.WithDatabaseAsync(async db =>
        {
            var generatedVersion = await db.TemplateVersions
                .AsNoTracking()
                .SingleAsync(item => item.Id == versionId);
            Assert.Equal(unitId, generatedVersion.OriginatingUnitId);
            Assert.Equal(profileHash, generatedVersion.GenerationProfileHash);
        });

        var cloneResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions",
            "teacher",
            new { sourceVersionId = versionId });
        Assert.Equal(HttpStatusCode.Created, cloneResponse.StatusCode);
        var clone = await ReadJsonAsync(cloneResponse);
        var cloneVersionId = RequiredString(clone, "id");
        var clonedSource = Assert.Single(
            clone.GetProperty("sources").EnumerateArray());
        var cloneSourceId = RequiredString(clonedSource, "id");

        await application.WithDatabaseAsync(async db =>
        {
            var clonedVersion = await db.TemplateVersions
                .AsNoTracking()
                .SingleAsync(item => item.Id == cloneVersionId);
            var clonedSourceEntity = await db.TemplateSources
                .AsNoTracking()
                .SingleAsync(item => item.Id == cloneSourceId);
            Assert.Equal(batchId, clonedVersion.OriginatingBatchId);
            Assert.Equal(unitId, clonedVersion.OriginatingUnitId);
            Assert.Equal(
                profile.ProfileVersion,
                clonedVersion.GenerationProfileVersion);
            Assert.Equal(profileJson, clonedVersion.GenerationProfileJson);
            Assert.Equal(profileHash, clonedVersion.GenerationProfileHash);
            Assert.Equal(TestType.Hop, clonedVersion.TestType);
            Assert.Equal(
                TemplatePromptSystem.Standard,
                clonedVersion.PromptSystem);
            Assert.Equal(GradeLevel.Grade4, clonedVersion.ResolvedGrade);
            Assert.Equal(fileReferenceId, clonedSourceEntity.FileReferenceId);
        });

        var contentResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{cloneVersionId}" +
                $"/sources/{cloneSourceId}/content",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);
        Assert.Equal(
            derivedBytes,
            await contentResponse.Content.ReadAsByteArrayAsync());

        await application.WithDatabaseAsync(async db =>
        {
            var clonedVersion = await db.TemplateVersions.SingleAsync(
                item => item.Id == cloneVersionId);
            clonedVersion.OriginatingUnitId = UlidId.New();
            await db.SaveChangesAsync();
        });
        var crossUnitResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{cloneVersionId}" +
                $"/sources/{cloneSourceId}/content",
            "teacher");
        Assert.Equal(HttpStatusCode.Gone, crossUnitResponse.StatusCode);
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
            subjective.RequiresReviewAlways = true;
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
            "question.review_always",
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
                requiresCompleteAnswer = true,
                answerOrderInsensitive = true,
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
        Assert.True(created.GetProperty("requiresCompleteAnswer").GetBoolean());
        Assert.True(created.GetProperty("answerOrderInsensitive").GetBoolean());
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
            var question = await db.Questions.AsNoTracking().SingleAsync();
            Assert.True(question.RequiresCompleteAnswer);
            Assert.True(question.AnswerOrderInsensitive);
        });
    }

    [Theory]
    [InlineData("multiple_choice")]
    [InlineData("boolean")]
    [InlineData("numeric")]
    [InlineData("exact_short_text")]
    [InlineData("semantic_short_text")]
    [InlineData("multi_part")]
    [InlineData("subjective")]
    public async Task QuestionCreationDefaultsSupportedTypesToAiRubric(
        string questionType)
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            defaultPointsMilli: 1_750);

        var response = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "問1",
                questionText = "理由を説明しなさい。",
                questionType,
                canonicalAnswer = "模範解答",
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var question = await ReadJsonAsync(response);
        Assert.Equal("ai_rubric", RequiredString(question, "gradingMode"));
        Assert.Contains(
            "模範解答",
            RequiredString(question, "rubric"),
            StringComparison.Ordinal);
        Assert.Equal(250, question.GetProperty("pointIncrementMilli").GetInt64());
        Assert.False(question.GetProperty("requiresReviewAlways").GetBoolean());
    }

    [Fact]
    public async Task QuestionTypeChangeDerivesDefaultsButPreservesExplicitMode()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var createdResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "問1",
                questionText = "答えなさい。",
                questionType = "exact_short_text",
                gradingMode = "manual",
                canonicalAnswer = "答え",
            });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await ReadJsonAsync(createdResponse);
        var questionId = RequiredString(created, "id");

        var sameTypeResponse = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            new { questionType = "exact_short_text" },
            RequiredEtag(createdResponse));
        Assert.Equal(HttpStatusCode.OK, sameTypeResponse.StatusCode);
        var sameType = await ReadJsonAsync(sameTypeResponse);
        Assert.Equal("manual", RequiredString(sameType, "gradingMode"));

        var changedTypeResponse = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            new { questionType = "subjective" },
            RequiredEtag(sameTypeResponse));
        Assert.Equal(HttpStatusCode.OK, changedTypeResponse.StatusCode);
        var changedType = await ReadJsonAsync(changedTypeResponse);
        Assert.Equal("ai_rubric", RequiredString(changedType, "gradingMode"));
        Assert.False(changedType.GetProperty("requiresReviewAlways").GetBoolean());
        Assert.Contains(
            "答え",
            RequiredString(changedType, "rubric"),
            StringComparison.Ordinal);

        var explicitModeResponse = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            new
            {
                questionType = "semantic_short_text",
                gradingMode = "manual",
            },
            RequiredEtag(changedTypeResponse));
        Assert.Equal(HttpStatusCode.OK, explicitModeResponse.StatusCode);
        Assert.Equal(
            "manual",
            RequiredString(await ReadJsonAsync(explicitModeResponse), "gradingMode"));
    }

    [Fact]
    public async Task DefaultSubjectiveAiRubricCanBePublished()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var created = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            new
            {
                displayLabel = "問1",
                questionText = "理由を説明しなさい。",
                questionType = "subjective",
                canonicalAnswer = "模範解答",
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var validation = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:validate",
            "teacher",
            new { });
        Assert.True((await ReadJsonAsync(validation))
            .GetProperty("valid")
            .GetBoolean());

        var current = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var published = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            new { },
            RequiredEtag(current));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    [Fact]
    public async Task PublishAtomicallyStartsOpenSessionAndSnapshotsCanonicalMetadata()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var created = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            ExactQuestionRequest("次の語を漢字で書きなさい。", 1_000));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var current = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var etag = RequiredEtag(current);
        var request = new
        {
            testDate = "2026-08-11",
            classLabel = "A組",
        };
        var publishedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            request,
            etag);
        Assert.Equal(HttpStatusCode.OK, publishedResponse.StatusCode);
        var published = await ReadJsonAsync(publishedResponse);
        Assert.Equal("published", RequiredString(published, "state"));
        var session = published.GetProperty("testSession");
        var sessionId = RequiredString(session, "id");
        Assert.Equal("open", RequiredString(session, "state"));
        Assert.Equal("expedite", RequiredString(session, "priority"));
        Assert.Equal("漢字確認テスト", RequiredString(session, "title"));
        Assert.Equal("漢字確認テスト", RequiredString(session, "templateTitle"));
        Assert.Equal("国語", RequiredString(session, "subject"));
        Assert.Equal("中学1年", RequiredString(session, "gradeLabel"));
        Assert.Equal("漢字", RequiredString(session, "category"));
        Assert.Equal("標準", RequiredString(session, "course"));
        Assert.Equal("A組", RequiredString(session, "classLabel"));
        Assert.Equal("2026-08-11", RequiredString(session, "testDate"));
        Assert.Equal(
            "template_publish",
            RequiredString(session, "creationSource"));

        // A retry that outlives the middleware's response cache resolves the
        // durable publish-created session rather than making a duplicate.
        var retriedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            request,
            etag);
        Assert.Equal(HttpStatusCode.OK, retriedResponse.StatusCode);
        Assert.Equal(
            sessionId,
            RequiredString(
                (await ReadJsonAsync(retriedResponse)).GetProperty("testSession"),
                "id"));

        await application.WithDatabaseAsync(async db =>
        {
            var template = await db.TestTemplates.SingleAsync(
                item => item.Id == templateId);
            template.Title = "後から変更された名前";
            template.Subject = "変更後教科";
            template.Course = "変更後コース";
            await db.SaveChangesAsync();
        });

        var detailResponse = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/test-sessions/{sessionId}",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await ReadJsonAsync(detailResponse);
        Assert.Equal("漢字確認テスト", RequiredString(detail, "title"));
        Assert.Equal("国語", RequiredString(detail, "subject"));
        Assert.Equal("標準", RequiredString(detail, "course"));

        await application.WithDatabaseAsync(async db =>
        {
            var persistedSession = await db.TestSessions
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(sessionId, persistedSession.Id);
            Assert.Null(persistedSession.TitleOverride);
            Assert.Equal("漢字確認テスト", persistedSession.TemplateTitleSnapshot);
            Assert.Equal("国語", persistedSession.TemplateSubjectSnapshot);
            Assert.Equal("中学1年", persistedSession.TemplateGradeLabelSnapshot);
            Assert.Equal("漢字", persistedSession.TemplateCategorySnapshot);
            Assert.Equal("標準", persistedSession.TemplateCourseSnapshot);
            Assert.Equal(
                1,
                await db.AuditEvents.CountAsync(
                    item => item.EventType == "template_version.published"));
            Assert.Equal(
                1,
                await db.AuditEvents.CountAsync(
                    item => item.EventType == "test_session.created"));
            Assert.Equal(
                1,
                await db.AuditEvents.CountAsync(
                    item => item.EventType == "test_session.opened"));
        });
    }

    [Fact]
    public async Task SessionInsertFailureRollsBackTemplatePublication()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var created = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            ExactQuestionRequest("次の語を漢字で書きなさい。", 1_000));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await application.WithDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER force_publish_session_failure
                BEFORE INSERT ON test_session
                WHEN NEW.creation_source = 'template_publish'
                BEGIN
                    SELECT RAISE(ABORT, 'forced publish session failure');
                END;
                """));

        var current = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        var response = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
            "teacher",
            new { testDate = "2026-08-11" },
            RequiredEtag(current));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "TEST_SESSION_START_FAILED",
            RequiredString(await ReadJsonAsync(response), "code"));

        await application.WithDatabaseAsync(async db =>
        {
            var template = await db.TestTemplates.AsNoTracking().SingleAsync(
                item => item.Id == templateId);
            var version = await db.TemplateVersions.AsNoTracking().SingleAsync(
                item => item.Id == versionId);
            Assert.Equal("draft", template.State);
            Assert.Null(template.ActiveVersionId);
            Assert.Equal("draft", version.State);
            Assert.Null(version.PublishedAt);
            Assert.Null(version.ContentHash);
            Assert.Empty(await db.TestSessions.AsNoTracking().ToArrayAsync());
            Assert.DoesNotContain(
                await db.AuditEvents.AsNoTracking().ToArrayAsync(),
                item => item.EventType is "template_version.published"
                    or "test_session.created"
                    or "test_session.opened");
        });
    }

    [Fact]
    public async Task ExistingPublishedVersionCanStartAnotherOpenCanonicalSession()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);
        var created = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            ExactQuestionRequest("次の語を漢字で書きなさい。", 1_000));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var current = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{templateId}/versions/{versionId}",
            "teacher");
        Assert.Equal(
            HttpStatusCode.OK,
            (await application.SendAsync(
                HttpMethod.Post,
                $"/api/v1/templates/{templateId}/versions/{versionId}:publish",
                "teacher",
                new { testDate = "2026-08-11" },
                RequiredEtag(current))).StatusCode);

        var idempotencyKey = UlidId.New();
        var sessionRequest = new
        {
            templateVersionId = versionId,
            testDate = "2026-08-12",
            classLabel = "B組",
            openImmediately = true,
        };
        var createdSessionResponse = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/test-sessions",
            "teacher",
            sessionRequest,
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, createdSessionResponse.StatusCode);
        var session = await ReadJsonAsync(createdSessionResponse);
        var sessionId = RequiredString(session, "id");
        Assert.Equal("open", RequiredString(session, "state"));
        Assert.Equal("expedite", RequiredString(session, "priority"));
        Assert.Equal("漢字確認テスト", RequiredString(session, "title"));
        Assert.Equal("標準", RequiredString(session, "course"));
        Assert.Equal("manual", RequiredString(session, "creationSource"));

        // Simulate a committed endpoint response whose middleware replay row
        // was lost: the session row's actor/key fence returns the same resource.
        var replayResponse = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/test-sessions",
            "teacher",
            sessionRequest,
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal(
            sessionId,
            RequiredString(await ReadJsonAsync(replayResponse), "id"));

        var reusedKey = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/test-sessions",
            "teacher",
            new
            {
                templateVersionId = versionId,
                testDate = "2026-08-13",
                openImmediately = true,
            },
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, reusedKey.StatusCode);
        Assert.Equal(
            "IDEMPOTENCY_KEY_REUSED",
            RequiredString(await ReadJsonAsync(reusedKey), "code"));
        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(2, await db.TestSessions.CountAsync());
            Assert.Equal(
                1,
                await db.TestSessions.CountAsync(
                    item => item.CreationSource == "template_publish"));
            Assert.Equal(
                idempotencyKey,
                (await db.TestSessions.SingleAsync(item => item.Id == sessionId))
                .RequestIdempotencyKey);
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
    public async Task ExplicitNonKanjiExceptionRoundTripsThroughQuestionUpdates()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(application);

        var createdResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions",
            "teacher",
            ExactQuestionRequest("次の語を漢字で書きなさい。", 1_000));
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await ReadJsonAsync(createdResponse);
        var questionId = RequiredString(created, "id");
        var createdException = Assert.Single(
            created.GetProperty("acceptedAnswers").EnumerateArray(),
            answer => RequiredString(answer, "variantType") == "explicitException");
        Assert.Equal("かんじ", RequiredString(createdException, "text"));

        var updatedResponse = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/templates/{templateId}/versions/{versionId}/questions/{questionId}",
            "teacher",
            ExactQuestionRequest("更新した問題文", 1_000),
            RequiredEtag(createdResponse));
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = await ReadJsonAsync(updatedResponse);
        var updatedException = Assert.Single(
            updated.GetProperty("acceptedAnswers").EnumerateArray(),
            answer => RequiredString(answer, "variantType") == "explicitException");
        Assert.Equal("かんじ", RequiredString(updatedException, "text"));

        await application.WithDatabaseAsync(async db =>
        {
            var stored = await db.AcceptedAnswers
                .AsNoTracking()
                .SingleAsync(answer =>
                    answer.QuestionId == questionId
                    && answer.VariantType == "phonetic_exception");
            Assert.Equal("かんじ", stored.AnswerText);
        });
    }

    [Theory]
    [InlineData("profileHash", "generation.profile_invalid", false)]
    [InlineData("promptRoute", "generation.prompt_route_invalid", false)]
    [InlineData("grade", "generation.grade_required", false)]
    [InlineData("title", "generation.final_name_required", false)]
    [InlineData("range", "generation.source_range_invalid", false)]
    [InlineData("draftHash", "generation.extraction_draft_hash_invalid", false)]
    [InlineData("derivedObject", "generation.derived_object_unavailable", false)]
    [InlineData("blockingWarning", "generation.blocking_warning", false)]
    [InlineData("stepSuffix", "generation.step_suffix_invalid", true)]
    public async Task GeneratedPublicationBlocksTamperedProfileAndProvenance(
        string tamper,
        string expectedIssueCode,
        bool step)
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var seeded = await SeedGeneratedPublicationVersionAsync(
            application,
            step ? TestType.Step : TestType.Hop);

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions
                .Include(item => item.TestTemplate)
                .SingleAsync(item => item.Id == seeded.VersionId);
            var unit = await db.TemplateGenerationUnits
                .SingleAsync(item => item.Id == seeded.UnitId);
            switch (tamper)
            {
                case "profileHash":
                    version.GenerationProfileHash = new string('f', 64);
                    break;
                case "promptRoute":
                    version.PromptSystem = TemplatePromptSystem.ClassPlacement;
                    break;
                case "grade":
                    version.ResolvedGrade = GradeLevel.Unknown;
                    break;
                case "title":
                    version.TestTemplate.Title = " ";
                    break;
                case "range":
                    unit.FirstPage = 2;
                    unit.LastPage = 2;
                    break;
                case "draftHash":
                    unit.ExtractionDraftHash = new string('e', 64);
                    break;
                case "derivedObject":
                    var fileObject = await db.FileObjects.SingleAsync(
                        item => item.Id == seeded.FileObjectId);
                    fileObject.State = "missing";
                    break;
                case "blockingWarning":
                    unit.WarningsJson = JsonSerializer.Serialize(
                        new[]
                        {
                            new GenerationWarning(
                                "GRADE_REQUIRED",
                                GenerationWarningSeverity.Blocking,
                                "学年を確認してください。"),
                        },
                        WebJsonOptions);
                    break;
                case "stepSuffix":
                    version.TestTemplate.Title += "-1";
                    unit.FinalTemplateName = version.TestTemplate.Title;
                    break;
                default:
                    throw new InvalidOperationException(tamper);
            }

            await db.SaveChangesAsync();
        });

        var validationResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{seeded.TemplateId}/versions/" +
                $"{seeded.VersionId}:validate",
            "teacher",
            new { });
        Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
        var validation = await ReadJsonAsync(validationResponse);
        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Contains(
            validation.GetProperty("issues").EnumerateArray(),
            item => RequiredString(item, "code") == expectedIssueCode);

        var current = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{seeded.TemplateId}/versions/{seeded.VersionId}",
            "teacher");
        var publishResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{seeded.TemplateId}/versions/" +
                $"{seeded.VersionId}:publish",
            "teacher",
            new { },
            RequiredEtag(current));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            publishResponse.StatusCode);
    }

    [Fact]
    public async Task GeneratedUnitPaperCanPublishMixedProvidedAndAiAnswers()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var seeded = await SeedGeneratedPublicationVersionAsync(
            application,
            TestType.Hop);

        var current = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{seeded.TemplateId}/versions/{seeded.VersionId}",
            "teacher");
        var verifiedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{seeded.TemplateId}/versions/" +
                $"{seeded.VersionId}/questions:verifyProposals",
            "teacher",
            new { selectionMode = "all" },
            RequiredEtag(current),
            UlidId.New());
        Assert.Equal(HttpStatusCode.OK, verifiedResponse.StatusCode);
        var verified = await ReadJsonAsync(verifiedResponse);
        Assert.Equal(
            2,
            verified.GetProperty("verifiedQuestionCount").GetInt32());
        Assert.Equal(
            2,
            verified.GetProperty("verifiedAnswerCount").GetInt32());
        Assert.Equal(
            0,
            verified.GetProperty("skippedQuestionCount").GetInt32());
        Assert.DoesNotContain(
            verified.GetProperty("issues").EnumerateArray(),
            item => RequiredString(item, "code") ==
                "answer.authoritative_source_required");

        var validatedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{seeded.TemplateId}/versions/" +
                $"{seeded.VersionId}:validate",
            "teacher",
            new { });
        var validated = await ReadJsonAsync(validatedResponse);
        Assert.True(validated.GetProperty("valid").GetBoolean());

        var updated = await application.SendAsync(
            HttpMethod.Get,
            $"/api/v1/templates/{seeded.TemplateId}/versions/{seeded.VersionId}",
            "teacher");
        var publishedResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{seeded.TemplateId}/versions/" +
                $"{seeded.VersionId}:publish",
            "teacher",
            new { },
            RequiredEtag(updated));
        Assert.Equal(HttpStatusCode.OK, publishedResponse.StatusCode);
        var published = await ReadJsonAsync(publishedResponse);
        Assert.Equal("published", RequiredString(published, "state"));
    }

    [Fact]
    public async Task GeneratedUnitPaperStillRequiresPerAnswerSourceProvenance()
    {
        await using var application = await TemplateTestApplication.CreateAsync();
        var seeded = await SeedGeneratedPublicationVersionAsync(
            application,
            TestType.Hop);
        await application.WithDatabaseAsync(async db =>
        {
            var questions = await db.Questions
                .Include(item => item.AcceptedAnswers)
                .Where(item => item.TemplateVersionId == seeded.VersionId)
                .ToListAsync();
            foreach (var question in questions)
            {
                question.TeacherVerified = true;
                foreach (var answer in question.AcceptedAnswers)
                {
                    answer.TeacherVerified = true;
                }
            }

            questions
                .SelectMany(item => item.AcceptedAnswers)
                .Single(item =>
                    item.AnswerProvenance == "provided_model_answer")
                .SourcePageNumber = null;
            await db.SaveChangesAsync();
        });

        var validationResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/templates/{seeded.TemplateId}/versions/" +
                $"{seeded.VersionId}:validate",
            "teacher",
            new { });
        var validation = await ReadJsonAsync(validationResponse);
        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Contains(
            validation.GetProperty("issues").EnumerateArray(),
            item => RequiredString(item, "code") ==
                "answer.invalid_provided_source");
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

    private static async Task<SeededGeneratedPublicationVersion>
        SeedGeneratedPublicationVersionAsync(
            TemplateTestApplication application,
            TestType testType)
    {
        var (templateId, versionId) = await CreateTemplateAndVersionAsync(
            application,
            addBlankSource: false);
        var now = DateTimeOffset.UtcNow;
        var batchId = UlidId.New(now.AddTicks(1));
        var unitId = UlidId.New(now.AddTicks(2));
        var uploadId = UlidId.New(now.AddTicks(3));
        var fileObjectId = UlidId.New(now.AddTicks(4));
        var fileReferenceId = UlidId.New(now.AddTicks(5));
        var sourceId = UlidId.New(now.AddTicks(6));
        var firstQuestionId = UlidId.New(now.AddTicks(7));
        var secondQuestionId = UlidId.New(now.AddTicks(8));
        var sourceSha = new string('a', 64);
        var derivedSha = new string('b', 64);
        var sourcePageCount = testType == TestType.Step ? 6 : 1;
        var expectedUnitCount = testType == TestType.Step ? 3 : 1;
        var lastPage = testType == TestType.Step ? 2 : 1;
        var suffix = testType == TestType.Step ? "-1" : null;
        var finalName = TemplateNamePolicy.CreateKnownTestName(
            testType,
            "国語",
            GradeLevel.Grade4,
            unitSequence: 1,
            stepSetIndex: testType == TestType.Step ? 1 : null,
            stepVariationIndex: testType == TestType.Step ? 1 : null);
        var profile = new TemplateGenerationProfile(
            TemplateGenerationProfile.CurrentProfileVersion,
            testType,
            "国語",
            AnswerStyle: null,
            TemplatePromptSystem.Standard,
            sourcePageCount,
            UnitSequence: 1,
            FirstPage: 1,
            LastPage: lastPage,
            StepSetIndex: testType == TestType.Step ? 1 : null,
            StepVariationIndex: testType == TestType.Step ? 1 : null,
            DeterministicSuffix: suffix,
            TemplateGenerationProfile.CurrentSplitPolicyVersion,
            TemplateGenerationProfile.CurrentNamingPolicyVersion,
            "template-extract-v2.0.0",
            "template_extract_v5");
        var profileJson = JsonSerializer.Serialize(profile, WebJsonOptions);
        const string draftJson = "{\"schemaVersion\":\"template_extract_v5\"}";
        var draftHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(draftJson)))
            .ToLowerInvariant();

        await application.WithDatabaseAsync(async db =>
        {
            var version = await db.TemplateVersions
                .Include(item => item.TestTemplate)
                .SingleAsync(item => item.Id == versionId);
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = TestAuthenticationHandler.StaffId,
                Username = "generated.teacher",
                UsernameNormalized = "generated.teacher",
                DisplayName = "生成確認担当",
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            version.TestTemplate.Title = finalName;
            version.TargetTotalPointsMilli = 2_000;
            version.AiGenerationProvenanceId = UlidId.New(now.AddTicks(9));
            version.TestType = testType;
            version.PromptSystem = TemplatePromptSystem.Standard;
            version.OriginatingBatchId = batchId;
            version.OriginatingUnitId = unitId;
            version.GenerationProfileVersion = profile.ProfileVersion;
            version.GenerationProfileJson = profileJson;
            version.GenerationProfileHash = profile.ComputeHash();
            version.StepSetIndex = testType == TestType.Step ? 1 : null;
            version.StepVariationIndex = testType == TestType.Step ? 1 : null;
            version.PrintedTestName = "漢字確認テスト";
            version.ResolvedGrade = GradeLevel.Grade4;
            version.ExpectedSubmissionPageCount = lastPage;

            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = "HOP国語_小学4年.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 1_000,
                CurrentBytes = 1_000,
                FinalSha256 = sourceSha,
                IncomingRelativePath = $"test/{uploadId}",
                State = "completed",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TemplateGenerationBatches.Add(new TemplateGenerationBatchEntity
            {
                Id = batchId,
                Status = TemplateGenerationBatchStatus.Completed,
                TestType = testType,
                Subject = "国語",
                PromptSystem = TemplatePromptSystem.Standard,
                SourceId = uploadId,
                SourcePageCount = sourcePageCount,
                ExpectedUnitCount = expectedUnitCount,
                CompletedUnitCount = expectedUnitCount,
                FailedUnitCount = 0,
                PlanHash = new string('c', 64),
                CreatedByUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = now,
            });
            db.TemplateGenerationUnits.Add(new TemplateGenerationUnitEntity
            {
                Id = unitId,
                BatchId = batchId,
                Sequence = 1,
                Status = TemplateGenerationUnitStatus.Confirmed,
                TestType = testType,
                FirstPage = 1,
                LastPage = lastPage,
                StepSetIndex = testType == TestType.Step ? 1 : null,
                StepVariationIndex = testType == TestType.Step ? 1 : null,
                DeterministicSuffix = suffix,
                PromptSystem = TemplatePromptSystem.Standard,
                GenerationProfileJson = profileJson,
                GenerationProfileHash = profile.ComputeHash(),
                AppliedRotationsJson = "[]",
                DerivedSourceObjectKey = $"template/derived/{derivedSha}.pdf",
                DerivedSourceSha256 = derivedSha,
                ExtractionDraftJson = draftJson,
                ExtractionDraftHash = draftHash,
                PrintedTestName = "漢字確認テスト",
                UserConfirmedBaseName = null,
                FinalTemplateName = finalName,
                FilenameGrade = GradeLevel.Grade4,
                PaperGrade = GradeLevel.Grade4,
                ResolvedGrade = GradeLevel.Grade4,
                GradeEvidence = GradeEvidence.FileNameAndPaper,
                GradeConfirmedByUser = true,
                WarningsJson = "[]",
                CreatedTemplateId = templateId,
                CreatedTemplateVersionId = versionId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = derivedSha,
                Bytes = 500,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = $"template/derived/{derivedSha}.pdf",
                StorageClass = ContentStorageClass.TemplateDerived.ToString(),
                RetentionClass = "template_source",
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = fileReferenceId,
                FileObjectId = fileObjectId,
                OwnerType = "template_generation_unit",
                OwnerId = unitId,
                Purpose = "derived_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            db.TemplateGenerationDerivedSources.Add(
                new TemplateGenerationDerivedSourceEntity
                {
                    Id = UlidId.New(now.AddTicks(10)),
                    UnitId = unitId,
                    ParentSourceId = uploadId,
                    ParentFirstPage = 1,
                    ParentLastPage = lastPage,
                    OriginalContentSha256 = sourceSha,
                    DerivationType = "pageRange",
                    AppliedRotationsJson = "[]",
                    DerivationPolicyVersion =
                        PdfPageRangeDerivationPolicy.CurrentVersion,
                    DerivedContentSha256 = derivedSha,
                    FileReferenceId = fileReferenceId,
                    CreatedAt = now,
                });
            db.TemplateSources.Add(new TemplateSourceEntity
            {
                Id = sourceId,
                TemplateVersionId = versionId,
                UploadSessionId = uploadId,
                FileReferenceId = fileReferenceId,
                SourceRole = "contains_model_answers",
                DisplayName = $"{finalName}.pdf",
                Ordinal = 0,
                UploadedByStaffUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
            });
            db.Questions.AddRange(
                CreateGeneratedQuestion(
                    firstQuestionId,
                    versionId,
                    orderIndex: 0,
                    "問1",
                    "光を漢字で書きなさい。",
                    "模範解答の転記候補です。原資料との照合が必要です。",
                    now),
                CreateGeneratedQuestion(
                    secondQuestionId,
                    versionId,
                    orderIndex: 1,
                    "問2",
                    "東南アジア諸国連合の略称を書きなさい。",
                    "正答はAIによる提案です。先生が根拠資料と照合してください。",
                    now));
            db.AcceptedAnswers.AddRange(
                CreateGeneratedAnswer(
                    UlidId.New(now.AddTicks(11)),
                    firstQuestionId,
                    "光",
                    "provided_model_answer",
                    fileReferenceId,
                    now),
                CreateGeneratedAnswer(
                    UlidId.New(now.AddTicks(12)),
                    secondQuestionId,
                    "ASEAN",
                    "ai_proposed",
                    sourceFileReferenceId: null,
                    now));
            await db.SaveChangesAsync();
        });

        return new SeededGeneratedPublicationVersion(
            templateId,
            versionId,
            unitId,
            fileObjectId);
    }

    private static QuestionEntity CreateGeneratedQuestion(
        string id,
        string versionId,
        int orderIndex,
        string displayLabel,
        string questionText,
        string teacherNote,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            TemplateVersionId = versionId,
            LogicalQuestionId = UlidId.New(now.AddTicks(20 + orderIndex)),
            OrderIndex = orderIndex,
            DisplayLabel = displayLabel,
            QuestionText = questionText,
            QuestionType = "exact_short_text",
            GradingMode = "transcribe_then_rules",
            MaxPointsMilli = 1_000,
            PointIncrementMilli = 1_000,
            AllowNonKanji = false,
            TeacherNote = teacherNote
                + "\n[AI確認] [question.filled_answer_removal_unconfirmed] "
                + "記入済み内容を除外できたか確認してください。",
            RequiresReviewAlways = false,
            AiConfidenceBasisPoints = 9_800,
            TeacherVerified = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static AcceptedAnswerEntity CreateGeneratedAnswer(
        string id,
        string questionId,
        string text,
        string provenance,
        string? sourceFileReferenceId,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            QuestionId = questionId,
            AnswerText = text,
            NormalizedText = JapaneseTextNormalizer.NormalizeForComparison(text),
            VariantType = "canonical",
            TeacherVerified = false,
            AnswerProvenance = provenance,
            SourceFileReferenceId = sourceFileReferenceId,
            SourcePageNumber = sourceFileReferenceId is null ? null : 1,
            Locale = "ja-JP",
            CreatedAt = now,
            UpdatedAt = now,
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
        await application.WithDatabaseAsync(async db =>
        {
            var persistedVersion = await db.TemplateVersions
                .SingleAsync(item => item.Id == versionId);
            // The fixture's synthetic source has no readable PDF payload. Keep
            // the canonical page-count metadata that a real upload/publish flow
            // derives from its verified source.
            persistedVersion.ExpectedSubmissionPageCount = 1;
            await db.SaveChangesAsync();
        });
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

    private sealed record SeededGeneratedPublicationVersion(
        string TemplateId,
        string VersionId,
        string UnitId,
        string FileObjectId);

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
                        services.AddSingleton<ContentObjectLockProvider>();
                        services.AddSingleton<IContentStore>(provider =>
                            provider.GetRequiredService<TestContentStore>());
                        services.AddSingleton<IPdfPageCountReader,
                            LocalPdfPageCountReader>();
                        services.AddSingleton(connection);
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services.AddScoped<OrderedScanBatchService>();
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
                                    .RequireRole("administrator", "teacher"))
                            .AddPolicy(
                                "upload",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole(
                                        "administrator",
                                        "teacher",
                                        "scanOperator"))
                            .AddPolicy(
                                "review",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole(
                                        "administrator",
                                        "teacher",
                                        "readOnlyReviewer"));
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
                                endpoints.MapTestSessionsEndpoints();
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

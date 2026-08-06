using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Security;
using OokiGrader.Preprocessing;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateExtractionJobWorkerTests
{
    private static readonly byte[] SinglePageTiffFixture =
        Convert.FromBase64String(
            "SUkqABYAAACAAAAP+CP+BQWDQIAQEBEAAAEDAAEAAAACAAAAAQEDAAEAAAAC"
            + "AAAAAgEDAAIAAAAQABAAAwEDAAEAAAAFAAAABgEDAAEAAAABAAAACgEDAAEA"
            + "AAABAAAAEQEEAAEAAAAIAAAAEgEDAAEAAAABAAAAFQEDAAEAAAACAAAAFgED"
            + "AAEAAAACAAAAFwEEAAEAAAAOAAAAHAEDAAEAAAABAAAAKQEDAAIAAAAAAAEA"
            + "PQEDAAEAAAACAAAAPgEFAAIAAAAYAQAAPwEFAAYAAADoAAAAUgEDAAEAAAAC"
            + "AAAAAAAAAIXrUQAAAIAAw/WoAAAAAALNzEwAAAAAAc3MTAAAAIAAzcxMAAAA"
            + "AAKPwvUAAAAAEDcaoAAAAAACK4cKAAAAIAA=");
    private static readonly string[] AcceptedVariants = ["東京都"];
    private static readonly string[] ComparisonWarnings =
    [
        "独立比較では大阪という別案が出たため要確認。",
    ];

    [Fact]
    public async Task PreservesSuppliedAnswerAuthorityAndCreatesReviewOnlyDraft()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var version = await db.TemplateVersions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.VersionId);
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item =>
                item.TemplateVersionId == seeded.VersionId);
        var answers = await db.AcceptedAnswers
            .AsNoTracking()
            .Where(item => item.QuestionId == question.Id)
            .OrderBy(item => item.VariantType)
            .ToListAsync();
        var canonical = answers.Single(
            item => item.VariantType == "canonical");
        var regions = await db.Regions
            .AsNoTracking()
            .Where(item => item.OwnerId == question.Id)
            .ToListAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);

        Assert.Equal("draft", version.State);
        Assert.Equal(request.Id, version.AiGenerationProvenanceId);
        Assert.Null(version.PublishedAt);
        Assert.False(question.TeacherVerified);
        Assert.True(question.RequiresReviewAlways);
        Assert.Contains(
            "独立比較",
            question.TeacherNote,
            StringComparison.Ordinal);
        Assert.Equal("東京", canonical.AnswerText);
        Assert.Equal("provided_model_answer", canonical.AnswerProvenance);
        Assert.Equal(seeded.FileReferenceId, canonical.SourceFileReferenceId);
        Assert.Equal(1, canonical.SourcePageNumber);
        Assert.Null(canonical.SourceRegionId);
        Assert.DoesNotContain(
            answers,
            item => item.AnswerText == "大阪");
        Assert.Contains(
            answers,
            item => item.AnswerText == "東京都"
                && item.AnswerProvenance == "derived_variant");
        Assert.Empty(regions);
        Assert.Equal("succeeded", request.State);
        Assert.Equal("succeeded", job.State);
        Assert.Single(await db.AiUsage.AsNoTracking().ToListAsync());
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal("settled", reservation.State);
        Assert.True(
            reservation.ReservedUsdMicros >= 786_432,
            "The reservation must cover the worker's full 12 MiB "
            + "outbound-media allowance, not only compressed source bytes.");
        var template = await db.TestTemplates
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.TemplateId);
        Assert.Equal("Template extraction fixture", template.Title);
        Assert.Equal("社会", template.Subject);
        Assert.Equal("中学1年", template.GradeLabel);
        Assert.Equal("manual_ai_assisted", template.Source);
    }

    [Fact]
    public async Task SafeObjectiveDraftAvoidsPermanentReviewButStaysUnverified()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                confidence: 0.99));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item =>
                item.TemplateVersionId == seeded.VersionId);
        var answer = await db.AcceptedAnswers
            .AsNoTracking()
            .SingleAsync(item =>
                item.QuestionId == question.Id
                && item.VariantType == "canonical");

        Assert.False(question.RequiresReviewAlways);
        Assert.False(question.TeacherVerified);
        Assert.False(answer.TeacherVerified);
    }

    [Fact]
    public async Task DuplicateAnswerVariantsAreNormalizedWithoutLosingDraft()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                acceptedVariants: ["東京", "東京都", "東京都"]));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item => item.TemplateVersionId == seeded.VersionId);
        var answers = await db.AcceptedAnswers
            .AsNoTracking()
            .Where(item => item.QuestionId == question.Id)
            .OrderBy(item => item.VariantType)
            .ToListAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);

        Assert.Equal("succeeded", request.State);
        Assert.Equal("succeeded", job.State);
        Assert.Collection(
            answers,
            canonical =>
            {
                Assert.Equal("canonical", canonical.VariantType);
                Assert.Equal("東京", canonical.AnswerText);
            },
            variant =>
            {
                Assert.Equal("equivalent", variant.VariantType);
                Assert.Equal("東京都", variant.AnswerText);
            });
    }

    [Fact]
    public async Task ReplacesFilenameFallbackButPreservesTeacherMetadata()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers",
            templateTitle: "中1 社会 問題用紙",
            templateSubject: "先生指定教科",
            replaceableMetadataFields: ["title"]);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var template = await db.TestTemplates
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.TemplateId);

        Assert.Equal("中学1年 社会科 地理", template.Title);
        Assert.Equal("先生指定教科", template.Subject);
        Assert.Contains(
            "\"replaceable_metadata_fields\":[\"title\"]",
            Assert.Single(fixture.Provider.Requests).UserInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsFilenameFallbackWhenAiMetadataIsLowConfidence()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(request, metadataConfidence: 0.50));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers",
            templateTitle: "中1 社会 問題用紙",
            templateSubject: "社会",
            replaceableMetadataFields: ["title", "subject"]);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var template = await db.TestTemplates
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.TemplateId);

        Assert.Equal("中1 社会 問題用紙", template.Title);
        Assert.Equal("社会", template.Subject);
    }

    [Fact]
    public async Task PreservesIndependentQuestionsOnInterleavedQuestionSheet()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            CreateInterleavedResponse);
        var seeded = await fixture.SeedAsync(sourceRole: "blank_test");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var questions = await db.Questions
            .AsNoTracking()
            .Where(item => item.TemplateVersionId == seeded.VersionId)
            .OrderBy(item => item.OrderIndex)
            .ToArrayAsync();

        Assert.Collection(
            questions,
            first =>
            {
                Assert.Equal("基本①", first.DisplayLabel);
                Assert.Null(first.QuestionRegionId);
                Assert.Null(first.AnswerRegionId);
            },
            second =>
            {
                Assert.Equal("発展③", second.DisplayLabel);
                Assert.Null(second.QuestionRegionId);
                Assert.Null(second.AnswerRegionId);
            });
        Assert.Contains(
            "Questions, reference maps/tables, and writable answer areas may be",
            Assert.Single(fixture.Provider.Requests).UserInstruction,
            StringComparison.Ordinal);
        Assert.All(questions, item => Assert.True(item.RequiresReviewAlways));
    }

    [Fact]
    public async Task FillBlankSlotsStaySeparateAndVisibleAnswersAreRedacted()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            CreateJapaneseScienceFillBlankResponse);
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "小学校理科_光_記入済み.png");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var questions = await db.Questions
            .AsNoTracking()
            .Where(item => item.TemplateVersionId == seeded.VersionId)
            .OrderBy(item => item.OrderIndex)
            .ToArrayAsync();
        var answers = await db.AcceptedAnswers
            .AsNoTracking()
            .Where(item => questions.Select(question => question.Id)
                .Contains(item.QuestionId)
                && item.VariantType == "canonical")
            .ToDictionaryAsync(item => item.QuestionId);
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);

        Assert.Equal("succeeded", request.State);
        Assert.Equal(11, questions.Length);
        Assert.Equal(
            [
                "①",
                "②",
                "③",
                "④",
                "③（2回目）",
                "⑤",
                "⑥",
                "⑦",
                "⑧",
                "⑧（2回目）",
                "⑧（3回目）",
            ],
            questions.Select(question => question.DisplayLabel));
        Assert.Equal(
            [
                "光",
                "太陽",
                "光源",
                "月",
                "光源",
                "かげ",
                "直進",
                "上下左右",
                "反射",
                "反射",
                "反射",
            ],
            questions.Select(question => answers[question.Id].AnswerText));
        Assert.All(
            questions,
            question =>
            {
                Assert.Contains("［　］", question.QuestionText);
                Assert.DoesNotContain(
                    $"［{answers[question.Id].AnswerText}］",
                    question.QuestionText,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    $"[{answers[question.Id].AnswerText}]",
                    question.QuestionText,
                    StringComparison.Ordinal);
                Assert.True(question.RequiresReviewAlways);
                Assert.Contains(
                    "question.filled_answer_redacted",
                    question.TeacherNote,
                    StringComparison.Ordinal);
            });
        Assert.Equal(2, fixture.Provider.Requests.Count);
        var primaryRequest = fixture.Provider.Requests[0];
        Assert.Contains(
            "every curricular slot",
            primaryRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never renumber ⑧ as ⑨ or as 2①",
            primaryRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "A filled name box or a handwritten score is not an answer slot",
            primaryRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "administrative or scoring fields",
            primaryRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "INTERNAL QUALITY-CONTROL PASS",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.Single(primaryRequest.Media);
        var qualityControlRequest = fixture.Provider.Requests[1];
        Assert.Equal(5, qualityControlRequest.Media.Count);
        Assert.Contains(
            "not five sources or five pages",
            qualityControlRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "non-overlapping top-to-bottom quarters",
            qualityControlRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Return every physical curricular slot",
            qualityControlRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.All(
            fixture.Provider.Requests,
            request => Assert.Equal("MEDIUM", request.ThinkingLevel));
        Assert.All(
            qualityControlRequest.Media.Skip(1),
            detailView =>
            {
                Assert.Equal("image/png", detailView.MimeType);
                Assert.True(detailView.Bytes.Span[..8].SequenceEqual(
                    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(detailView.Bytes.Span))
                        .ToLowerInvariant(),
                    detailView.Sha256);
            });
        Assert.Equal(
            4,
            fixture.Provider.BorrowedQualityControlDetailViews.Count);
        Assert.All(
            fixture.Provider.BorrowedQualityControlDetailViews,
            detailView => Assert.All(
                detailView.ToArray(),
                value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(13)]
    public async Task IndependentImageAuditRepairsVariableSlotInventory(
        int initialQuestionCount)
    {
        var providerCall = 0;
        AiProviderResponse Respond(AiProviderRequest request)
        {
            var response = request.UserInstruction.StartsWith(
                "INTERNAL QUALITY-CONTROL PASS",
                StringComparison.Ordinal)
                || request.UserInstruction.StartsWith(
                    "INTERNAL FINAL SLOT RECONCILIATION",
                    StringComparison.Ordinal)
                ? CreateJapaneseScienceFillBlankResponse(request)
                : CreateJapaneseScienceFillBlankResponse(
                    request,
                    questionCount: initialQuestionCount);
            return response with
            {
                RoutedProvider = initialQuestionCount == 9
                    && ++providerCall > 1
                        ? "OtherRoute"
                        : "Google",
                Usage = response.Usage with
                {
                    ProviderCostUsdMicros = 100,
                },
            };
        }

        await using var fixture = await ExtractionFixture.CreateAsync(Respond);
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "小学校理科_光_記入済み.png");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var questions = await db.Questions
            .AsNoTracking()
            .Where(item => item.TemplateVersionId == seeded.VersionId)
            .OrderBy(item => item.OrderIndex)
            .ToArrayAsync();
        var usage = await db.AiUsage.AsNoTracking().SingleAsync();

        Assert.Equal(11, questions.Length);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.StartsWith(
            "INTERNAL QUALITY-CONTROL PASS",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "INTERNAL FINAL SLOT RECONCILIATION",
            fixture.Provider.Requests[2].UserInstruction,
            StringComparison.Ordinal);
        Assert.Single(fixture.Provider.Requests[0].Media);
        Assert.Equal(5, fixture.Provider.Requests[1].Media.Count);
        Assert.Equal(5, fixture.Provider.Requests[2].Media.Count);
        Assert.All(
            fixture.Provider.Requests,
            request => Assert.Equal("MEDIUM", request.ThinkingLevel));
        Assert.Equal(3_600, usage.InputTokens);
        Assert.Equal(2_700, usage.OutputTokens);
        Assert.Equal(6_300, usage.TotalTokens);
        Assert.Equal(300, usage.EstimatedUsdMicros);
        Assert.Equal(
            initialQuestionCount == 9
                ? AiProviders.GeminiDirect
                : "Google",
            usage.ActualProvider);
    }

    [Fact]
    public async Task QualityControlDetailViewsRespectOutboundMediaLimit()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            CreateJapaneseScienceFillBlankResponse,
            maximumMediaBytes: 1_024);
        await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "上限確認.png",
            sourceBytesOverride: new byte[900]);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.All(
            fixture.Provider.Requests,
            request => Assert.Single(request.Media));
        Assert.Empty(fixture.Provider.BorrowedQualityControlDetailViews);
    }

    [Fact]
    public async Task QualityControlDetailViewsStayOffForTiffSource()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            CreateJapaneseScienceFillBlankResponse);
        await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceMimeType: "image/tiff",
            sourceDisplayName: "複数ページ対応外.tiff");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.All(
            fixture.Provider.Requests,
            request => Assert.Single(request.Media));
        Assert.Empty(fixture.Provider.BorrowedQualityControlDetailViews);
    }

    [Fact]
    public async Task InvalidInitialSchemaRecoversAfterIndependentAudit()
    {
        var providerCall = 0;
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                displayLabel: ++providerCall == 1 ? "" : "問1"));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var usage = await db.AiUsage.AsNoTracking().SingleAsync();
        var version = await db.TemplateVersions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.VersionId);

        Assert.Equal("succeeded", request.State);
        Assert.Equal("succeeded", job.State);
        Assert.Equal("draft", version.State);
        Assert.Equal(TemplateExtractionJobWorker.PipelineVersion,
            version.PipelineVersion);
        Assert.Single(await db.Questions.AsNoTracking().ToListAsync());
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Provider.Requests[0].Media);
        Assert.Equal(5, fixture.Provider.Requests[1].Media.Count);
        Assert.Equal(5, fixture.Provider.Requests[2].Media.Count);
        Assert.StartsWith(
            "INTERNAL VALIDATION-RECOVERY PASS",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "INTERNAL QUALITY-CONTROL PASS",
            fixture.Provider.Requests[2].UserInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "template_extract_display_label_invalid",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.Equal(2_700, usage.InputTokens);
        Assert.Equal(660, usage.OutputTokens);
        Assert.Equal(3_360, usage.TotalTokens);
        Assert.Equal(
            8,
            fixture.Provider.BorrowedQualityControlDetailViews.Count);
        Assert.All(
            fixture.Provider.BorrowedQualityControlDetailViews,
            detailView => Assert.All(
                detailView.ToArray(),
                value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task RepeatedInvalidRecoveryRemainsBlockedAfterThreeCalls()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                displayLabel: ""));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var usage = await db.AiUsage.AsNoTracking().SingleAsync();

        Assert.Equal("invalid_output", request.State);
        Assert.Equal("template_extract_display_label_invalid",
            request.ErrorCode);
        Assert.Equal("blocked", job.State);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Provider.Requests[0].Media);
        Assert.Equal(5, fixture.Provider.Requests[1].Media.Count);
        Assert.Equal(5, fixture.Provider.Requests[2].Media.Count);
        Assert.StartsWith(
            "INTERNAL VALIDATION-RECOVERY PASS",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "INTERNAL VALIDATION-RECOVERY PASS",
            fixture.Provider.Requests[2].UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "INTERNAL QUALITY-CONTROL PASS",
            fixture.Provider.Requests[2].UserInstruction,
            StringComparison.Ordinal);
        Assert.Empty(await db.Questions.AsNoTracking().ToListAsync());
        Assert.Equal(2_700, usage.InputTokens);
        Assert.Equal(660, usage.OutputTokens);
        Assert.Equal(3_360, usage.TotalTokens);
        Assert.Equal(
            8,
            fixture.Provider.BorrowedQualityControlDetailViews.Count);
        Assert.All(
            fixture.Provider.BorrowedQualityControlDetailViews,
            detailView => Assert.All(
                detailView.ToArray(),
                value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task RecoveryDoesNotBypassInvalidIndependentAudit()
    {
        var providerCall = 0;
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                displayLabel: ++providerCall == 2 ? "問1" : ""));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);

        Assert.Equal("invalid_output", request.State);
        Assert.Equal("template_extract_display_label_invalid",
            request.ErrorCode);
        Assert.Equal("blocked", job.State);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Empty(await db.Questions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task MergedFillBlankSlotsProduceDraftButCannotBeBulkConfirmed()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            CreateMergedFillBlankResponse);
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "小学校理科_光_結合誤り.png");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var version = await db.TemplateVersions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.VersionId);
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item => item.TemplateVersionId == seeded.VersionId);
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);

        Assert.Equal("draft", version.State);
        Assert.Equal("succeeded", request.State);
        Assert.True(question.RequiresReviewAlways);
        Assert.Equal(1, question.QuestionText.Split("［　］").Length - 1);
        Assert.DoesNotContain("［反射］", question.QuestionText);
        Assert.Contains(
            "question.answer_slots_not_separated",
            question.TeacherNote,
            StringComparison.Ordinal);
        Assert.Contains(
            "question.additional_placeholders_redacted",
            question.TeacherNote,
            StringComparison.Ordinal);
        Assert.Contains(
            "question.answer_slot_inventory_mismatch",
            question.TeacherNote,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedPrintedLabelsAreDisambiguatedWithoutFalseBlocker()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            CreateRepeatedPrintedLabelResponse);
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var questions = await db.Questions
            .AsNoTracking()
            .Where(item => item.TemplateVersionId == seeded.VersionId)
            .OrderBy(item => item.OrderIndex)
            .ToArrayAsync();

        Assert.Equal(["⑧", "⑧（2回目）"], questions.Select(item => item.DisplayLabel));
        Assert.All(questions, item => Assert.False(item.RequiresReviewAlways));
        Assert.DoesNotContain(
            questions,
            item => item.TeacherNote?.Contains(
                "question.repeated_printed_label_disambiguated",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RejectsProvidedAnswerClaimFromBlankSource()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(sourceRole: "blank_test");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var version = await db.TemplateVersions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.VersionId);
        Assert.Equal("invalid_output", request.State);
        Assert.Equal(
            "template_extract_provided_answer_source_invalid",
            request.ErrorCode);
        Assert.Equal("draft", version.State);
        Assert.Null(version.AiGenerationProvenanceId);
        Assert.Empty(await db.Questions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task NonModelAnswersAreIgnoredAndAiSolvesIndependently()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                aiProposed: true));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "中1社会_生徒答案.png");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item => item.TemplateVersionId == seeded.VersionId);
        var canonical = await db.AcceptedAnswers
            .AsNoTracking()
            .SingleAsync(item =>
                item.QuestionId == question.Id
                && item.VariantType == "canonical");
        var request = Assert.Single(fixture.Provider.Requests);

        Assert.Equal("ai_proposed", canonical.AnswerProvenance);
        Assert.Null(canonical.SourceFileReferenceId);
        Assert.Null(canonical.SourcePageNumber);
        Assert.Null(canonical.SourceRegionId);
        Assert.Equal("template-extract-v1.8.3", request.PromptVersion);
        Assert.Contains(
            "use your own subject-matter knowledge only",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "preserve Japanese script exactly",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "never splice a kana proposal and a Kanji proposal",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Do not use web search or external knowledge.",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"source_role\":\"contains_non_model_answers\"",
            request.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "must be ignored as answer authority",
            request.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "ai_proposed with answer_source null",
            request.UserInstruction,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("光が鏡ではじか返される性質。")]
    [InlineData("光が鏡ではい返される性質。")]
    [InlineData("光が鏡ではいね返される性質。")]
    [InlineData("光が鏡ではいかはね返される性質。")]
    [InlineData("光が鏡ではいね返る性質。")]
    public async Task CorrectsBoundedJapaneseBounceOcrNoise(
        string malformedQuestionText)
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                questionText: malformedQuestionText));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item => item.TemplateVersionId == seeded.VersionId);

        Assert.Equal("光が鏡ではね返される性質。", question.QuestionText);
        Assert.Contains(
            "question.ocr_noise_corrected",
            question.TeacherNote,
            StringComparison.Ordinal);
        Assert.False(question.RequiresReviewAlways);
    }

    [Fact]
    public async Task LeavesCorrectJapaneseBounceTextAndNearbyWordsUntouched()
    {
        const string original =
            "光は鏡ではね返される。答えはいかにもとめる。";
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                questionText: original));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item => item.TemplateVersionId == seeded.VersionId);

        Assert.Equal(original, question.QuestionText);
        Assert.DoesNotContain(
            "question.ocr_noise_corrected",
            question.TeacherNote ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsProvidedAnswerClaimFromNonModelAnswerSource()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "中1社会_生徒答案.png");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        Assert.Equal("invalid_output", request.State);
        Assert.Equal(
            "template_extract_provided_answer_source_invalid",
            request.ErrorCode);
        Assert.Empty(await db.Questions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SeparateAnswerKeyOverridesNonModelFilledAnswers()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                sourceId: ExtractSourceId(
                    request.UserInstruction,
                    "contains_non_model_answers"),
                answerSourceId: ExtractSourceId(
                    request.UserInstruction,
                    "separate_answer_key"),
                warnings: Array.Empty<string>()));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "中1社会_生徒答案.png",
            additionalSourceRole: "separate_answer_key",
            additionalSourceDisplayName: "中1社会_模範解答.png");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var separateKey = await db.TemplateSources
            .AsNoTracking()
            .SingleAsync(item => item.SourceRole == "separate_answer_key");
        var canonical = await db.AcceptedAnswers
            .AsNoTracking()
            .SingleAsync(item => item.VariantType == "canonical");

        Assert.Equal("provided_model_answer", canonical.AnswerProvenance);
        Assert.Equal(
            separateKey.FileReferenceId,
            canonical.SourceFileReferenceId);
        Assert.Equal(2, Assert.Single(fixture.Provider.Requests).Media.Count);
    }

    [Fact]
    public async Task AiProposalWithSeparateAnswerKeyPersistsBlockedDraft()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                sourceId: ExtractSourceId(
                    request.UserInstruction,
                    "contains_non_model_answers"),
                warnings: Array.Empty<string>(),
                aiProposed: true));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_non_model_answers",
            sourceDisplayName: "中1社会_生徒答案.png",
            additionalSourceRole: "separate_answer_key",
            additionalSourceDisplayName: "中1社会_模範解答.png");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item => item.TemplateVersionId == seeded.VersionId);
        var answer = await db.AcceptedAnswers
            .AsNoTracking()
            .SingleAsync(item => item.QuestionId == question.Id
                && item.VariantType == "canonical");
        Assert.Equal("succeeded", request.State);
        Assert.Null(request.ErrorCode);
        Assert.Equal("ai_proposed", answer.AnswerProvenance);
        Assert.Null(answer.SourceFileReferenceId);
        Assert.True(question.RequiresReviewAlways);
        Assert.Contains(
            "answer.source_conflict_or_ambiguity",
            question.TeacherNote,
            StringComparison.Ordinal);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.StartsWith(
            "INTERNAL QUALITY-CONTROL PASS",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "INTERNAL FINAL SLOT RECONCILIATION",
            fixture.Provider.Requests[2].UserInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconcilesWrongAiProvenanceAgainstAuthoritativePixels()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: Array.Empty<string>(),
                aiProposed: !request.UserInstruction.StartsWith(
                    "INTERNAL FINAL SLOT RECONCILIATION",
                    StringComparison.Ordinal)));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var canonical = await db.AcceptedAnswers
            .AsNoTracking()
            .SingleAsync(item => item.VariantType == "canonical");

        Assert.Equal("東京", canonical.AnswerText);
        Assert.Equal(
            "provided_model_answer",
            canonical.AnswerProvenance);
        Assert.Equal(seeded.FileReferenceId, canonical.SourceFileReferenceId);
        Assert.Equal(1, canonical.SourcePageNumber);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Contains(
            "never substitute",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "ai_proposed for an authoritative source",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "provenance label without independently checking the visible answer",
            fixture.Provider.Requests[2].UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "FINAL SOURCE-ROLE GATE",
            fixture.Provider.Requests[2].UserInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableAuthoritativeAnswerStaysUnavailableAfterAudit()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                warnings: ["記入内容を判読できません。"],
                unavailable: true));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var question = await db.Questions
            .AsNoTracking()
            .SingleAsync(item => item.TemplateVersionId == seeded.VersionId);

        Assert.Empty(await db.AcceptedAnswers.AsNoTracking().ToListAsync());
        Assert.True(question.RequiresReviewAlways);
        Assert.Contains(
            "answer.supplied_answer_missing",
            question.TeacherNote,
            StringComparison.Ordinal);
        Assert.Equal(3, fixture.Provider.Requests.Count);
    }

    [Fact]
    public async Task RejectsUnknownSourceAtomically()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            request => CreateResponse(
                request,
                sourceId: "unknown-source"));
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("invalid_output", request.State);
        Assert.Equal(
            "template_extract_unknown_source",
            request.ErrorCode);
        Assert.Null(request.ValidatedResponseJson);
        Assert.Equal("blocked", job.State);
        Assert.Empty(await db.Questions.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AcceptedAnswers.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Regions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ContentInspectionCredentialAndProviderStayOutsideWriteLock()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        await fixture.SeedAsync(sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.False(fixture.ContentStore.ObservedInsideWriteCoordinator);
        Assert.False(fixture.Preprocessing.ObservedInsideWriteCoordinator);
        Assert.False(fixture.SecretStore.ObservedInsideWriteCoordinator);
        Assert.False(fixture.Provider.ObservedInsideWriteCoordinator);
        var providerRequest = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(AiTaskTypes.TemplateExtraction, providerRequest.TaskType);
        Assert.Equal("template_extract_v4", providerRequest.SchemaVersion);
        Assert.Contains(
            "\"source_role\":\"contains_model_answers\"",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Single(providerRequest.Media);
    }

    [Fact]
    public async Task NormalizesSinglePageTiffBeforeProviderDisclosure()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        await fixture.SeedAsync(
            sourceRole: "contains_model_answers",
            sourceMimeType: "image/tiff",
            sourceDisplayName: "模範解答.tiff");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var providerRequest = Assert.Single(fixture.Provider.Requests);
        var media = Assert.Single(providerRequest.Media);
        Assert.Equal("image/png", media.MimeType);
        Assert.Equal(
            FakePreprocessingService.NormalizedPngBytes,
            media.Bytes.ToArray());
        Assert.Equal(
            FakePreprocessingService.NormalizedPngSha256,
            media.Sha256);
        Assert.Contains(
            "\"page_count\":1",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertsMultiPageTiffToPdfBeforeProviderDisclosure()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            preprocessingPageCount: 2);
        await fixture.SeedAsync(
            sourceRole: "contains_model_answers",
            sourceMimeType: "image/tiff",
            sourceDisplayName: "模範解答.tiff");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var providerRequest = Assert.Single(fixture.Provider.Requests);
        var media = Assert.Single(providerRequest.Media);
        Assert.Equal("application/pdf", media.MimeType);
        Assert.True(
            media.Bytes.Span[..5].SequenceEqual("%PDF-"u8));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(media.Bytes.Span))
                .ToLowerInvariant(),
            media.Sha256);
        Assert.Contains(
            "\"page_count\":2",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessesRealTiffCodecOnWorkerPath()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            useRealPreprocessing: true);
        await fixture.SeedAsync(
            sourceRole: "contains_model_answers",
            sourceMimeType: "image/tiff",
            sourceDisplayName: "模範解答.tiff",
            sourceBytesOverride: SinglePageTiffFixture);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var providerRequest = Assert.Single(fixture.Provider.Requests);
        var media = Assert.Single(providerRequest.Media);
        Assert.Equal("image/png", media.MimeType);
        Assert.True(media.Bytes.Span[..8].SequenceEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Contains(
            "\"page_count\":1",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedTiffFailsPermanentlyWithoutProviderRetry()
    {
        await using var fixture = await ExtractionFixture.CreateAsync(
            useRealPreprocessing: true);
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers",
            sourceMimeType: "image/tiff",
            sourceDisplayName: "破損.tiff",
            sourceBytesOverride:
                [0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0]);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.VersionId);
        Assert.Equal("failed", job.State);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal("template_source_image_invalid", job.ErrorCode);
        Assert.Equal("failed", request.State);
        Assert.Empty(fixture.Provider.Requests);
    }

    [Fact]
    public async Task RedeliveryAfterCommittedDraftDoesNotCallProviderAgain()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            sourceRole: "contains_model_answers");

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await fixture.RequeueAsync(seeded.JobId);
        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Single(fixture.Provider.Requests);
        await using var db = await fixture.CreateDbContextAsync();
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(2, job.AttemptCount);
        Assert.Single(await db.Questions.AsNoTracking().ToListAsync());
        Assert.Single(await db.AiRequests.AsNoTracking().ToListAsync());
    }

    private static AiProviderResponse CreateResponse(
        AiProviderRequest request,
        string? sourceId = null,
        string? answerSourceId = null,
        IReadOnlyCollection<string>? warnings = null,
        double confidence = 0.98,
        double metadataConfidence = 0.96,
        bool aiProposed = false,
        IReadOnlyCollection<string>? acceptedVariants = null,
        string questionText = "日本の首都を書きなさい。",
        bool unavailable = false,
        string displayLabel = "問1")
    {
        sourceId ??= ExtractSourceId(request.UserInstruction);
        answerSourceId ??= sourceId;
        warnings ??= ComparisonWarnings;
        acceptedVariants ??= AcceptedVariants;
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "template_extract_v4",
                request_key = request.RequestKey,
                metadata = new
                {
                    title = "中学1年 社会科 地理",
                    subject = "社会",
                    category = "定期テスト",
                    grade_label = "中学1年",
                    course = "地理",
                    confidence = metadataConfidence,
                    warnings = Array.Empty<string>(),
                },
                pages = new[]
                {
                    new
                    {
                        source_id = sourceId,
                        page_number = 1,
                        detected_answer_slot_count = 1,
                        questions = new[]
                        {
                            new
                            {
                                source_key = "page1-q1",
                                display_label = displayLabel,
                                question_text = questionText,
                                answer_slot_ordinal = 1,
                                answer_slot_count = 1,
                                filled_answer_removed = true,
                                is_embedded_fill_blank = false,
                                question_type = "exact_short_text",
                                expected_answer = unavailable
                                    ? null
                                    : "東京",
                                answer_provenance = unavailable
                                    ? "unavailable"
                                    : aiProposed
                                        ? "ai_proposed"
                                        : "provided_model_answer",
                                answer_source = unavailable || aiProposed
                                    ? (object?)null
                                    : new
                                    {
                                        source_id = answerSourceId,
                                        page_number = 1,
                                    },
                                accepted_variants = unavailable
                                    ? Array.Empty<string>()
                                    : acceptedVariants,
                                suggested_points_milli = 1_000,
                                allow_non_kanji_suggestion = false,
                                requires_teacher_answer = unavailable,
                                confidence,
                                warnings,
                            },
                        },
                    },
                },
                global_warnings = Array.Empty<string>(),
            }));
        return new AiProviderResponse(
            AiProviders.GeminiDirect,
            TemplateExtractionJobWorker.ModelId,
            TemplateExtractionJobWorker.ModelId,
            "template-response-1",
            "STOP",
            document.RootElement.Clone(),
            new AiUsage(
                PromptTokens: 900,
                CachedTokens: 0,
                OutputTokens: 220,
                ThinkingTokens: 0,
                TotalTokens: 1_120),
            TimeSpan.FromMilliseconds(25));
    }

    private static AiProviderResponse CreateInterleavedResponse(
        AiProviderRequest request)
    {
        var sourceId = ExtractSourceId(request.UserInstruction);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "template_extract_v4",
                request_key = request.RequestKey,
                metadata = new
                {
                    title = "中学1年 社会科 地理",
                    subject = "社会",
                    category = "演習",
                    grade_label = "中学1年",
                    course = "地理",
                    confidence = 0.95,
                    warnings = Array.Empty<string>(),
                },
                pages = new[]
                {
                    new
                    {
                        source_id = sourceId,
                        page_number = 1,
                        detected_answer_slot_count = 2,
                        questions = new object[]
                        {
                            new
                            {
                                source_key = "basic-1",
                                display_label = "基本①",
                                question_text =
                                    "東南アジア諸国連合を何というか。",
                                answer_slot_ordinal = 1,
                                answer_slot_count = 1,
                                filled_answer_removed = true,
                                is_embedded_fill_blank = false,
                                question_type = "exact_short_text",
                                expected_answer = "ASEAN",
                                answer_provenance = "ai_proposed",
                                answer_source = (object?)null,
                                accepted_variants = Array.Empty<string>(),
                                suggested_points_milli = 1_000,
                                allow_non_kanji_suggestion = false,
                                requires_teacher_answer = true,
                                confidence = 0.95,
                                warnings = Array.Empty<string>(),
                            },
                            new
                            {
                                source_key = "advanced-3",
                                display_label = "発展③",
                                question_text =
                                    "資料を参考に輸出品の変化を書きなさい。",
                                answer_slot_ordinal = 2,
                                answer_slot_count = 1,
                                filled_answer_removed = true,
                                is_embedded_fill_blank = false,
                                question_type = "subjective",
                                expected_answer =
                                    "天然ゴム中心から機械類中心へ変化した。",
                                answer_provenance = "ai_proposed",
                                answer_source = (object?)null,
                                accepted_variants = Array.Empty<string>(),
                                suggested_points_milli = 3_000,
                                allow_non_kanji_suggestion = false,
                                requires_teacher_answer = true,
                                confidence = 0.91,
                                warnings = Array.Empty<string>(),
                            },
                        },
                    },
                },
                global_warnings = Array.Empty<string>(),
            }));
        return new AiProviderResponse(
            AiProviders.GeminiDirect,
            TemplateExtractionJobWorker.ModelId,
            TemplateExtractionJobWorker.ModelId,
            "template-interleaved-response-1",
            "STOP",
            document.RootElement.Clone(),
            new AiUsage(
                PromptTokens: 1_100,
                CachedTokens: 0,
                OutputTokens: 410,
                ThinkingTokens: 0,
                TotalTokens: 1_510),
            TimeSpan.FromMilliseconds(25));
    }

    private static AiProviderResponse CreateJapaneseScienceFillBlankResponse(
        AiProviderRequest request) =>
        CreateJapaneseScienceFillBlankResponse(request, questionCount: 11);

    private static AiProviderResponse CreateJapaneseScienceFillBlankResponse(
        AiProviderRequest request,
        int questionCount)
    {
        var sourceId = ExtractSourceId(request.UserInstruction);
        string[] labels =
        [
            "①", "②", "③", "④", "③", "⑤", "⑥", "⑦", "⑧", "⑧", "⑧",
            "⑧", "⑨",
        ];
        string[] answers =
            [
                "光",
                "太陽",
                "光源",
                "月",
                "光源",
                "かげ",
                "直進",
                "上下左右",
                "反射",
                "反射",
                "反射",
                "光",
                "入射光線",
            ];
        var questions = labels.Select(
                (label, index) => new
                {
                    source_key =
                        $"page-1-slot-{index + 1}-printed-{label}",
                    display_label = label,
                    question_text =
                        $"光について、{label}の［{answers[index]}］に入る語を書きなさい。",
                    answer_slot_ordinal = index + 1,
                    answer_slot_count = 1,
                    filled_answer_removed = false,
                    is_embedded_fill_blank = true,
                    question_type = "exact_short_text",
                    expected_answer = answers[index],
                    answer_provenance = "ai_proposed",
                    answer_source = (object?)null,
                    accepted_variants = Array.Empty<string>(),
                    suggested_points_milli = 1_000,
                    allow_non_kanji_suggestion = false,
                    requires_teacher_answer = false,
                    confidence = 0.99,
                    warnings = Array.Empty<string>(),
                })
            .Take(questionCount)
            .ToArray();
        return CreateTemplateResponse(
            request,
            sourceId,
            detectedAnswerSlotCount: questionCount,
            questions);
    }

    private static AiProviderResponse CreateMergedFillBlankResponse(
        AiProviderRequest request)
    {
        var sourceId = ExtractSourceId(request.UserInstruction);
        var questions = new[]
        {
            new
            {
                source_key = "page-1-slots-9-and-10-printed-⑧",
                display_label = "⑧",
                question_text = "性質を［反射］という。前の光を［反射］光線という。",
                answer_slot_ordinal = 1,
                answer_slot_count = 2,
                filled_answer_removed = false,
                is_embedded_fill_blank = true,
                question_type = "exact_short_text",
                expected_answer = "反射",
                answer_provenance = "ai_proposed",
                answer_source = (object?)null,
                accepted_variants = Array.Empty<string>(),
                suggested_points_milli = 1_000,
                allow_non_kanji_suggestion = false,
                requires_teacher_answer = false,
                confidence = 0.99,
                warnings = Array.Empty<string>(),
            },
        };
        return CreateTemplateResponse(
            request,
            sourceId,
            detectedAnswerSlotCount: 2,
            questions);
    }

    private static AiProviderResponse CreateRepeatedPrintedLabelResponse(
        AiProviderRequest request)
    {
        var sourceId = ExtractSourceId(request.UserInstruction);
        var questions = Enumerable.Range(1, 2)
            .Select(index => new
            {
                source_key = $"page-1-slot-{index}-printed-⑧",
                display_label = "⑧",
                question_text = $"光の性質{index}を［　］という。",
                answer_slot_ordinal = index,
                answer_slot_count = 1,
                filled_answer_removed = true,
                is_embedded_fill_blank = true,
                question_type = "exact_short_text",
                expected_answer = "反射",
                answer_provenance = "ai_proposed",
                answer_source = (object?)null,
                accepted_variants = Array.Empty<string>(),
                suggested_points_milli = 500,
                allow_non_kanji_suggestion = false,
                requires_teacher_answer = false,
                confidence = 0.99,
                warnings = Array.Empty<string>(),
            })
            .ToArray();
        return CreateTemplateResponse(
            request,
            sourceId,
            detectedAnswerSlotCount: 2,
            questions);
    }

    private static AiProviderResponse CreateTemplateResponse<TQuestion>(
        AiProviderRequest request,
        string sourceId,
        int detectedAnswerSlotCount,
        IReadOnlyCollection<TQuestion> questions)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "template_extract_v4",
                request_key = request.RequestKey,
                metadata = new
                {
                    title = "第9回 光",
                    subject = "理科",
                    category = "まとめ",
                    grade_label = (string?)null,
                    course = "光",
                    confidence = 0.99,
                    warnings = Array.Empty<string>(),
                },
                pages = new[]
                {
                    new
                    {
                        source_id = sourceId,
                        page_number = 1,
                        detected_answer_slot_count = detectedAnswerSlotCount,
                        questions,
                    },
                },
                global_warnings = Array.Empty<string>(),
            }));
        return new AiProviderResponse(
            AiProviders.GeminiDirect,
            TemplateExtractionJobWorker.ModelId,
            TemplateExtractionJobWorker.ModelId,
            "template-fill-blank-response-1",
            "STOP",
            document.RootElement.Clone(),
            new AiUsage(
                PromptTokens: 1_200,
                CachedTokens: 0,
                OutputTokens: 900,
                ThinkingTokens: 0,
                TotalTokens: 2_100),
            TimeSpan.FromMilliseconds(25));
    }

    private static string ExtractSourceId(
        string instruction,
        string? sourceRole = null)
    {
        var start = instruction.IndexOf('{');
        using var document = JsonDocument.Parse(instruction[start..]);
        var sources = document.RootElement.GetProperty("sources");
        var source = sourceRole is null
            ? sources[0]
            : sources.EnumerateArray().Single(item =>
                item.GetProperty("source_role").GetString() == sourceRole);
        return source
            .GetProperty("source_id")
            .GetString()!;
    }

    private sealed class ExtractionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly string _connectionId;
        private readonly string _secretReference;

        private ExtractionFixture(
            SqliteConnection connection,
            ServiceProvider services,
            string connectionId,
            string secretReference,
            BoundaryWriteCoordinator writeCoordinator,
            FakeContentStore contentStore,
            IObservedPreprocessingService preprocessing,
            ObservingSecretStore secretStore,
            FakeAiProvider provider)
        {
            _connection = connection;
            _services = services;
            _connectionId = connectionId;
            _secretReference = secretReference;
            WriteCoordinator = writeCoordinator;
            ContentStore = contentStore;
            Preprocessing = preprocessing;
            SecretStore = secretStore;
            Provider = provider;
            Worker = services.GetRequiredService<TemplateExtractionJobWorker>();
        }

        public TemplateExtractionJobWorker Worker { get; }
        public BoundaryWriteCoordinator WriteCoordinator { get; }
        public FakeContentStore ContentStore { get; }
        public IObservedPreprocessingService Preprocessing { get; }
        public ObservingSecretStore SecretStore { get; }
        public FakeAiProvider Provider { get; }

        public static async Task<ExtractionFixture> CreateAsync(
            Func<AiProviderRequest, AiProviderResponse>? responseFactory = null,
            int preprocessingPageCount = 1,
            bool useRealPreprocessing = false,
            int maximumMediaBytes = 12 * 1024 * 1024)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var boundary = new BoundaryProbe();
            var writeCoordinator = new BoundaryWriteCoordinator(boundary);
            var contentStore = new FakeContentStore(boundary);
            IObservedPreprocessingService preprocessing =
                useRealPreprocessing
                    ? new ObservingPreprocessingService(
                        boundary,
                        new PreprocessingService())
                    : new FakePreprocessingService(
                        boundary,
                        preprocessingPageCount);
            var provider = new FakeAiProvider(
                boundary,
                responseFactory ?? (request => CreateResponse(request)));
            var secretStore = new ObservingSecretStore(boundary);
            var connectionId = UlidId.New();
            var secretReference = (await secretStore.WriteAsync(
                connectionId,
                1,
                "fixture-provider-key".AsMemory())).Value;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IClock>(SystemClock.Instance);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IWriteCoordinator>(writeCoordinator);
            services.AddSingleton<IContentStore>(contentStore);
            services.AddSingleton<IPreprocessingService>(preprocessing);
            services.AddSingleton<IAiProviderClient>(provider);
            services.AddSingleton<IAiSecretStore>(secretStore);
            services.AddSingleton<IAiPromptBundleCatalog>(
                new ApprovedPromptBundleCatalog());
            services.AddSingleton(
                Options.Create(new TemplateExtractionJobWorkerOptions
                {
                    MaximumMediaBytes = maximumMediaBytes,
                }));
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<TemplateExtractionJobWorker>();
            var serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });
            try
            {
                await using var db = await serviceProvider
                    .GetRequiredService<
                        IDbContextFactory<OokiGraderDbContext>>()
                    .CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                return new ExtractionFixture(
                    connection,
                    serviceProvider,
                    connectionId,
                    secretReference,
                    writeCoordinator,
                    contentStore,
                    preprocessing,
                    secretStore,
                    provider);
            }
            catch
            {
                await serviceProvider.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync()
        {
            return _services
                .GetRequiredService<
                    IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();
        }

        public async Task<SeededExtraction> SeedAsync(
            string sourceRole,
            string templateTitle = "Template extraction fixture",
            string? templateSubject = null,
            IReadOnlyList<string>? replaceableMetadataFields = null,
            string sourceMimeType = "image/png",
            string sourceDisplayName = "模範解答.png",
            byte[]? sourceBytesOverride = null,
            string? additionalSourceRole = null,
            string additionalSourceDisplayName = "別紙模範解答.png")
        {
            var now = DateTimeOffset.UtcNow;
            var staffId = UlidId.New(now);
            var templateId = UlidId.New(now);
            var versionId = UlidId.New(now);
            var uploadId = UlidId.New(now);
            var sourceId = UlidId.New(now);
            var fileObjectId = UlidId.New(now);
            var fileReferenceId = UlidId.New(now);
            var additionalUploadId = UlidId.New(now.AddTicks(1));
            var additionalSourceId = UlidId.New(now.AddTicks(1));
            var additionalFileObjectId = UlidId.New(now.AddTicks(1));
            var additionalFileReferenceId = UlidId.New(now.AddTicks(1));
            var sourceBytes = sourceBytesOverride
                ?? Encoding.UTF8.GetBytes(
                    $"template-source-{sourceRole}");
            var sourceHash = Convert.ToHexString(
                    SHA256.HashData(sourceBytes))
                .ToLowerInvariant();
            ContentStore.Add(sourceHash, sourceBytes);
            byte[]? additionalSourceBytes = null;
            string? additionalSourceHash = null;
            if (additionalSourceRole is not null)
            {
                additionalSourceBytes = Encoding.UTF8.GetBytes(
                    $"template-source-additional-{additionalSourceRole}");
                additionalSourceHash = Convert.ToHexString(
                        SHA256.HashData(additionalSourceBytes))
                    .ToLowerInvariant();
                ContentStore.Add(additionalSourceHash, additionalSourceBytes);
            }
            var bundle = _services
                .GetRequiredService<IAiPromptBundleCatalog>()
                .GetRequired(AiTaskTypes.TemplateExtraction);

            await using var db = await CreateDbContextAsync();
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = templateTitle,
                Subject = templateSubject,
                State = "draft",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "generating",
                TargetTotalPointsMilli = 1_000,
                DefaultPointsMilli = 1_000,
                PipelineVersion =
                    TemplateExtractionJobWorker.PipelineVersion,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
            });
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = staffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = sourceDisplayName,
                DeclaredMimeType = sourceMimeType,
                ExpectedBytes = sourceBytes.Length,
                CurrentBytes = sourceBytes.Length,
                FinalSha256 = sourceHash,
                IncomingRelativePath = "fixture/source",
                State = "completed",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = sourceHash,
                Bytes = sourceBytes.Length,
                VerifiedMime = sourceMimeType,
                Extension = sourceMimeType == "image/tiff"
                    ? "tiff"
                    : "png",
                RelativeObjectPath =
                    $"template-source/{sourceHash}."
                    + (sourceMimeType == "image/tiff" ? "tiff" : "png"),
                StorageClass = ContentStorageClass.TemplateSource.ToString(),
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
                OwnerType = "upload_session",
                OwnerId = uploadId,
                Purpose = "template_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            db.TemplateSources.Add(new TemplateSourceEntity
            {
                Id = sourceId,
                TemplateVersionId = versionId,
                UploadSessionId = uploadId,
                FileReferenceId = fileReferenceId,
                SourceRole = sourceRole,
                DisplayName = sourceDisplayName,
                Ordinal = 0,
                UploadedByStaffUserId = staffId,
                CreatedAt = now,
            });
            if (additionalSourceRole is not null
                && additionalSourceBytes is not null
                && additionalSourceHash is not null)
            {
                db.UploadSessions.Add(new UploadSessionEntity
                {
                    Id = additionalUploadId,
                    CreatedByStaffUserId = staffId,
                    Purpose = "template_source",
                    DestinationType = "template_source",
                    OriginalFileName = additionalSourceDisplayName,
                    DeclaredMimeType = "image/png",
                    ExpectedBytes = additionalSourceBytes.Length,
                    CurrentBytes = additionalSourceBytes.Length,
                    FinalSha256 = additionalSourceHash,
                    IncomingRelativePath = "fixture/additional-source",
                    State = "completed",
                    ExpiresAt = now.AddHours(1),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.FileObjects.Add(new FileObjectEntity
                {
                    Id = additionalFileObjectId,
                    Sha256 = additionalSourceHash,
                    Bytes = additionalSourceBytes.Length,
                    VerifiedMime = "image/png",
                    Extension = "png",
                    RelativeObjectPath =
                        $"template-source/{additionalSourceHash}.png",
                    StorageClass =
                        ContentStorageClass.TemplateSource.ToString(),
                    RetentionClass = "template_source",
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 1,
                });
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = additionalFileReferenceId,
                    FileObjectId = additionalFileObjectId,
                    OwnerType = "upload_session",
                    OwnerId = additionalUploadId,
                    Purpose = "template_source",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
                db.TemplateSources.Add(new TemplateSourceEntity
                {
                    Id = additionalSourceId,
                    TemplateVersionId = versionId,
                    UploadSessionId = additionalUploadId,
                    FileReferenceId = additionalFileReferenceId,
                    SourceRole = additionalSourceRole,
                    DisplayName = additionalSourceDisplayName,
                    Ordinal = 1,
                    UploadedByStaffUserId = staffId,
                    CreatedAt = now,
                });
            }
            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = _connectionId,
                Provider = AiProviders.GeminiDirect,
                EndpointProfile = "googleGenerativeLanguage",
                ModelId = TemplateExtractionJobWorker.ModelId,
                SecretReference = _secretReference,
                KeyFingerprint = "sha256:fixture",
                CredentialRevision = 1,
                TimeoutSeconds = 30,
                ConcurrencyLimit = 1,
                State = "active",
                LastCapabilityProbeState = "passed",
                LastCapabilityProbeAt = now,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = UlidId.New(now),
                Name = "Template extraction pilot",
                TaskType = AiTaskTypes.TemplateExtraction,
                AiConnectionId = _connectionId,
                ConnectionRevision = 1,
                ModelId = TemplateExtractionJobWorker.ModelId,
                ProcessingStrategy = "queued_standard",
                PromptVersion = bundle.PromptVersion,
                SchemaVersion = bundle.SchemaVersion,
                PromptContentHash = bundle.ContentHash,
                ThinkingLevel = "medium",
                MediaResolution = "high",
                MaxOutputTokens = 4_096,
                ConcurrencyLimit = 1,
                ApprovalState = "pilot_approved",
                AccuracyEvaluationId = "fixture-evaluation",
                Active = true,
                ActivatedAt = now,
                ActivatedByStaffUserId = staffId,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
            });
            db.PricingSnapshots.Add(new PricingSnapshotEntity
            {
                Id = UlidId.New(now),
                Provider = AiProviders.GeminiDirect,
                ModelId = TemplateExtractionJobWorker.ModelId,
                InputUsdMicrosPerMillionTokens = 250_000,
                OutputUsdMicrosPerMillionTokens = 1_500_000,
                ThinkingUsdMicrosPerMillionTokens = 1_500_000,
                SourceUrl = "https://ai.google.dev/gemini-api/docs/pricing",
                EffectiveAt = now.AddDays(-1),
                CapturedAt = now,
            });
            var job = new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = TemplateExtractionJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey =
                    $"template-version:{versionId}:gemini-extract:r1",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    templateVersionId = versionId,
                    generationRevision = 1,
                    replaceableMetadataFields,
                }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now.AddMinutes(-1),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BackgroundJobs.Add(job);
            await db.SaveChangesAsync();
            return new SeededExtraction(
                templateId,
                versionId,
                sourceId,
                fileReferenceId,
                job.Id);
        }

        public async Task RequeueAsync(string jobId)
        {
            await using var db = await CreateDbContextAsync();
            var job = await db.BackgroundJobs.SingleAsync(
                item => item.Id == jobId);
            job.State = "queued";
            job.ProgressBasisPoints = 0;
            job.CompletedAt = null;
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            job.ErrorCode = null;
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
            SecretStore.Dispose();
        }
    }

    private sealed record SeededExtraction(
        string TemplateId,
        string VersionId,
        string SourceId,
        string FileReferenceId,
        string JobId);

    private sealed class BoundaryProbe
    {
        private readonly AsyncLocal<int> _depth = new();
        public bool IsInside => _depth.Value > 0;

        public IDisposable Enter()
        {
            _depth.Value++;
            return new Scope(this);
        }

        private sealed class Scope(BoundaryProbe owner) : IDisposable
        {
            public void Dispose()
            {
                owner._depth.Value--;
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
            await ExecuteAsync(
                async token =>
                {
                    await operation(token);
                    return true;
                },
                cancellationToken);
        }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var scope = boundary.Enter();
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

    private sealed class FakeContentStore(BoundaryProbe boundary)
        : IContentStore
    {
        private readonly Dictionary<string, byte[]> _content =
            new(StringComparer.Ordinal);
        public bool ObservedInsideWriteCoordinator { get; private set; }

        public void Add(string sha256, byte[] content)
        {
            _content.Add(sha256, content);
        }

        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedInsideWriteCoordinator |= boundary.IsInside;
            return Task.FromResult<Stream>(
                new MemoryStream(
                    _content[locator.Sha256],
                    writable: false));
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_content.ContainsKey(locator.Sha256));
        }

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private interface IObservedPreprocessingService : IPreprocessingService
    {
        bool ObservedInsideWriteCoordinator { get; }
    }

    private sealed class FakePreprocessingService(
        BoundaryProbe boundary,
        int pageCount)
        : IObservedPreprocessingService
    {
        public static readonly byte[] NormalizedPngBytes =
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAIAQAAAADsdIMmAAAAIGNIUk0AAHom"
                + "AACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAACYktHRAAB3YoT"
                + "pAAAAAd0SU1FB+oIBQ43F2XvIU4AAAAMSURBVAjXY/jPgAIBP9AH+YjzhVIA"
                + "AAAldEVYdGRhdGU6Y3JlYXRlADIwMjYtMDgtMDVUMTQ6NTU6MjMrMDA6MDC"
                + "0kA3nAAAAJXRFWHRkYXRlOm1vZGlmeQAyMDI2LTA4LTA1VDE0OjU1OjIzKz"
                + "AwOjAwxc21WwAAACh0RVh0ZGF0ZTp0aW1lc3RhbXAAMjAyNi0wOC0wNVQxN"
                + "Do1NToyMyswMDowMJLYlIQAAAAASUVORK5CYII=");

        public static readonly string NormalizedPngSha256 =
            Convert.ToHexString(SHA256.HashData(NormalizedPngBytes))
                .ToLowerInvariant();

        public bool ObservedInsideWriteCoordinator { get; private set; }

        public Task<PreprocessingResult> ProcessAsync(
            Stream source,
            PreprocessingInput input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedInsideWriteCoordinator |= boundary.IsInside;
            var artifact = new ImageArtifact(
                "image/png",
                "png",
                8,
                8,
                NormalizedPngBytes,
                NormalizedPngSha256);
            var pages = Enumerable
                .Range(1, pageCount)
                .Select(pageNumber => new PreprocessedPage(
                    pageNumber,
                    8,
                    8,
                    300,
                    300,
                    artifact,
                    artifact,
                    new PageQualityMetrics(
                        1,
                        1,
                        1,
                        0,
                        1,
                        0,
                        0,
                        false,
                        []),
                    new PageFingerprint(
                        NormalizedPngSha256,
                        new string('0', 16))))
                .ToArray();
            return Task.FromResult(
                new PreprocessingResult(
                    "fixture-v1",
                    new string('a', 64),
                    input.VerifiedMimeType,
                    pages,
                    [],
                    new string('b', 64)));
        }

        public ImageArtifact Crop(
            PreprocessedPage page,
            MillionthsRegion region,
            int marginMillionths = 0)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ObservingPreprocessingService(
        BoundaryProbe boundary,
        IPreprocessingService inner)
        : IObservedPreprocessingService
    {
        public bool ObservedInsideWriteCoordinator { get; private set; }

        public Task<PreprocessingResult> ProcessAsync(
            Stream source,
            PreprocessingInput input,
            CancellationToken cancellationToken = default)
        {
            ObservedInsideWriteCoordinator |= boundary.IsInside;
            return inner.ProcessAsync(source, input, cancellationToken);
        }

        public ImageArtifact Crop(
            PreprocessedPage page,
            MillionthsRegion region,
            int marginMillionths = 0)
        {
            return inner.Crop(page, region, marginMillionths);
        }
    }

    private sealed class ObservingSecretStore(
        BoundaryProbe boundary) : IAiSecretStore, IDisposable
    {
        private readonly InMemoryAiSecretStore _inner = new();
        public bool ObservedInsideWriteCoordinator { get; private set; }

        public Task<AiSecretReference> WriteAsync(
            string ownerId,
            long credentialRevision,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            return _inner.WriteAsync(
                ownerId,
                credentialRevision,
                secret,
                cancellationToken);
        }

        public Task<AiSecretLease> ReadAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default)
        {
            ObservedInsideWriteCoordinator |= boundary.IsInside;
            return _inner.ReadAsync(reference, cancellationToken);
        }

        public Task<bool> DeleteAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default)
        {
            return _inner.DeleteAsync(reference, cancellationToken);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }

    private sealed class FakeAiProvider(
        BoundaryProbe boundary,
        Func<AiProviderRequest, AiProviderResponse> responseFactory)
        : IAiProviderClient
    {
        public string Provider => AiProviders.GeminiDirect;
        public List<AiProviderRequest> Requests { get; } = [];
        public List<ReadOnlyMemory<byte>> BorrowedQualityControlDetailViews
        {
            get;
        } = [];
        public bool ObservedInsideWriteCoordinator { get; private set; }

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedInsideWriteCoordinator |= boundary.IsInside;
            if (request.Media.Count == 5)
            {
                BorrowedQualityControlDetailViews.AddRange(
                    request.Media.Skip(1).Select(media => media.Bytes));
            }

            Requests.Add(request with
            {
                Media = request.Media.Select(media => media with
                {
                    Bytes = media.Bytes.ToArray(),
                }).ToArray(),
            });
            return Task.FromResult(responseFactory(request));
        }

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

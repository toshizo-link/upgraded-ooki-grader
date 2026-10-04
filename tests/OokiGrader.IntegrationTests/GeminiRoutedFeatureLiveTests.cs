using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Security;
using OokiGrader.Host.Jobs;
using OokiGrader.Application.Grading;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;
using DomainQuestionDefinition = OokiGrader.Domain.Templates.QuestionDefinition;

namespace OokiGrader.IntegrationTests;

/// <summary>
/// Explicit opt-in checks against Gemini 3.8 Flash using the synthetic Japanese
/// Hanako completed-test fixture. No real student data or AI response bodies are
/// written to disk. OOKI_GEMINI_FEATURE_IMAGE points to its rendered PNG.
/// </summary>
public sealed class GeminiRoutedFeatureLiveTests
{
    private const string ModelId = "gemini-3.8-flash";
    private const string ExpectedName = "桜井花子";
    private const string ExpectedStudentNumber = "S-001";
    private static readonly FeatureQuestion[] Questions =
    [
        new("release-q1", "1", "日本の首都を漢字で書きなさい。", "東京", 8_000),
        new("release-q2", "2", "ASEAN（アセアン）を日本語で何というか。", "東南アジア諸国連合", 10_000),
        new("release-q3", "3", "インドで最も多くの人が信仰している宗教を書きなさい。", "ヒンドゥー教", 8_000),
    ];

    [LiveFeatureFact]
    public async Task NameTranscriptionReadsVisibleJapaneseNameAndStudentNumber()
    {
        const string requestKey = "release-3-8-name-1";
        using var catalog = new ApprovedPromptBundleCatalog();
        var bundle = catalog.GetRequired(AiTaskTypes.NameTranscription);
        var instruction =
            """
            The attached media are the identity-bearing page selected from one
            completed Japanese test. Find the printed fields used for the
            student's name and student number and transcribe only characters
            visibly written in those fields. Preserve Japanese script exactly;
            do not correct a spelling to a common name or consult a roster.
            Ignore all answers while performing this identity task. Use null for
            a field that is not visible. Return blank or unreadable instead of guessing.

            """
            + JsonSerializer.Serialize(new
            {
                schema_version = bundle.SchemaVersion,
                request_key = requestKey,
                media = new[] { new { media_index = 0, artifact_type = "PAGE_1", ordinal = 0, panel_label = "PAGE_1" } },
            });
        var response = await GenerateAsync(bundle, requestKey, instruction);
        var root = response.StructuredOutput;
        Assert.Equal(bundle.SchemaVersion, root.GetProperty("schema_version").GetString());
        Assert.Equal(requestKey, root.GetProperty("request_key").GetString());
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.Equal(ExpectedName, Compact(root.GetProperty("transcribed_name").GetString()!));
        Assert.Equal(ExpectedStudentNumber, root.GetProperty("transcribed_student_number").GetString());
        Assert.Equal("clear", root.GetProperty("legibility").GetString());
        Assert.InRange(root.GetProperty("confidence").GetDouble(), 0.8, 1);
        Assert.False(root.GetProperty("unexpected_content").GetBoolean());
    }

    [LiveFeatureFact]
    public async Task InitialGradingProducesCorrectThreeItemScoresAndIdentity()
    {
        const string requestKey = "release-3-8-grading-1";
        using var catalog = new ApprovedPromptBundleCatalog();
        var bundle = catalog.GetRequired(AiTaskTypes.InitialGrading);
        var response = await GenerateAsync(
            bundle,
            requestKey,
            GradingInstruction(bundle, requestKey, Questions, identityRequired: true));
        var definitions = Questions.Select(ToDefinition).ToDictionary(
            question => question.Id, StringComparer.Ordinal);
        var validated = AiGradingResponseValidator.Validate(
            response.StructuredOutput, requestKey, definitions, mediaPartCount: 1);
        AssertScores(response.StructuredOutput, validated, Questions);
        var identity = AiGradingResponseValidator.ValidateIdentityComponent(
            response.StructuredOutput, requestKey, identityExpected: true);
        Assert.True(identity.IsApplicable);
        Assert.True(identity.IsValid, identity.ErrorCode);
        Assert.NotNull(identity.Transcription);
        Assert.Equal(ExpectedName, Compact(identity.Transcription.VisibleName!));
        Assert.Equal(ExpectedStudentNumber, identity.Transcription.VisibleStudentNumber);
        Assert.Equal("clear", identity.Transcription.Legibility);
        Assert.False(identity.Transcription.UnexpectedContent);
    }

    [LiveFeatureFact]
    public async Task IndependentAdjudicationReturnsCorrectAnswerAndFullScore()
    {
        const string requestKey = "release-3-8-adjudication-1";
        using var catalog = new ApprovedPromptBundleCatalog();
        var bundle = catalog.GetRequired(AiTaskTypes.Adjudication);
        FeatureQuestion[] requested = [Questions[1]];
        var response = await GenerateAsync(
            bundle,
            requestKey,
            GradingInstruction(bundle, requestKey, requested, identityRequired: false));
        var definitions = requested.Select(ToDefinition).ToDictionary(
            question => question.Id, StringComparer.Ordinal);
        var validated = AiGradingResponseValidator.Validate(
            response.StructuredOutput, requestKey, definitions, mediaPartCount: 1);
        AssertScores(response.StructuredOutput, validated, requested);
        Assert.False(response.StructuredOutput.TryGetProperty("identity", out _));
    }

    private static string GradingInstruction(
        AiPromptBundle bundle,
        string requestKey,
        FeatureQuestion[] requested,
        bool identityRequired) =>
        """
        The attached image is a complete page from a completed Japanese test.
        Locate only the requested answers by their printed question labels and
        question text. Inspect the original page pixels and transcribe each
        visible answer exactly, preserving Japanese script. Grade only against
        the supplied teacher-approved accepted answers and grading options.
        Every accepted_answers entry is an independently complete valid answer.
        Ignore layout whitespace, but never omit or merge answer components.
        Include every requested question exactly once in results or once in
        missing_question_ids if its answer cannot be located. Return no other
        questions. Recommend review for ambiguous, incomplete, unexpected or
        unreadable evidence. When identity_required=true, transcribe only the
        visibly written name and number from the printed identity field on
        PAGE_1 into identity. Otherwise do not infer or return identity.
        This assessment is independent: the earlier grader's judgment is
        intentionally omitted. Do not assume another grader was correct.

        """
        + JsonSerializer.Serialize(new
        {
            schema_version = bundle.SchemaVersion,
            request_key = requestKey,
            chunk_index = 0,
            chunk_count = 1,
            identity_required = identityRequired,
            media = new[] { new { media_index = 0, page_number = 1, page_label = "PAGE_1" } },
            questions = requested.Select(question => new
            {
                question_id = question.Id,
                display_label = question.Label,
                question_text = question.Text,
                question_type = "exact_short_text",
                grading_mode = "transcribe_then_rules",
                maximum_points_milli = question.MaximumPointsMilli,
                point_increment_milli = 1_000,
                allow_non_kanji = false,
                requires_complete_answer = true,
                answer_order_insensitive = false,
                rubric_text = (string?)null,
                accepted_answers = new[] { question.Answer },
            }),
        });

    private static DomainQuestionDefinition ToDefinition(FeatureQuestion question) =>
        new(
            question.Id, "logical-" + question.Id, 0, question.Label, question.Text,
            QuestionType.ExactShortText, GradingMode.TranscribeThenRules,
            new MilliPoints(question.MaximumPointsMilli), new MilliPoints(1_000),
            allowNonKanji: false, requiresReviewAlways: false, teacherVerified: true,
            acceptedAnswers:
            [
                new AcceptedAnswer(question.Id + "-answer", question.Answer,
                    AcceptedAnswerVariantType.Canonical, AnswerProvenance.TeacherEntered,
                    teacherVerified: true),
            ],
            requiresCompleteAnswer: true);

    private static void AssertScores(
        JsonElement raw,
        ValidatedAiGradingResponse validated,
        FeatureQuestion[] requested)
    {
        Assert.Equal(requested.Length, validated.Observations.Count);
        Assert.False(validated.UnexpectedContent);
        Assert.Empty(raw.GetProperty("missing_question_ids").EnumerateArray());
        foreach (var question in requested)
        {
            var observation = Assert.Single(validated.Observations,
                item => item.QuestionId == question.Id);
            Assert.Equal(question.Answer, Compact(observation.Observation.Transcription));
            Assert.Equal(question.MaximumPointsMilli, observation.ProposedPointsMilli);
            Assert.Equal("correct", observation.ProposedOutcome);
            Assert.False(observation.Observation.ExplicitlyBlank);
            var original = Assert.Single(raw.GetProperty("results").EnumerateArray(),
                item => item.GetProperty("question_id").GetString() == question.Id);
            // Check the provider's original proposal as well as the host's
            // local reconciliation, so a mistaken AI score cannot be hidden.
            Assert.Equal("correct", original.GetProperty("proposed_outcome").GetString());
            Assert.Equal(question.MaximumPointsMilli,
                original.GetProperty("proposed_points_milli").GetInt64());
        }
    }

    private static async Task<AiProviderResponse> GenerateAsync(
        AiPromptBundle bundle,
        string requestKey,
        string instruction)
    {
        
        var imagePath = Environment.GetEnvironmentVariable("OOKI_GEMINI_FEATURE_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new InvalidOperationException("The opt-in live feature test inputs are required.");
        }

        var secretStore = new WindowsDpapiAiSecretStore(new WindowsDpapiAiSecretStoreOptions
        { RootPath = Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_ROOT")! });
        using var secret = await secretStore.ReadAsync(new AiSecretReference(
            Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_REFERENCE")!));
        var credential = secret.Utf8Bytes.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "ooki-live-features-" + Guid.NewGuid().ToString("N"));
        try
        {
            var media = await File.ReadAllBytesAsync(imagePath);
            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var client = new GeminiTaskRoutingClient(new GeminiDirectClient(httpClient),
                new GeminiQuotaCooldownStore(directory, TimeProvider.System));
            var connection = new AiConnectionSettings(
                "release-3-8-features", AiProviders.GeminiDirect,
                AiProviderCatalog.GeminiBaseAddress, ModelId, TimeSpan.FromSeconds(300));
            var request = new AiProviderRequest(
                requestKey, bundle.TaskType, bundle.PromptVersion, bundle.SchemaVersion,
                bundle.SystemInstruction, instruction, bundle.ResponseJsonSchema,
                [new AiMediaPart("image/png", media,
                    Convert.ToHexString(SHA256.HashData(media)).ToLowerInvariant())],
                MaxOutputTokens: 8_192, MediaResolution: "MEDIA_RESOLUTION_HIGH",
                ThinkingLevel: "LOW");
            var response = await client.GenerateAsync(connection, credential, request);
            Assert.Equal(AiProviders.GeminiDirect, response.Provider);
            Assert.Equal(ModelId, response.RequestedModel);
            Assert.True(AiResponseMetadataValidator.IsAccepted(response));
            if (bundle.TaskType == AiTaskTypes.NameTranscription)
                Assert.Equal(AiProviderCatalog.GeminiLightModelId, response.ActualModel);
            Assert.Equal("STOP", response.FinishReason);
            Assert.True(response.Usage.TotalTokens > 0);
            return response;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static string Compact(string text) =>
        string.Concat(text.Normalize(NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character)));

    private sealed record FeatureQuestion(
        string Id, string Label, string Text, string Answer, long MaximumPointsMilli);

    private sealed class LiveFeatureFactAttribute : FactAttribute
    {
        public LiveFeatureFactAttribute()
        {
            if ((string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_ROOT"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_REFERENCE")))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_GEMINI_FEATURE_IMAGE")))
            {
                Skip = "Requires protected credential reference and feature image for an explicit live run.";
            }
        }
    }
}

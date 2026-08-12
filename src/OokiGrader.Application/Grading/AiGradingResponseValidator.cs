using System.Text;
using System.Text.Json;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;
using DomainAcceptedAnswerVariantType = OokiGrader.Domain.Templates.AcceptedAnswerVariantType;
using DomainQuestionDefinition = OokiGrader.Domain.Templates.QuestionDefinition;
using DomainGradingMode = OokiGrader.Domain.Templates.GradingMode;

namespace OokiGrader.Application.Grading;

public sealed record ValidatedAiQuestionObservation(
    string QuestionId,
    AnswerObservation Observation,
    long ProposedPointsMilli,
    string ProposedOutcome,
    int ProviderConfidenceBasisPoints,
    bool ProviderReviewRecommended,
    string? ProviderReasonCode,
    string? BoundedExplanation,
    string CanonicalItemHash,
    int? EvidenceMediaIndex = null);

public sealed record ValidatedAiIdentityTranscription(
    string? VisibleName,
    string? VisibleStudentNumber,
    string Legibility,
    int ProviderConfidenceBasisPoints,
    bool UnexpectedContent);

public sealed record AiIdentityComponentValidation(
    bool IsApplicable,
    bool IsValid,
    ValidatedAiIdentityTranscription? Transcription,
    string? ErrorCode);

public sealed record ValidatedAiGradingResponse(
    string RequestKey,
    IReadOnlyList<ValidatedAiQuestionObservation> Observations,
    bool UnexpectedContent,
    ValidatedAiIdentityTranscription? Identity = null,
    string? IdentityValidationError = null);

public static class AiGradingResponseValidator
{
    private const int MaximumResponseItems = 300;
    private const int MaximumRequestKeyLength = 200;
    private const int MaximumQuestionIdLength = 200;
    private const int MaximumTranscriptionLength = 20_000;
    private const double MinimumAutomaticConfidence = 0.80;
    private static readonly HashSet<string> AllowedOutcomes =
    [
        "correct",
        "incorrect",
        "partial",
        "blank",
        "unreadable",
        "review",
    ];
    public static ValidatedAiGradingResponse Validate(
        JsonElement response,
        string expectedRequestKey,
        IReadOnlyDictionary<string, DomainQuestionDefinition> questions,
        int? mediaPartCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRequestKey);
        ArgumentNullException.ThrowIfNull(questions);
        var schemaVersion = response.ValueKind is JsonValueKind.Object
            ? RequiredString(response, "schema_version")
            : string.Empty;
        if (response.ValueKind is not JsonValueKind.Object
            || schemaVersion is not (
                "answer_transcribe_grade_v1" or "submission_analysis_v2")
            || expectedRequestKey.Length > MaximumRequestKeyLength
            || RequiredBoundedString(
                    response,
                    "request_key",
                    MaximumRequestKeyLength)
                != expectedRequestKey)
        {
            throw Invalid("ai_response_identity_invalid");
        }

        if (!response.TryGetProperty("results", out var results)
            || results.ValueKind is not JsonValueKind.Array
            || results.GetArrayLength() > MaximumResponseItems)
        {
            throw Invalid("ai_response_results_invalid");
        }

        var observations = new List<ValidatedAiQuestionObservation>(
            results.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind is not JsonValueKind.Object)
            {
                throw Invalid("ai_response_field_invalid");
            }

            var questionId = RequiredBoundedString(
                result,
                "question_id",
                MaximumQuestionIdLength);
            if (!seen.Add(questionId)
                || !questions.TryGetValue(questionId, out var question))
            {
                throw Invalid("ai_response_question_coverage_invalid");
            }

            int? evidenceMediaIndex = null;
            if (schemaVersion == "submission_analysis_v2")
            {
                var parsedIndex = RequiredInt32(
                    result,
                    "evidence_media_index");
                if (parsedIndex < 0
                    || (mediaPartCount is not null
                        && parsedIndex >= mediaPartCount.Value))
                {
                    throw Invalid("ai_response_evidence_media_invalid");
                }

                evidenceMediaIndex = parsedIndex;
            }

            var transcription = RequiredBoundedString(
                result,
                "transcription",
                MaximumTranscriptionLength,
                allowEmpty: true);
            var legibility = RequiredString(result, "legibility");
            var blank = RequiredBoolean(result, "blank");
            var outcome = RequiredString(result, "proposed_outcome");
            var proposedPoints = RequiredInt64(result, "proposed_points_milli");
            var confidence = RequiredDouble(result, "confidence");
            var reviewRecommended = question.RequiresReviewAlways
                || confidence < MinimumAutomaticConfidence
                || legibility != "clear"
                || outcome is "partial" or "review" or "unreadable";
            var pointAwardValid = proposedPoints >= 0
                && proposedPoints <= question.MaximumPoints.Value
                && question.PointPolicy.ValidateAward(
                        new MilliPoints(proposedPoints))
                    .IsValid
                && (outcome switch
                {
                    "correct" => proposedPoints == question.MaximumPoints.Value,
                    "partial" => proposedPoints > 0
                        && proposedPoints < question.MaximumPoints.Value,
                    "incorrect" or "blank" or "unreadable" or "review" =>
                        proposedPoints == 0,
                    _ => false,
                });
            if (confidence is < 0 or > 1
                || !AllowedOutcomes.Contains(outcome)
                || (blank && transcription.Length > 0)
                || (blank && outcome != "blank")
                || (!blank && outcome == "blank")
                || (legibility is "unreadable" or "cropped" && outcome != "unreadable"))
            {
                throw Invalid("ai_response_semantics_invalid");
            }

            var quality = legibility switch
            {
                "clear" => AnswerQuality.Clear,
                "ambiguous" => AnswerQuality.Ambiguous,
                "unreadable" => AnswerQuality.Unreadable,
                "cropped" => AnswerQuality.Cropped,
                _ => throw Invalid("ai_response_legibility_invalid"),
            };
            var observation = new AnswerObservation(
                transcription,
                quality,
                blank,
                scriptObservationUncertain: false);
            var reconciled = ReconcileProposal(
                question,
                observation,
                pointAwardValid,
                proposedPoints,
                outcome,
                reviewRecommended);
            var canonicalItem = JsonSerializer.SerializeToUtf8Bytes(result);
            observations.Add(new ValidatedAiQuestionObservation(
                questionId,
                observation,
                reconciled.ProposedPointsMilli,
                reconciled.ProposedOutcome,
                checked((int)Math.Round(
                    confidence * 10_000,
                    MidpointRounding.AwayFromZero)),
                reconciled.ReviewRecommended,
                reconciled.ReasonCode,
                null,
                Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(canonicalItem))
                    .ToLowerInvariant(),
                evidenceMediaIndex));
        }

        if (!response.TryGetProperty("missing_question_ids", out var missing)
            || missing.ValueKind is not JsonValueKind.Array)
        {
            throw Invalid("ai_response_missing_ids_invalid");
        }

        var reportedMissingValues = missing.EnumerateArray()
            .Select(item => item.ValueKind is JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : throw Invalid("ai_response_missing_ids_invalid"))
            .ToArray();
        var reportedMissing = reportedMissingValues
            .ToHashSet(StringComparer.Ordinal);
        var actualMissing = questions.Keys
            .Where(questionId => !seen.Contains(questionId))
            .ToHashSet(StringComparer.Ordinal);
        if (reportedMissingValues.Length > MaximumResponseItems
            || reportedMissing.Count != reportedMissingValues.Length
            || reportedMissing.Any(questionId =>
                questionId.Length is 0 or > MaximumQuestionIdLength)
            || !reportedMissing.SetEquals(actualMissing)
            || reportedMissing.Any(questionId => !questions.ContainsKey(questionId)))
        {
            throw Invalid("ai_response_question_coverage_invalid");
        }

        return new ValidatedAiGradingResponse(
            expectedRequestKey,
            observations,
            RequiredBoolean(response, "unexpected_content"));
    }

    public static AiIdentityComponentValidation ValidateIdentityComponent(
        JsonElement response,
        string expectedRequestKey,
        bool identityExpected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRequestKey);
        try
        {
            if (response.ValueKind is not JsonValueKind.Object)
            {
                return InvalidIdentity(
                    identityExpected,
                    "ai_identity_response_invalid");
            }

            var schemaVersion = RequiredString(response, "schema_version");
            if (schemaVersion == "answer_transcribe_grade_v1")
            {
                return new AiIdentityComponentValidation(
                    IsApplicable: false,
                    IsValid: true,
                    Transcription: null,
                    ErrorCode: null);
            }

            if (schemaVersion != "submission_analysis_v2"
                || RequiredBoundedString(
                        response,
                        "request_key",
                        MaximumRequestKeyLength)
                    != expectedRequestKey
                || !response.TryGetProperty("identity", out var identity))
            {
                return InvalidIdentity(
                    identityExpected,
                    "ai_identity_response_invalid");
            }

            if (!identityExpected)
            {
                return identity.ValueKind is JsonValueKind.Null
                    ? new AiIdentityComponentValidation(
                        IsApplicable: true,
                        IsValid: true,
                        Transcription: null,
                        ErrorCode: null)
                    : InvalidIdentity(
                        identityExpected: false,
                        "ai_identity_unexpected_chunk");
            }

            if (identity.ValueKind is not JsonValueKind.Object)
            {
                return InvalidIdentity(
                    identityExpected: true,
                    "ai_identity_missing_first_chunk");
            }

            var name = NullableBoundedString(
                identity,
                "transcribed_name",
                400);
            var studentNumber = NullableBoundedString(
                identity,
                "transcribed_student_number",
                200);
            var legibility = RequiredString(identity, "legibility");
            var confidence = RequiredDouble(identity, "confidence");
            var unexpected = RequiredBoolean(identity, "unexpected_content");
            if (legibility is not (
                    "clear" or "ambiguous" or "unreadable" or "blank" or
                    "cropped")
                || confidence is < 0 or > 1
                || (legibility is "blank" or "unreadable"
                    && (name is not null || studentNumber is not null))
                || (legibility == "clear"
                    && name is null
                    && studentNumber is null))
            {
                return InvalidIdentity(
                    identityExpected: true,
                    "ai_identity_semantics_invalid");
            }

            return new AiIdentityComponentValidation(
                IsApplicable: true,
                IsValid: true,
                new ValidatedAiIdentityTranscription(
                    name,
                    studentNumber,
                    legibility,
                    Math.Clamp(
                        (int)Math.Round(
                            confidence * 10_000,
                            MidpointRounding.AwayFromZero),
                        0,
                        10_000),
                    unexpected),
                ErrorCode: null);
        }
        catch (InvalidDataException)
        {
            return InvalidIdentity(
                identityExpected,
                "ai_identity_response_invalid");
        }
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not JsonValueKind.String
            || property.GetString() is not { } result)
        {
            throw Invalid("ai_response_field_invalid");
        }

        return result;
    }

    private static string RequiredBoundedString(
        JsonElement value,
        string name,
        int maximumLength,
        bool allowEmpty = false)
    {
        var result = RequiredString(value, name);
        if (result.Length > maximumLength || (!allowEmpty && result.Length == 0))
        {
            throw Invalid("ai_response_field_invalid");
        }

        return result;
    }

    private static bool RequiredBoolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("ai_response_field_invalid");
        }

        return property.GetBoolean();
    }

    private static long RequiredInt64(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || !property.TryGetInt64(out var result))
        {
            throw Invalid("ai_response_field_invalid");
        }

        return result;
    }

    private static int RequiredInt32(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || !property.TryGetInt32(out var result))
        {
            throw Invalid("ai_response_field_invalid");
        }

        return result;
    }

    private static string? NullableBoundedString(
        JsonElement value,
        string name,
        int maximumLength)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            throw Invalid("ai_response_field_invalid");
        }

        if (property.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind is not JsonValueKind.String
            || property.GetString() is not { } result
            || result.Length > maximumLength)
        {
            throw Invalid("ai_response_field_invalid");
        }

        result = result.Trim();
        return result.Length == 0 ? null : result;
    }

    private static double RequiredDouble(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || !property.TryGetDouble(out var result)
            || !double.IsFinite(result))
        {
            throw Invalid("ai_response_field_invalid");
        }

        return result;
    }

    private static ReconciledProposal ReconcileProposal(
        DomainQuestionDefinition question,
        AnswerObservation observation,
        bool pointAwardValid,
        long proposedPoints,
        string proposedOutcome,
        bool reviewRecommended)
    {
        if (question.GradingMode is DomainGradingMode.Manual)
        {
            return new ReconciledProposal(
                0,
                "review",
                true,
                "ai_manual_question");
        }

        if (question.GradingMode is DomainGradingMode.AiRubric
            && observation.Quality is AnswerQuality.Clear
            && !observation.ExplicitlyBlank
            && proposedOutcome == "incorrect"
            && MatchesAcceptedAnswerIgnoringLayoutLineBreaks(
                question,
                observation.Transcription))
        {
            return new ReconciledProposal(
                question.MaximumPoints.Value,
                "correct",
                reviewRecommended,
                "ai_layout_line_wrap_reconciled");
        }

        var useLocalRules = question.GradingMode is
                DomainGradingMode.Deterministic
                or DomainGradingMode.TranscribeThenRules
            || observation.ExplicitlyBlank
            || observation.Quality is not AnswerQuality.Clear;
        if (!useLocalRules)
        {
            if (question.RequiresCompleteAnswer
                && pointAwardValid
                && proposedPoints > 0
                && proposedPoints < question.MaximumPoints.Value)
            {
                return new ReconciledProposal(
                    0,
                    "incorrect",
                    true,
                    "ai_complete_answer_required");
            }

            return pointAwardValid
                ? new ReconciledProposal(
                    proposedPoints,
                    proposedOutcome,
                    reviewRecommended,
                    null)
                : new ReconciledProposal(
                    0,
                    "review",
                    true,
                    "ai_invalid_point_award");
        }

        var deterministic = DeterministicGrader.Grade(question, observation);
        if (deterministic.Disposition is GradeDisposition.ReviewRequired)
        {
            return new ReconciledProposal(
                0,
                observation.Quality is AnswerQuality.Unreadable or AnswerQuality.Cropped
                    ? "unreadable"
                    : "review",
                true,
                "ai_deterministic_review_required");
        }

        var expectedOutcome = deterministic.Disposition switch
        {
            GradeDisposition.Correct => "correct",
            GradeDisposition.Incorrect => "incorrect",
            GradeDisposition.Partial => "partial",
            GradeDisposition.Blank => "blank",
            _ => throw Invalid("ai_response_semantics_invalid"),
        };
        var providerContradictedLocalRules = !pointAwardValid
            || proposedPoints != deterministic.AwardedPoints.Value
            || proposedOutcome != expectedOutcome;
        return new ReconciledProposal(
            deterministic.AwardedPoints.Value,
            expectedOutcome,
            reviewRecommended
                || deterministic.RequiresReview
                || providerContradictedLocalRules,
            providerContradictedLocalRules
                ? "ai_deterministic_recomputed"
                : null);
    }

    private static bool MatchesAcceptedAnswerIgnoringLayoutLineBreaks(
        DomainQuestionDefinition question,
        string transcription)
    {
        if (question.AnswerOrderInsensitive)
        {
            return false;
        }

        var normalizedTranscription = NormalizeLayoutLineBreaks(transcription);
        if (normalizedTranscription.Length == 0)
        {
            return false;
        }

        var canonicalRequiresKanji = !question.AllowNonKanji
            && question.CanonicalAnswer is { } canonical
            && KanjiDetector.ContainsKanji(canonical.AnswerText);
        var transcriptionHasKanji = KanjiDetector.ContainsKanji(transcription);

        return question.AcceptedAnswers.Any(answer =>
            answer.VariantType is DomainAcceptedAnswerVariantType.Canonical
                or DomainAcceptedAnswerVariantType.Equivalent
                or DomainAcceptedAnswerVariantType.PhoneticException
            && (!canonicalRequiresKanji
                || transcriptionHasKanji
                || answer.VariantType
                    == DomainAcceptedAnswerVariantType.PhoneticException)
            && string.Equals(
                NormalizeLayoutLineBreaks(answer.AnswerText),
                normalizedTranscription,
                StringComparison.Ordinal));
    }

    private static string NormalizeLayoutLineBreaks(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var trimLineIndent = false;
        foreach (var character in normalized)
        {
            if (character is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')
            {
                while (builder.Length > 0
                       && builder[^1] is ' ' or '\t' or '\u00A0')
                {
                    builder.Length--;
                }

                trimLineIndent = true;
                continue;
            }

            if (trimLineIndent && character is ' ' or '\t' or '\u00A0')
            {
                continue;
            }

            trimLineIndent = false;
            builder.Append(character);
        }

        return JapaneseTextNormalizer.NormalizeForComparison(builder.ToString());
    }

    private sealed record ReconciledProposal(
        long ProposedPointsMilli,
        string ProposedOutcome,
        bool ReviewRecommended,
        string? ReasonCode);

    private static AiIdentityComponentValidation InvalidIdentity(
        bool identityExpected,
        string errorCode) =>
        new(
            IsApplicable: true,
            IsValid: false,
            identityExpected
                ? new ValidatedAiIdentityTranscription(
                    VisibleName: null,
                    VisibleStudentNumber: null,
                    Legibility: "unreadable",
                    ProviderConfidenceBasisPoints: 0,
                    UnexpectedContent: true)
                : null,
            errorCode);

    private static InvalidDataException Invalid(string code) => new(code);
}

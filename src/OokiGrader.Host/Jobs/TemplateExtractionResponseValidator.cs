using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OokiGrader.Host.Jobs;

internal static class TemplateExtractionResponseValidator
{
    private const int MaximumQuestions = 300;
    private const int MaximumPages = 200;
    private const int MaximumWarnings = 100;
    private const long MaximumQuestionPointsMilli = 100_000_000;
    private const string CanonicalBlankToken = "［　］";

    // A narrowly bounded family seen when OCR reads 「はね返される」.  Requiring
    // the complete malformed prefix and inflected suffix avoids rewriting
    // unrelated occurrences of short fragments such as 「はい」 or 「返される」.
    private static readonly Regex JapaneseBounceOcrNoisePattern = new(
        @"は(?:い|じ)(?:か)?(?:は?ね)?返(?:され)?る",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly HashSet<string> QuestionTypes =
    [
        "multiple_choice",
        "boolean",
        "numeric",
        "exact_short_text",
        "semantic_short_text",
        "multi_part",
        "subjective",
        "unsupported",
    ];

    private static readonly HashSet<string> AnswerProvenances =
    [
        "provided_model_answer",
        "ai_proposed",
        "unavailable",
    ];

    private static readonly HashSet<string> SlotStructureIssueCodes =
    [
        "template.answer_slot_inventory_mismatch",
        "template.duplicate_source_key",
        "question.duplicate_answer_slot_ordinal",
        "question.answer_slot_inventory_mismatch",
        "question.answer_slots_not_separated",
        "question.additional_placeholders_redacted",
        "question.fill_blank_placeholder_invalid",
    ];

    /// <summary>
    /// Decides whether a second, image-grounded inventory pass is warranted.
    /// A page containing multiple embedded blanks is always audited because a
    /// self-consistent omission (for example, nine objects and a declared count
    /// of nine) cannot be detected from one model response alone.
    /// </summary>
    internal static bool RequiresIndependentSlotAudit(
        ValidatedTemplateExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return extraction.Pages.Any(page =>
                page.DetectedAnswerSlotCount != page.Questions.Count
                || (page.Questions.Count > 1
                    && page.Questions.Any(question =>
                        question.IsEmbeddedFillBlank)))
            || HasRepairableSlotStructureIssue(extraction)
            || HasAnswerAuthorityIssue(extraction);
    }

    /// <summary>
    /// Detects answers that could not be tied to the declared authoritative
    /// source. These cases need another image-grounded pass; changing only the
    /// provenance label would turn a potentially inferred or hallucinated answer
    /// into false source evidence.
    /// </summary>
    internal static bool HasAnswerAuthorityIssue(
        ValidatedTemplateExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return extraction.ReviewIssues.Any(IsAnswerAuthorityIssue)
            || extraction.Pages.Any(page => page.Questions.Any(question =>
                question.ReviewIssues.Any(IsAnswerAuthorityIssue)));

        static bool IsAnswerAuthorityIssue(
            TemplateExtractionReviewIssue issue) =>
            issue.Code is
                "answer.source_conflict_or_ambiguity"
                or "answer.supplied_answer_missing";
    }

    /// <summary>
    /// Compares only the slot inventory, not prose punctuation or confidence.
    /// This makes two independently generated candidates comparable while still
    /// detecting omitted, merged, invented, reordered, or relabelled slots.
    /// </summary>
    internal static bool SlotInventoriesAgree(
        ValidatedTemplateExtraction first,
        ValidatedTemplateExtraction second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var firstPages = first.Pages
            .OrderBy(page => page.SourceId, StringComparer.Ordinal)
            .ThenBy(page => page.PageNumber)
            .ToArray();
        var secondPages = second.Pages
            .OrderBy(page => page.SourceId, StringComparer.Ordinal)
            .ThenBy(page => page.PageNumber)
            .ToArray();
        if (firstPages.Length != secondPages.Length)
        {
            return false;
        }

        for (var pageIndex = 0; pageIndex < firstPages.Length; pageIndex++)
        {
            var firstPage = firstPages[pageIndex];
            var secondPage = secondPages[pageIndex];
            if (!string.Equals(
                    firstPage.SourceId,
                    secondPage.SourceId,
                    StringComparison.Ordinal)
                || firstPage.PageNumber != secondPage.PageNumber
                || firstPage.DetectedAnswerSlotCount
                    != secondPage.DetectedAnswerSlotCount
                || firstPage.Questions.Count != secondPage.Questions.Count)
            {
                return false;
            }

            for (var questionIndex = 0;
                 questionIndex < firstPage.Questions.Count;
                 questionIndex++)
            {
                var firstQuestion = firstPage.Questions[questionIndex];
                var secondQuestion = secondPage.Questions[questionIndex];
                if (firstQuestion.AnswerSlotOrdinal
                        != secondQuestion.AnswerSlotOrdinal
                    || firstQuestion.AnswerSlotCount
                        != secondQuestion.AnswerSlotCount
                    || firstQuestion.IsEmbeddedFillBlank
                        != secondQuestion.IsEmbeddedFillBlank
                    || !string.Equals(
                        firstQuestion.AnswerProvenance,
                        secondQuestion.AnswerProvenance,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        firstQuestion.ExpectedAnswer,
                        secondQuestion.ExpectedAnswer,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        firstQuestion.DisplayLabel,
                        secondQuestion.DisplayLabel,
                        StringComparison.Ordinal)
                    || CountOccurrences(
                            firstQuestion.QuestionText,
                            CanonicalBlankToken)
                        != CountOccurrences(
                            secondQuestion.QuestionText,
                            CanonicalBlankToken))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static bool HasRepairableSlotStructureIssue(
        ValidatedTemplateExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return extraction.Pages.Any(page =>
                page.DetectedAnswerSlotCount != page.Questions.Count)
            || extraction.ReviewIssues.Any(issue =>
                SlotStructureIssueCodes.Contains(issue.Code))
            || extraction.Pages.Any(page => page.Questions.Any(question =>
                question.ReviewIssues.Any(issue =>
                    SlotStructureIssueCodes.Contains(issue.Code))));
    }

    public static ValidatedTemplateExtraction Validate(
        JsonElement root,
        string expectedRequestKey,
        IReadOnlyDictionary<string, TemplateExtractionSourceEvidence> sources,
        long defaultPointsMilli,
        long? targetTotalPointsMilli,
        bool requireGradingRuleFlags = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRequestKey);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Count > MaximumPages)
        {
            throw Invalid("template_extract_sources_invalid");
        }

        if (defaultPointsMilli <= 0
            || defaultPointsMilli > MaximumQuestionPointsMilli
            || targetTotalPointsMilli is < 0)
        {
            throw Invalid("template_extract_point_policy_invalid");
        }

        RequireObject(root, "template_extract_root_invalid");
        RequireExactProperties(
            root,
            [
                "schema_version",
                "request_key",
                "metadata",
                "pages",
                "global_warnings",
            ],
            "template_extract_root_shape_invalid");
        RequireString(
            root,
            "schema_version",
            64,
            "template_extract_schema_invalid",
            exact: "template_extract_v4");
        RequireString(
            root,
            "request_key",
            200,
            "template_extract_request_key_invalid",
            exact: expectedRequestKey);

        var metadata = ReadMetadata(root);
        var globalWarnings = ReadStringArray(
            root,
            "global_warnings",
            MaximumWarnings,
            1_000,
            "template_extract_global_warnings_invalid");
        var pagesElement = RequireArray(
            root,
            "pages",
            MaximumPages,
            "template_extract_pages_invalid");
        var pages = new List<ValidatedTemplatePage>(
            pagesElement.GetArrayLength());
        var pageKeys = new HashSet<string>(StringComparer.Ordinal);
        var questionKeys = new HashSet<string>(StringComparer.Ordinal);
        var displayLabels = new HashSet<string>(StringComparer.Ordinal);
        var totalQuestions = 0;
        long totalPointsMilli = 0;
        var reviewIssues = metadata.Warnings
            .Select(warning => new TemplateExtractionReviewIssue(
                "template.metadata_warning",
                warning,
                Blocking: false))
            .ToList();
        var authoritativeSourcesExist = sources.Values.Any(
            source => IsAuthoritativeRole(source.SourceRole));

        foreach (var pageElement in pagesElement.EnumerateArray())
        {
            RequireObject(pageElement, "template_extract_page_invalid");
            RequireExactProperties(
                pageElement,
                [
                    "source_id",
                    "page_number",
                    "detected_answer_slot_count",
                    "questions",
                ],
                "template_extract_page_shape_invalid");
            var sourceId = RequireString(
                pageElement,
                "source_id",
                200,
                "template_extract_source_id_invalid");
            if (!sources.TryGetValue(sourceId, out var source))
            {
                throw Invalid("template_extract_unknown_source");
            }

            var pageNumber = RequireInt32(
                pageElement,
                "page_number",
                1,
                source.PageCount,
                "template_extract_page_number_invalid");
            if (!pageKeys.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{sourceId}:{pageNumber}")))
            {
                throw Invalid("template_extract_duplicate_page");
            }

            var detectedAnswerSlotCount = RequireInt32(
                pageElement,
                "detected_answer_slot_count",
                0,
                MaximumQuestions,
                "template_extract_answer_slot_inventory_invalid");

            var questionsElement = RequireArray(
                pageElement,
                "questions",
                MaximumQuestions,
                "template_extract_questions_invalid");
            var questions = new List<ValidatedTemplateQuestion>(
                questionsElement.GetArrayLength());
            var answerSlotOrdinals = new HashSet<int>();

            foreach (var questionElement in questionsElement.EnumerateArray())
            {
                totalQuestions = checked(totalQuestions + 1);
                if (totalQuestions > MaximumQuestions)
                {
                    throw Invalid("template_extract_question_limit");
                }

                var question = ReadQuestion(
                    questionElement,
                    sourceId,
                    pageNumber,
                    sources,
                    authoritativeSourcesExist,
                    defaultPointsMilli,
                    requireGradingRuleFlags);
                if (!questionKeys.Add(question.SourceKey))
                {
                    question.ReviewIssues.Add(
                        new TemplateExtractionReviewIssue(
                            "template.duplicate_source_key",
                            $"抽出識別子「{question.SourceKey}」が重複しています。",
                            Blocking: true));
                }

                if (!answerSlotOrdinals.Add(question.AnswerSlotOrdinal))
                {
                    question.ReviewIssues.Add(
                        new TemplateExtractionReviewIssue(
                            "question.duplicate_answer_slot_ordinal",
                            $"ページ内の解答欄順 {question.AnswerSlotOrdinal} が重複しています。",
                            Blocking: true));
                }

                if (!displayLabels.Add(question.DisplayLabel))
                {
                    var originalLabel = question.DisplayLabel;
                    var suffix = 2;
                    do
                    {
                        question = question with
                        {
                            DisplayLabel = string.Create(
                                CultureInfo.InvariantCulture,
                                $"{originalLabel}（{suffix}回目）"),
                        };
                        suffix = checked(suffix + 1);
                    }
                    while (!displayLabels.Add(question.DisplayLabel));
                    question.ReviewIssues.Add(
                        new TemplateExtractionReviewIssue(
                            "question.repeated_printed_label_disambiguated",
                            $"印字番号「{originalLabel}」の再出現を" +
                            $"「{question.DisplayLabel}」として区別しました。",
                            Blocking: false));
                }

                totalPointsMilli = checked(
                    totalPointsMilli + question.SuggestedPointsMilli);
                questions.Add(question);
            }

            ResolveUnambiguousMultiPlaceholderTargets(questions);

            var expectedOrdinals = Enumerable.Range(
                    1,
                    questions.Count)
                .ToHashSet();
            if (detectedAnswerSlotCount != questions.Count
                || !answerSlotOrdinals.SetEquals(expectedOrdinals))
            {
                var message =
                    $"ページ{pageNumber}で検出した解答欄は" +
                    $"{detectedAnswerSlotCount}個ですが、" +
                    $"個別問題は{questions.Count}件です。" +
                    "解答欄の分割と順番を確認してください。";
                reviewIssues.Add(
                    new TemplateExtractionReviewIssue(
                        "template.answer_slot_inventory_mismatch",
                        message,
                        Blocking: true));
                foreach (var question in questions)
                {
                    question.ReviewIssues.Add(
                        new TemplateExtractionReviewIssue(
                            "question.answer_slot_inventory_mismatch",
                            message,
                            Blocking: true));
                }
            }

            pages.Add(
                new ValidatedTemplatePage(
                    sourceId,
                    pageNumber,
                    detectedAnswerSlotCount,
                    questions));
        }

        if (totalQuestions == 0)
        {
            throw Invalid("template_extract_questions_missing");
        }

        if (targetTotalPointsMilli is > 0
            && totalPointsMilli != targetTotalPointsMilli.Value)
        {
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"提案配点合計 {totalPointsMilli} は目標配点 " +
                $"{targetTotalPointsMilli.Value} と一致しません。");
            globalWarnings.Add(message);
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "template.target_total_mismatch",
                    message,
                    Blocking: true));
        }

        return new ValidatedTemplateExtraction(
            metadata,
            pages,
            globalWarnings,
            reviewIssues,
            totalPointsMilli);
    }

    private static ValidatedTemplateMetadata ReadMetadata(JsonElement root)
    {
        var element = RequireProperty(
            root,
            "metadata",
            "template_extract_metadata_invalid");
        RequireObject(element, "template_extract_metadata_invalid");
        RequireExactProperties(
            element,
            [
                "title",
                "subject",
                "category",
                "grade_label",
                "course",
                "confidence",
                "warnings",
            ],
            "template_extract_metadata_invalid");
        return new ValidatedTemplateMetadata(
            ReadNullableString(
                element,
                "title",
                500,
                "template_extract_metadata_invalid"),
            ReadNullableString(
                element,
                "subject",
                300,
                "template_extract_metadata_invalid"),
            ReadNullableString(
                element,
                "category",
                300,
                "template_extract_metadata_invalid"),
            ReadNullableString(
                element,
                "grade_label",
                200,
                "template_extract_metadata_invalid"),
            ReadNullableString(
                element,
                "course",
                300,
                "template_extract_metadata_invalid"),
            RequireFiniteDouble(
                element,
                "confidence",
                0,
                1,
                "template_extract_metadata_invalid"),
            ReadStringArray(
                element,
                "warnings",
                20,
                1_000,
                "template_extract_metadata_invalid"));
    }

    private static ValidatedTemplateQuestion ReadQuestion(
        JsonElement element,
        string pageSourceId,
        int pageNumber,
        IReadOnlyDictionary<string, TemplateExtractionSourceEvidence> sources,
        bool authoritativeSourcesExist,
        long defaultPointsMilli,
        bool requireGradingRuleFlags)
    {
        RequireObject(element, "template_extract_question_invalid");
        var hasRequiresCompleteAnswer = element.TryGetProperty(
            "requires_complete_answer_suggestion",
            out _);
        var hasAnswerOrderInsensitive = element.TryGetProperty(
            "answer_order_insensitive_suggestion",
            out _);
        if (hasRequiresCompleteAnswer != hasAnswerOrderInsensitive
            || (requireGradingRuleFlags && !hasRequiresCompleteAnswer))
        {
            throw Invalid("template_extract_question_shape_invalid");
        }

        var expectedProperties = new List<string>
        {
            "source_key",
            "display_label",
            "question_text",
            "answer_slot_ordinal",
            "answer_slot_count",
            "filled_answer_removed",
            "is_embedded_fill_blank",
            "question_type",
            "expected_answer",
            "answer_provenance",
            "answer_source",
            "accepted_variants",
            "suggested_points_milli",
            "allow_non_kanji_suggestion",
            "requires_teacher_answer",
            "confidence",
            "warnings",
        };
        if (hasRequiresCompleteAnswer)
        {
            expectedProperties.Add("requires_complete_answer_suggestion");
            expectedProperties.Add("answer_order_insensitive_suggestion");
        }

        RequireExactProperties(
            element,
            expectedProperties,
            "template_extract_question_shape_invalid");
        var sourceKey = RequireString(
            element,
            "source_key",
            200,
            "template_extract_source_key_invalid");
        var displayLabel = RequireString(
            element,
            "display_label",
            100,
            "template_extract_display_label_invalid");
        var questionText = RequireString(
            element,
            "question_text",
            20_000,
            "template_extract_question_text_invalid");
        var answerSlotOrdinal = RequireInt32(
            element,
            "answer_slot_ordinal",
            1,
            MaximumQuestions,
            "template_extract_answer_slot_ordinal_invalid");
        var answerSlotCount = RequireInt32(
            element,
            "answer_slot_count",
            0,
            20,
            "template_extract_answer_slot_count_invalid");
        var filledAnswerRemoved = RequireBoolean(
            element,
            "filled_answer_removed",
            "template_extract_filled_answer_removed_invalid");
        var isEmbeddedFillBlank = RequireBoolean(
            element,
            "is_embedded_fill_blank",
            "template_extract_fill_blank_flag_invalid");
        var questionType = RequireString(
            element,
            "question_type",
            64,
            "template_extract_question_type_invalid");
        if (!QuestionTypes.Contains(questionType))
        {
            throw Invalid("template_extract_question_type_invalid");
        }

        var expectedAnswer = ReadNullableString(
            element,
            "expected_answer",
            4_000,
            "template_extract_expected_answer_invalid");
        var provenance = RequireString(
            element,
            "answer_provenance",
            64,
            "template_extract_answer_provenance_invalid");
        if (!AnswerProvenances.Contains(provenance))
        {
            throw Invalid("template_extract_answer_provenance_invalid");
        }

        var answerSource = ReadNullableAnswerSource(
            element,
            "answer_source",
            sources);
        var aiProposalConflictsWithAuthority =
            provenance == "ai_proposed" && authoritativeSourcesExist;
        if (!aiProposalConflictsWithAuthority)
        {
            ValidateAnswerAuthority(
                provenance,
                expectedAnswer,
                answerSource,
                sources,
                authoritativeSourcesExist);
        }
        var acceptedVariants = ReadStringArray(
            element,
            "accepted_variants",
            50,
            4_000,
            "template_extract_accepted_variants_invalid");
        var uniqueVariants = new HashSet<string>(StringComparer.Ordinal);
        acceptedVariants = acceptedVariants
            .Where(variant =>
                !string.Equals(
                    variant,
                    expectedAnswer,
                    StringComparison.Ordinal)
                && uniqueVariants.Add(variant))
            .ToList();

        var suggestedPoints = RequireInt64(
            element,
            "suggested_points_milli",
            0,
            MaximumQuestionPointsMilli,
            "template_extract_points_invalid");
        var resolvedPoints = suggestedPoints == 0
            ? defaultPointsMilli
            : suggestedPoints;
        var allowNonKanji = RequireBoolean(
            element,
            "allow_non_kanji_suggestion",
            "template_extract_kanji_policy_invalid");
        var requiresCompleteAnswer = hasRequiresCompleteAnswer
            && RequireBoolean(
                element,
                "requires_complete_answer_suggestion",
                "template_extract_complete_answer_policy_invalid");
        var answerOrderInsensitive = hasAnswerOrderInsensitive
            && RequireBoolean(
                element,
                "answer_order_insensitive_suggestion",
                "template_extract_answer_order_policy_invalid");
        var requiresTeacherAnswer = RequireBoolean(
            element,
            "requires_teacher_answer",
            "template_extract_teacher_answer_invalid");
        var confidence = RequireFiniteDouble(
            element,
            "confidence",
            0,
            1,
            "template_extract_confidence_invalid");
        var warnings = ReadStringArray(
            element,
            "warnings",
            50,
            1_000,
            "template_extract_warnings_invalid");
        if (suggestedPoints == 0)
        {
            warnings.Add("配点が読み取れなかったため、既定配点を使用しました。");
        }

        var reviewIssues = new List<TemplateExtractionReviewIssue>();
        if (aiProposalConflictsWithAuthority)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "answer.source_conflict_or_ambiguity",
                    $"{displayLabel}で模範解答の出典を確認できず、AI独自の正答候補が返されました。",
                    Blocking: true));
        }

        var blankAnalysis = NormalizeEmbeddedBlankText(
            questionText,
            isEmbeddedFillBlank,
            filledAnswerRemoved,
            expectedAnswer,
            acceptedVariants);
        questionText = blankAnalysis.QuestionText;
        var normalizedOcrText = NormalizeKnownJapaneseOcrNoise(questionText);
        if (!string.Equals(
                normalizedOcrText,
                questionText,
                StringComparison.Ordinal))
        {
            questionText = normalizedOcrText;
            blankAnalysis = blankAnalysis with { QuestionText = questionText };
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.ocr_noise_corrected",
                    $"{displayLabel}の明らかなOCRノイズを補正しました。",
                    Blocking: false));
        }
        if (blankAnalysis.InferredFillBlank && !isEmbeddedFillBlank)
        {
            isEmbeddedFillBlank = true;
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.fill_blank_classification_corrected",
                    $"{displayLabel}を穴埋め問題として安全側に補正しました。",
                    Blocking: true));
        }

        if (blankAnalysis.RemovedVisibleContent)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.filled_answer_redacted",
                    $"{displayLabel}の問題文に混入した解答を空欄へ置換しました。",
                    Blocking: true));
        }

        if (isEmbeddedFillBlank && blankAnalysis.PlaceholderCount > 1)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.additional_placeholders_redacted",
                    $"{displayLabel}の対象外の空欄を問題文から省略しました。対象欄を原稿で確認してください。",
                    Blocking: true));
        }

        if (answerSlotCount != 1)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.answer_slots_not_separated",
                    $"{displayLabel}が{answerSlotCount}個の解答欄をまとめている可能性があります。",
                    Blocking: true));
        }

        if (!filledAnswerRemoved)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.filled_answer_removal_unconfirmed",
                    $"{displayLabel}の記入済み内容を問題文から除外できたか確認が必要です。",
                    Blocking: true));
        }

        if (isEmbeddedFillBlank && blankAnalysis.PlaceholderCount != 1)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.fill_blank_placeholder_invalid",
                    $"{displayLabel}の問題文にある対象空欄を1個に分離できませんでした。",
                    Blocking: true));
        }

        if (expectedAnswer is null)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    authoritativeSourcesExist
                        ? "answer.supplied_answer_missing"
                        : "answer.expected_answer_missing",
                    authoritativeSourcesExist
                        ? $"{displayLabel}に対応する模範解答を確認できませんでした。"
                        : $"{displayLabel}の正答候補を作成できませんでした。",
                    Blocking: questionType is
                        "multiple_choice"
                        or "boolean"
                        or "numeric"
                        or "exact_short_text"));
        }

        if (requiresTeacherAnswer
            && provenance == "provided_model_answer")
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "answer.source_conflict_or_ambiguity",
                    $"{displayLabel}の模範解答に不一致または曖昧さがあります。",
                    Blocking: true));
        }

        if (confidence < 0.95)
        {
            reviewIssues.Add(
                new TemplateExtractionReviewIssue(
                    "question.ai_confidence_low",
                    $"{displayLabel}の抽出信頼度が基準を下回っています。",
                    Blocking: false));
        }

        return new ValidatedTemplateQuestion(
            sourceKey,
            pageSourceId,
            pageNumber,
            displayLabel,
            questionText,
            answerSlotOrdinal,
            answerSlotCount,
            filledAnswerRemoved,
            isEmbeddedFillBlank,
            questionType,
            expectedAnswer,
            provenance,
            answerSource,
            acceptedVariants,
            resolvedPoints,
            allowNonKanji,
            requiresCompleteAnswer,
            answerOrderInsensitive,
            requiresTeacherAnswer,
            confidence,
            warnings,
            reviewIssues);
    }

    private static void ResolveUnambiguousMultiPlaceholderTargets(
        List<ValidatedTemplateQuestion> questions)
    {
        for (var index = 0; index < questions.Count;)
        {
            var question = questions[index];
            var placeholderCount = CountOccurrences(
                question.QuestionText,
                CanonicalBlankToken);
            if (!question.IsEmbeddedFillBlank || placeholderCount <= 1)
            {
                index++;
                continue;
            }

            var runEnd = index + 1;
            while (runEnd < questions.Count
                   && string.Equals(
                       questions[runEnd].QuestionText,
                       question.QuestionText,
                       StringComparison.Ordinal))
            {
                runEnd++;
            }

            var runLength = runEnd - index;
            var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
            var unambiguous = runLength == placeholderCount;
            for (var offset = 0; unambiguous && offset < runLength; offset++)
            {
                var candidate = questions[index + offset];
                unambiguous = candidate.IsEmbeddedFillBlank
                    && candidate.AnswerSlotCount == 1
                    && candidate.AnswerSlotOrdinal
                        == question.AnswerSlotOrdinal + offset
                    && sourceKeys.Add(candidate.SourceKey)
                    && !candidate.ReviewIssues.Any(issue => issue.Code is
                        "template.duplicate_source_key"
                        or "question.duplicate_answer_slot_ordinal");
            }

            for (var offset = 0; offset < runLength; offset++)
            {
                var candidate = questions[index + offset];
                if (unambiguous)
                {
                    candidate.ReviewIssues.RemoveAll(issue => issue.Code is
                        "question.additional_placeholders_redacted"
                        or "question.fill_blank_placeholder_invalid");
                    questions[index + offset] = candidate with
                    {
                        QuestionText = RetainPlaceholder(
                            candidate.QuestionText,
                            offset),
                    };
                }
                else
                {
                    questions[index + offset] = candidate with
                    {
                        QuestionText = RetainOnlyFirstPlaceholder(
                            candidate.QuestionText),
                    };
                }
            }

            index = runEnd;
        }
    }

    private static EmbeddedBlankAnalysis NormalizeEmbeddedBlankText(
        string questionText,
        bool declaredFillBlank,
        bool filledAnswerRemoved,
        string? expectedAnswer,
        IReadOnlyCollection<string> acceptedVariants)
    {
        var answerCandidates = acceptedVariants
            .Append(expectedAnswer)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var bracketSpans = FindSquareBracketSpans(questionText);
        var inferredFillBlank = declaredFillBlank
            || bracketSpans.Any(span =>
                IsBlankSlotContent(span.Content)
                || answerCandidates.Contains(span.Content.Trim()));
        if (!inferredFillBlank)
        {
            return new EmbeddedBlankAnalysis(
                questionText,
                InferredFillBlank: false,
                RemovedVisibleContent: false,
                PlaceholderCount: CountOccurrences(
                    questionText,
                    CanonicalBlankToken));
        }

        var spansToReplace = bracketSpans
            .Where(span =>
                IsBlankSlotContent(span.Content)
                || answerCandidates.Contains(span.Content.Trim()))
            .ToList();
        if (declaredFillBlank
            && spansToReplace.Count == 0
            && bracketSpans.Count == 1)
        {
            spansToReplace.Add(bracketSpans[0]);
        }
        else if (declaredFillBlank
                 && !filledAnswerRemoved
                 && spansToReplace.Count == 0)
        {
            // If the provider explicitly says visible content remains, remove all
            // square-box contents rather than risk showing student/model answers.
            // Multiple resulting blanks stay review-blocking below.
            spansToReplace.AddRange(bracketSpans);
        }

        if (spansToReplace.Count == 0)
        {
            return new EmbeddedBlankAnalysis(
                questionText,
                inferredFillBlank,
                RemovedVisibleContent: false,
                PlaceholderCount: CountOccurrences(
                    questionText,
                    CanonicalBlankToken));
        }

        var builder = new StringBuilder(questionText);
        var removedVisibleContent = false;
        foreach (var span in spansToReplace
                     .OrderByDescending(span => span.Start))
        {
            removedVisibleContent |= !IsBlankSlotContent(span.Content);
            builder.Remove(span.Start, span.Length);
            builder.Insert(span.Start, CanonicalBlankToken);
        }

        var normalized = builder.ToString();
        return new EmbeddedBlankAnalysis(
            normalized,
            inferredFillBlank,
            removedVisibleContent,
            CountOccurrences(normalized, CanonicalBlankToken));
    }

    private static List<SquareBracketSpan> FindSquareBracketSpans(string value)
    {
        var spans = new List<SquareBracketSpan>();
        for (var index = 0; index < value.Length; index++)
        {
            var close = value[index] switch
            {
                '[' => ']',
                '［' => '］',
                '【' => '】',
                _ => '\0',
            };
            if (close == '\0')
            {
                continue;
            }

            var closeIndex = value.IndexOf(close, index + 1);
            if (closeIndex < 0 || closeIndex - index > 102)
            {
                continue;
            }

            spans.Add(
                new SquareBracketSpan(
                    index,
                    closeIndex - index + 1,
                    value[(index + 1)..closeIndex]));
            index = closeIndex;
        }

        return spans;
    }

    private static bool IsBlankSlotContent(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.All(character => character is '_' or '＿' or '…' or '・');

    private static string RetainOnlyFirstPlaceholder(string value) =>
        RetainPlaceholder(value, targetOccurrence: 0);

    private static string RetainPlaceholder(
        string value,
        int targetOccurrence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetOccurrence);

        var builder = new StringBuilder(value.Length);
        var currentOccurrence = 0;
        var startIndex = 0;
        while (true)
        {
            var placeholderIndex = value.IndexOf(
                CanonicalBlankToken,
                startIndex,
                StringComparison.Ordinal);
            if (placeholderIndex < 0)
            {
                builder.Append(value, startIndex, value.Length - startIndex);
                break;
            }

            builder.Append(value, startIndex, placeholderIndex - startIndex);
            builder.Append(currentOccurrence == targetOccurrence
                ? CanonicalBlankToken
                : "（別の空欄は省略）");
            currentOccurrence++;
            startIndex = placeholderIndex + CanonicalBlankToken.Length;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            targetOccurrence,
            currentOccurrence);

        return builder.ToString();
    }

    private static string NormalizeKnownJapaneseOcrNoise(string value) =>
        JapaneseBounceOcrNoisePattern.Replace(value, "はね返される")
            .Replace(
                "といいう",
                "といい",
                StringComparison.Ordinal);

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(
                   search,
                   startIndex,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += search.Length;
        }

        return count;
    }

    private static void ValidateAnswerAuthority(
        string provenance,
        string? expectedAnswer,
        TemplateExtractionAnswerSource? answerSource,
        IReadOnlyDictionary<string, TemplateExtractionSourceEvidence> sources,
        bool authoritativeSourcesExist)
    {
        switch (provenance)
        {
            case "provided_model_answer":
                if (string.IsNullOrWhiteSpace(expectedAnswer)
                    || answerSource is null
                    || !sources.TryGetValue(
                        answerSource.SourceId,
                        out var source)
                    || !CanProvideVisibleModelAnswer(source.SourceRole))
                {
                    throw Invalid(
                        "template_extract_provided_answer_source_invalid");
                }

                break;
            case "ai_proposed":
                if (string.IsNullOrWhiteSpace(expectedAnswer)
                    || answerSource is not null
                    || authoritativeSourcesExist)
                {
                    throw Invalid(
                        "template_extract_ai_answer_authority_conflict");
                }

                break;
            case "unavailable":
                if (expectedAnswer is not null || answerSource is not null)
                {
                    throw Invalid("template_extract_unavailable_answer_invalid");
                }

                break;
            default:
                throw Invalid("template_extract_answer_provenance_invalid");
        }
    }

    private static TemplateExtractionAnswerSource? ReadNullableAnswerSource(
        JsonElement owner,
        string propertyName,
        IReadOnlyDictionary<string, TemplateExtractionSourceEvidence> sources)
    {
        var element = RequireProperty(
            owner,
            propertyName,
            "template_extract_answer_source_invalid");
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireObject(element, "template_extract_answer_source_invalid");
        RequireExactProperties(
            element,
            ["source_id", "page_number"],
            "template_extract_answer_source_invalid");
        var sourceId = RequireString(
            element,
            "source_id",
            200,
            "template_extract_answer_source_invalid");
        if (!sources.TryGetValue(sourceId, out var source))
        {
            throw Invalid("template_extract_answer_source_invalid");
        }

        var answerPage = RequireInt32(
            element,
            "page_number",
            1,
            source.PageCount,
            "template_extract_answer_source_page_invalid");
        return new TemplateExtractionAnswerSource(
            sourceId,
            answerPage);
    }

    private static List<string> ReadStringArray(
        JsonElement owner,
        string propertyName,
        int maximumItems,
        int maximumStringLength,
        string errorCode)
    {
        var array = RequireArray(
            owner,
            propertyName,
            maximumItems,
            errorCode);
        var values = new List<string>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String
                || element.GetString() is not { } value
                || string.IsNullOrWhiteSpace(value)
                || value.Length > maximumStringLength)
            {
                throw Invalid(errorCode);
            }

            values.Add(value.Trim());
        }

        return values;
    }

    private static string? ReadNullableString(
        JsonElement owner,
        string propertyName,
        int maximumLength,
        string errorCode)
    {
        var element = RequireProperty(owner, propertyName, errorCode);
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } value
            || string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength)
        {
            throw Invalid(errorCode);
        }

        return value.Trim();
    }

    private static string RequireString(
        JsonElement owner,
        string propertyName,
        int maximumLength,
        string errorCode,
        string? exact = null)
    {
        var element = RequireProperty(owner, propertyName, errorCode);
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } value
            || string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || (exact is not null
                && !string.Equals(value, exact, StringComparison.Ordinal)))
        {
            throw Invalid(errorCode);
        }

        return value;
    }

    private static bool RequireBoolean(
        JsonElement owner,
        string propertyName,
        string errorCode)
    {
        var element = RequireProperty(owner, propertyName, errorCode);
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(errorCode),
        };
    }

    private static int RequireInt32(
        JsonElement owner,
        string propertyName,
        int minimum,
        int maximum,
        string errorCode)
    {
        var element = RequireProperty(owner, propertyName, errorCode);
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < minimum
            || value > maximum)
        {
            throw Invalid(errorCode);
        }

        return value;
    }

    private static long RequireInt64(
        JsonElement owner,
        string propertyName,
        long minimum,
        long maximum,
        string errorCode)
    {
        var element = RequireProperty(owner, propertyName, errorCode);
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var value)
            || value < minimum
            || value > maximum)
        {
            throw Invalid(errorCode);
        }

        return value;
    }

    private static double RequireFiniteDouble(
        JsonElement owner,
        string propertyName,
        double minimum,
        double maximum,
        string errorCode)
    {
        var element = RequireProperty(owner, propertyName, errorCode);
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value < minimum
            || value > maximum)
        {
            throw Invalid(errorCode);
        }

        return value;
    }

    private static JsonElement RequireArray(
        JsonElement owner,
        string propertyName,
        int maximumItems,
        string errorCode)
    {
        var element = RequireProperty(owner, propertyName, errorCode);
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > maximumItems)
        {
            throw Invalid(errorCode);
        }

        return element;
    }

    private static JsonElement RequireProperty(
        JsonElement owner,
        string propertyName,
        string errorCode)
    {
        if (!owner.TryGetProperty(propertyName, out var element))
        {
            throw Invalid(errorCode);
        }

        return element;
    }

    private static void RequireObject(JsonElement element, string errorCode)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(errorCode);
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        List<string> expected,
        string errorCode)
    {
        var actual = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (actual.Length != expected.Count
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || actual.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
        {
            throw Invalid(errorCode);
        }
    }

    private static bool IsAuthoritativeRole(string role) =>
        role is "contains_model_answers" or "separate_answer_key";

    private static bool CanProvideVisibleModelAnswer(string role) =>
        IsAuthoritativeRole(role) || role == "unit_test_paper";

    private static InvalidDataException Invalid(string code) => new(code);
}

internal sealed record TemplateExtractionSourceEvidence(
    string SourceId,
    string SourceRole,
    int PageCount);

internal sealed record TemplateExtractionAnswerSource(
    string SourceId,
    int PageNumber);

internal sealed record TemplateExtractionReviewIssue(
    string Code,
    string Message,
    bool Blocking);

internal sealed record ValidatedTemplateMetadata(
    string? Title,
    string? Subject,
    string? Category,
    string? GradeLabel,
    string? Course,
    double Confidence,
    IReadOnlyList<string> Warnings);

internal sealed record ValidatedTemplateQuestion(
    string SourceKey,
    string PageSourceId,
    int PageNumber,
    string DisplayLabel,
    string QuestionText,
    int AnswerSlotOrdinal,
    int AnswerSlotCount,
    bool FilledAnswerRemoved,
    bool IsEmbeddedFillBlank,
    string QuestionType,
    string? ExpectedAnswer,
    string AnswerProvenance,
    TemplateExtractionAnswerSource? AnswerSource,
    IReadOnlyList<string> AcceptedVariants,
    long SuggestedPointsMilli,
    bool AllowNonKanjiSuggestion,
    bool RequiresCompleteAnswerSuggestion,
    bool AnswerOrderInsensitiveSuggestion,
    bool RequiresTeacherAnswer,
    double Confidence,
    IReadOnlyList<string> Warnings,
    List<TemplateExtractionReviewIssue> ReviewIssues);

internal sealed record ValidatedTemplatePage(
    string SourceId,
    int PageNumber,
    int DetectedAnswerSlotCount,
    IReadOnlyList<ValidatedTemplateQuestion> Questions);

internal sealed record EmbeddedBlankAnalysis(
    string QuestionText,
    bool InferredFillBlank,
    bool RemovedVisibleContent,
    int PlaceholderCount);

internal sealed record SquareBracketSpan(
    int Start,
    int Length,
    string Content);

internal sealed record ValidatedTemplateExtraction(
    ValidatedTemplateMetadata Metadata,
    IReadOnlyList<ValidatedTemplatePage> Pages,
    IReadOnlyList<string> GlobalWarnings,
    IReadOnlyList<TemplateExtractionReviewIssue> ReviewIssues,
    long TotalPointsMilli);

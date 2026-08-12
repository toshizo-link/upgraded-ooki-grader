using System.Collections.ObjectModel;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;

namespace OokiGrader.Domain.Templates;

public sealed class QuestionDefinition
{
    private readonly ReadOnlyCollection<AcceptedAnswer> _acceptedAnswers;
    private readonly ReadOnlyCollection<RubricRule> _rubricRules;

    public QuestionDefinition(
        string id,
        string logicalQuestionId,
        int orderIndex,
        string displayLabel,
        string questionText,
        QuestionType questionType,
        GradingMode gradingMode,
        MilliPoints maximumPoints,
        MilliPoints pointIncrement,
        bool allowNonKanji,
        bool requiresReviewAlways,
        bool teacherVerified,
        IEnumerable<AcceptedAnswer>? acceptedAnswers = null,
        IEnumerable<RubricRule>? rubricRules = null,
        NumericAnswerPolicy? numericPolicy = null,
        ChoiceAnswerPolicy? choicePolicy = null,
        string? kanjiPolicyNote = null,
        bool requiresCompleteAnswer = false,
        bool answerOrderInsensitive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalQuestionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayLabel);
        ArgumentNullException.ThrowIfNull(questionText);
        ArgumentOutOfRangeException.ThrowIfNegative(orderIndex);

        var answers = (acceptedAnswers ?? []).ToArray();
        var rules = (rubricRules ?? []).OrderBy(rule => rule.OrderIndex).ToArray();

        EnsureUnique(
            answers.Select(answer => answer.Id),
            "question.duplicate_answer_id",
            "Accepted-answer IDs must be unique within a question.");
        EnsureUnique(
            rules.Select(rule => rule.Id),
            "question.duplicate_rubric_id",
            "Rubric-rule IDs must be unique within a question.");
        EnsureUnique(
            rules.Select(rule => rule.OrderIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "question.duplicate_rubric_order",
            "Rubric-rule order indexes must be unique within a question.");

        Id = id;
        LogicalQuestionId = logicalQuestionId;
        OrderIndex = orderIndex;
        DisplayLabel = displayLabel;
        QuestionText = questionText;
        QuestionType = questionType;
        GradingMode = gradingMode;
        PointPolicy = new PointAwardPolicy(maximumPoints, pointIncrement);
        AllowNonKanji = allowNonKanji;
        RequiresReviewAlways = requiresReviewAlways;
        TeacherVerified = teacherVerified;
        _acceptedAnswers = Array.AsReadOnly(answers);
        _rubricRules = Array.AsReadOnly(rules);
        NumericPolicy = numericPolicy;
        ChoicePolicy = choicePolicy;
        KanjiPolicyNote = string.IsNullOrWhiteSpace(kanjiPolicyNote) ? null : kanjiPolicyNote;
        RequiresCompleteAnswer = requiresCompleteAnswer;
        AnswerOrderInsensitive = answerOrderInsensitive;
    }

    public string Id { get; }

    public string LogicalQuestionId { get; }

    public int OrderIndex { get; }

    public string DisplayLabel { get; }

    public string QuestionText { get; }

    public QuestionType QuestionType { get; }

    public GradingMode GradingMode { get; }

    public PointAwardPolicy PointPolicy { get; }

    public MilliPoints MaximumPoints => PointPolicy.Maximum;

    public MilliPoints PointIncrement => PointPolicy.Increment;

    public bool AllowNonKanji { get; }

    /// <summary>
    /// Requires an all-or-nothing award. Incomplete component or rubric
    /// matches receive zero rather than partial credit.
    /// </summary>
    public bool RequiresCompleteAnswer { get; }

    /// <summary>
    /// Compares explicitly separated answer components as a multiset. All
    /// components, including duplicate occurrences, must still be present.
    /// </summary>
    public bool AnswerOrderInsensitive { get; }

    public bool RequiresReviewAlways { get; }

    public bool TeacherVerified { get; }

    public IReadOnlyList<AcceptedAnswer> AcceptedAnswers => _acceptedAnswers;

    public IReadOnlyList<RubricRule> RubricRules => _rubricRules;

    public NumericAnswerPolicy? NumericPolicy { get; }

    public ChoiceAnswerPolicy? ChoicePolicy { get; }

    public string? KanjiPolicyNote { get; }

    public AcceptedAnswer? CanonicalAnswer =>
        _acceptedAnswers.FirstOrDefault(
            answer => answer.VariantType == AcceptedAnswerVariantType.Canonical);

    public QuestionDefinition ConfirmProposalByTeacher()
    {
        var confirmedAnswers = _acceptedAnswers
            .Select(answer => new AcceptedAnswer(
                answer.Id,
                answer.AnswerText,
                answer.VariantType,
                answer.Provenance,
                teacherVerified: true,
                answer.Source))
            .ToArray();
        var confirmedRules = _rubricRules
            .Select(rule => new RubricRule(
                rule.Id,
                rule.OrderIndex,
                rule.ConditionType,
                rule.Description,
                rule.Points,
                teacherVerified: true,
                rule.MutuallyExclusiveGroup))
            .ToArray();

        return new QuestionDefinition(
            Id,
            LogicalQuestionId,
            OrderIndex,
            DisplayLabel,
            QuestionText,
            QuestionType,
            GradingMode,
            MaximumPoints,
            PointIncrement,
            AllowNonKanji,
            RequiresReviewAlways,
            teacherVerified: true,
            confirmedAnswers,
            confirmedRules,
            NumericPolicy,
            ChoicePolicy,
            KanjiPolicyNote,
            RequiresCompleteAnswer,
            AnswerOrderInsensitive);
    }

    public DomainValidationResult ValidateForPublish(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var errors = new List<DomainError>();

        if (!TeacherVerified)
        {
            errors.Add(
                new DomainError(
                    "question.not_teacher_verified",
                    "Every question must be teacher-verified before publishing.",
                    path));
        }

        if (_acceptedAnswers.Any(answer => !answer.TeacherVerified))
        {
            errors.Add(
                new DomainError(
                    "answer.not_teacher_verified",
                    "Every accepted answer must be teacher-verified before publishing.",
                    $"{path}.acceptedAnswers"));
        }

        if (_rubricRules.Any(rule => !rule.TeacherVerified))
        {
            errors.Add(
                new DomainError(
                    "rubric.not_teacher_verified",
                    "Every rubric rule must be teacher-verified before publishing.",
                    $"{path}.rubricRules"));
        }

        var canonicalCount = _acceptedAnswers.Count(
            answer => answer.VariantType == AcceptedAnswerVariantType.Canonical);
        var answerRequired = GradingMode is GradingMode.Deterministic
            or GradingMode.TranscribeThenRules;
        if (answerRequired && canonicalCount != 1)
        {
            errors.Add(
                new DomainError(
                    "answer.canonical_count",
                    "Deterministic and rule-based questions require exactly one canonical answer.",
                    $"{path}.acceptedAnswers"));
        }

        if (_acceptedAnswers.Any(
                answer => answer.VariantType == AcceptedAnswerVariantType.RegexRestricted))
        {
            errors.Add(
                new DomainError(
                    "answer.regex_not_supported",
                    "Regex accepted answers are not supported in the MVP domain.",
                    $"{path}.acceptedAnswers"));
        }

        foreach (var answer in _acceptedAnswers.Where(
                     answer => answer.Provenance == AnswerProvenance.ProvidedModelAnswer))
        {
            if (answer.Source is null
                || answer.Source.SourceRole is not (
                    TemplateSourceRole.ContainsModelAnswers
                    or TemplateSourceRole.SeparateAnswerKey))
            {
                errors.Add(
                    new DomainError(
                        "answer.invalid_provided_source",
                        "A provided model answer must reference a source explicitly marked as containing model answers.",
                        $"{path}.acceptedAnswers[{answer.Id}]"));
            }
        }

        var rubricTotal = _rubricRules.Aggregate(
            MilliPoints.Zero,
            (total, rule) => total + rule.Points);
        if (rubricTotal > MaximumPoints)
        {
            errors.Add(
                new DomainError(
                    "rubric.above_maximum",
                    "Rubric-rule points cannot exceed the question maximum.",
                    $"{path}.rubricRules"));
        }

        foreach (var rule in _rubricRules)
        {
            var ruleAward = PointPolicy.ValidateAward(
                rule.Points,
                $"{path}.rubricRules[{rule.Id}].points");
            errors.AddRange(ruleAward.Errors);
        }

        if (QuestionType == QuestionType.Numeric && NumericPolicy is null)
        {
            errors.Add(
                new DomainError(
                    "numeric.policy_required",
                    "Numeric questions require an explicit numeric policy.",
                    $"{path}.numericPolicy"));
        }
        else if (QuestionType == QuestionType.Numeric
            && NumericPolicy is not null
            && CanonicalAnswer is not null)
        {
            var parsedCanonical = NumericAnswerParser.Parse(
                CanonicalAnswer.AnswerText,
                NumericPolicy);
            if (!parsedCanonical.Success
                || !NumericAnswerParser.Matches(parsedCanonical.Value, NumericPolicy))
            {
                errors.Add(
                    new DomainError(
                        "numeric.canonical_policy_mismatch",
                        "The canonical numeric answer must satisfy the explicit numeric policy.",
                        $"{path}.acceptedAnswers"));
            }
        }

        if (QuestionType is QuestionType.MultipleChoice or QuestionType.Boolean
            && ChoicePolicy is null)
        {
            errors.Add(
                new DomainError(
                    "choice.policy_required",
                    "Choice and Boolean questions require an explicit choice policy.",
                    $"{path}.choicePolicy"));
        }
        else if (QuestionType is QuestionType.MultipleChoice or QuestionType.Boolean
            && ChoicePolicy is not null
            && CanonicalAnswer is not null
            && !string.Equals(
                CanonicalAnswer.NormalizedText,
                ChoicePolicy.CorrectChoice,
                StringComparison.Ordinal))
        {
            errors.Add(
                new DomainError(
                    "choice.canonical_policy_mismatch",
                    "The canonical choice answer must match the explicit choice policy.",
                    $"{path}.acceptedAnswers"));
        }

        if (QuestionType == QuestionType.Unsupported
            && (!RequiresReviewAlways || GradingMode != GradingMode.Manual))
        {
            errors.Add(
                new DomainError(
                    "question.manual_review_required",
                    "Unsupported questions must be manual and always require review.",
                    path));
        }

        if (GradingMode == GradingMode.AiRubric && _rubricRules.Count == 0)
        {
            errors.Add(
                new DomainError(
                    "rubric.required",
                    "AI-rubric questions require at least one teacher-approved rubric rule.",
                    $"{path}.rubricRules"));
        }

        return errors.Count == 0
            ? DomainValidationResult.Valid()
            : DomainValidationResult.Invalid(errors);
    }

    private static void EnsureUnique(
        IEnumerable<string> values,
        string errorCode,
        string message)
    {
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count())
        {
            throw new DomainValidationException([new DomainError(errorCode, message)]);
        }
    }
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Scoring;

namespace OokiGrader.Domain.Templates;

public sealed class TemplateVersion
{
    private readonly ReadOnlyCollection<QuestionDefinition> _questions;

    private TemplateVersion(
        string id,
        string templateId,
        int versionNumber,
        TemplateVersionState state,
        IEnumerable<QuestionDefinition> questions,
        string? basedOnVersionId,
        MilliPoints? targetTotalPoints,
        bool defaultAllowNonKanji,
        string pipelineVersion,
        string? aiGenerationProvenanceId,
        string? publishedBy,
        DateTimeOffset? publishedAt,
        string? contentHash,
        long revision)
    {
        Id = id;
        TemplateId = templateId;
        VersionNumber = versionNumber;
        State = state;
        _questions = Array.AsReadOnly(
            questions.OrderBy(question => question.OrderIndex).ToArray());
        BasedOnVersionId = basedOnVersionId;
        TargetTotalPoints = targetTotalPoints;
        DefaultAllowNonKanji = defaultAllowNonKanji;
        PipelineVersion = pipelineVersion;
        AiGenerationProvenanceId = aiGenerationProvenanceId;
        PublishedBy = publishedBy;
        PublishedAt = publishedAt;
        ContentHash = contentHash;
        Revision = revision;
    }

    public string Id { get; }

    public string TemplateId { get; }

    public int VersionNumber { get; }

    public TemplateVersionState State { get; }

    public IReadOnlyList<QuestionDefinition> Questions => _questions;

    public string? BasedOnVersionId { get; }

    public MilliPoints? TargetTotalPoints { get; }

    public bool DefaultAllowNonKanji { get; }

    public string PipelineVersion { get; }

    public string? AiGenerationProvenanceId { get; }

    public string? PublishedBy { get; }

    public DateTimeOffset? PublishedAt { get; }

    public string? ContentHash { get; }

    public long Revision { get; }

    public static TemplateVersion CreateDraft(
        string id,
        string templateId,
        int versionNumber,
        string pipelineVersion,
        IEnumerable<QuestionDefinition>? questions = null,
        string? basedOnVersionId = null,
        MilliPoints? targetTotalPoints = null,
        bool defaultAllowNonKanji = false,
        string? aiGenerationProvenanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(versionNumber);

        return new TemplateVersion(
            id,
            templateId,
            versionNumber,
            TemplateVersionState.Draft,
            questions ?? [],
            basedOnVersionId,
            targetTotalPoints,
            defaultAllowNonKanji,
            pipelineVersion,
            aiGenerationProvenanceId,
            null,
            null,
            null,
            0);
    }

    public TemplateVersion WithQuestion(QuestionDefinition question)
    {
        ArgumentNullException.ThrowIfNull(question);
        EnsureEditable();

        var questions = _questions
            .Where(existing => !string.Equals(existing.Id, question.Id, StringComparison.Ordinal))
            .Append(question);

        return Copy(questions: questions, revision: checked(Revision + 1));
    }

    public TemplateVersion WithoutQuestion(string questionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        EnsureEditable();

        if (_questions.All(
                question => !string.Equals(question.Id, questionId, StringComparison.Ordinal)))
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "template.question_not_found",
                    $"Question '{questionId}' does not exist.",
                    nameof(questionId)),
            ]);
        }

        return Copy(
            questions: _questions.Where(
                question => !string.Equals(question.Id, questionId, StringComparison.Ordinal)),
            revision: checked(Revision + 1));
    }

    public TemplateVersion ConfirmQuestionProposals(
        IEnumerable<string> questionIds)
    {
        ArgumentNullException.ThrowIfNull(questionIds);
        EnsureEditable();

        var requestedIds = questionIds.ToArray();
        if (requestedIds.Any(string.IsNullOrWhiteSpace)
            || requestedIds.Distinct(StringComparer.Ordinal).Count()
                != requestedIds.Length)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "template.invalid_confirmation_set",
                    "Question confirmation IDs must be non-empty and unique.",
                    nameof(questionIds)),
            ]);
        }

        var requestedSet = requestedIds.ToHashSet(StringComparer.Ordinal);
        var missingIds = requestedSet
            .Except(_questions.Select(question => question.Id), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingIds.Length > 0)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "template.question_not_found",
                    "Every confirmed question must belong to this template version.",
                    nameof(questionIds)),
            ]);
        }

        if (requestedIds.Length == 0)
        {
            return this;
        }

        return Copy(
            questions: _questions.Select(question =>
                requestedSet.Contains(question.Id)
                    ? question.ConfirmProposalByTeacher()
                    : question),
            revision: checked(Revision + 1));
    }

    public TemplateVersion BeginGeneration()
    {
        EnsureState(TemplateVersionState.Draft);
        return Copy(
            state: TemplateVersionState.Generating,
            revision: checked(Revision + 1));
    }

    public TemplateVersion CompleteGeneration(
        IEnumerable<QuestionDefinition> generatedQuestions,
        string aiGenerationProvenanceId)
    {
        ArgumentNullException.ThrowIfNull(generatedQuestions);
        ArgumentException.ThrowIfNullOrWhiteSpace(aiGenerationProvenanceId);
        EnsureState(TemplateVersionState.Generating);

        return Copy(
            state: TemplateVersionState.Draft,
            questions: generatedQuestions,
            aiGenerationProvenanceId: aiGenerationProvenanceId,
            revision: checked(Revision + 1));
    }

    public TemplateVersion CancelGeneration()
    {
        EnsureState(TemplateVersionState.Generating);
        return Copy(
            state: TemplateVersionState.Draft,
            revision: checked(Revision + 1));
    }

    public DomainValidationResult ValidateForPublish()
    {
        var errors = new List<DomainError>();

        if (State != TemplateVersionState.Draft)
        {
            errors.Add(
                new DomainError(
                    "template.not_draft",
                    "Only a draft template version can be published.",
                    nameof(State)));
        }

        if (_questions.Count == 0)
        {
            errors.Add(
                new DomainError(
                    "template.no_questions",
                    "A template version must contain at least one question.",
                    nameof(Questions)));
        }

        AddDuplicateErrors(
            errors,
            _questions.Select(question => question.Id),
            "template.duplicate_question_id",
            "Question IDs must be unique.");
        AddDuplicateErrors(
            errors,
            _questions.Select(question => question.LogicalQuestionId),
            "template.duplicate_logical_question_id",
            "Logical question IDs must be unique.");
        AddDuplicateErrors(
            errors,
            _questions.Select(question => question.DisplayLabel),
            "template.duplicate_display_label",
            "Display labels must be unique.");
        AddDuplicateErrors(
            errors,
            _questions.Select(
                question => question.OrderIndex.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            "template.duplicate_order",
            "Question order indexes must be unique.");

        for (var index = 0; index < _questions.Count; index++)
        {
            var questionResult = _questions[index].ValidateForPublish($"questions[{index}]");
            errors.AddRange(questionResult.Errors);
        }

        var total = MilliPoints.Zero;
        var totalOverflowed = false;
        try
        {
            total = _questions.Aggregate(
                MilliPoints.Zero,
                (sum, question) => sum + question.MaximumPoints);
        }
        catch (OverflowException)
        {
            totalOverflowed = true;
            errors.Add(
                new DomainError(
                    "template.total_overflow",
                    "The template point total exceeds the supported 64-bit range.",
                    nameof(Questions)));
        }

        if (!totalOverflowed
            && TargetTotalPoints is not null
            && TargetTotalPoints.Value != total)
        {
            errors.Add(
                new DomainError(
                    "template.target_total_mismatch",
                    $"Configured target total {TargetTotalPoints.Value.Value} does not match question total {total.Value}.",
                    nameof(TargetTotalPoints)));
        }

        return errors.Count == 0
            ? DomainValidationResult.Valid()
            : DomainValidationResult.Invalid(errors);
    }

    public DomainResult<TemplateVersion> TryPublish(
        string publishedBy,
        DateTimeOffset publishedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedBy);
        var validation = ValidateForPublish();
        if (!validation.IsValid)
        {
            return DomainResult.Failure<TemplateVersion>(validation.Errors);
        }

        var contentHash = TemplateContentHasher.Compute(this);
        return DomainResult.Success(
            Copy(
                state: TemplateVersionState.Published,
                publishedBy: publishedBy,
                publishedAt: publishedAt,
                contentHash: contentHash));
    }

    public TemplateVersion Publish(string publishedBy, DateTimeOffset publishedAt)
    {
        var result = TryPublish(publishedBy, publishedAt);
        if (result.IsFailure)
        {
            throw new DomainValidationException(result.Errors);
        }

        return result.Value;
    }

    public TemplateVersion MarkSuperseded()
    {
        EnsureState(TemplateVersionState.Published);
        return Copy(state: TemplateVersionState.Superseded);
    }

    public TemplateVersion Retire()
    {
        if (State is not (TemplateVersionState.Published or TemplateVersionState.Superseded))
        {
            throw new InvalidDomainStateException(
                $"A template in state '{State}' cannot be retired.");
        }

        return Copy(state: TemplateVersionState.Retired);
    }

    public TemplateVersion CloneAsDraft(
        string newVersionId,
        int newVersionNumber,
        string pipelineVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineVersion);
        if (State is not (
                TemplateVersionState.Published
                or TemplateVersionState.Superseded
                or TemplateVersionState.Retired))
        {
            throw new InvalidDomainStateException(
                "Only a finalized template version can be cloned as a new draft.");
        }

        if (newVersionNumber <= VersionNumber)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "template.version_not_monotonic",
                    "A cloned version number must be greater than its base version.",
                    nameof(newVersionNumber)),
            ]);
        }

        return CreateDraft(
            newVersionId,
            TemplateId,
            newVersionNumber,
            pipelineVersion,
            _questions,
            Id,
            TargetTotalPoints,
            DefaultAllowNonKanji);
    }

    private TemplateVersion Copy(
        TemplateVersionState? state = null,
        IEnumerable<QuestionDefinition>? questions = null,
        string? aiGenerationProvenanceId = null,
        string? publishedBy = null,
        DateTimeOffset? publishedAt = null,
        string? contentHash = null,
        long? revision = null) =>
        new(
            Id,
            TemplateId,
            VersionNumber,
            state ?? State,
            questions ?? _questions,
            BasedOnVersionId,
            TargetTotalPoints,
            DefaultAllowNonKanji,
            PipelineVersion,
            aiGenerationProvenanceId ?? AiGenerationProvenanceId,
            publishedBy ?? PublishedBy,
            publishedAt ?? PublishedAt,
            contentHash ?? ContentHash,
            revision ?? Revision);

    private void EnsureEditable()
    {
        if (State != TemplateVersionState.Draft)
        {
            throw new InvalidDomainStateException(
                $"Questions cannot be edited while the template version is '{State}'.");
        }
    }

    private void EnsureState(TemplateVersionState expected)
    {
        if (State != expected)
        {
            throw new InvalidDomainStateException(
                $"Expected template state '{expected}', but found '{State}'.");
        }
    }

    private static void AddDuplicateErrors(
        List<DomainError> errors,
        IEnumerable<string> values,
        string code,
        string message)
    {
        if (values.GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            errors.Add(new DomainError(code, message, nameof(Questions)));
        }
    }

}

internal static class TemplateContentHasher
{
    public static string Compute(TemplateVersion version)
    {
        var builder = new StringBuilder();
        Add(builder, version.TemplateId);
        Add(builder, version.VersionNumber);
        Add(builder, version.TargetTotalPoints?.Value);
        Add(builder, version.DefaultAllowNonKanji);
        Add(builder, version.PipelineVersion);

        foreach (var question in version.Questions.OrderBy(question => question.OrderIndex))
        {
            Add(builder, question.Id);
            Add(builder, question.LogicalQuestionId);
            Add(builder, question.OrderIndex);
            Add(builder, question.DisplayLabel);
            Add(builder, question.QuestionText);
            Add(builder, question.QuestionType);
            Add(builder, question.GradingMode);
            Add(builder, question.MaximumPoints.Value);
            Add(builder, question.PointIncrement.Value);
            Add(builder, question.AllowNonKanji);
            Add(builder, question.RequiresReviewAlways);
            Add(builder, question.KanjiPolicyNote);

            if (question.NumericPolicy is not null)
            {
                Add(builder, question.NumericPolicy.ExpectedValue);
                Add(builder, question.NumericPolicy.Format);
                Add(builder, question.NumericPolicy.AbsoluteTolerance);
                Add(builder, question.NumericPolicy.RelativeTolerance);
                Add(builder, question.NumericPolicy.UnitRequired);
                foreach (var unit in question.NumericPolicy.AcceptedUnits)
                {
                    Add(builder, unit);
                }
            }

            if (question.ChoicePolicy is not null)
            {
                Add(builder, question.ChoicePolicy.CorrectChoice);
                foreach (var choice in question.ChoicePolicy.AllowedChoices)
                {
                    Add(builder, choice);
                }
            }

            foreach (var answer in question.AcceptedAnswers.OrderBy(answer => answer.Id))
            {
                Add(builder, answer.Id);
                Add(builder, answer.AnswerText);
                Add(builder, answer.NormalizedText);
                Add(builder, answer.VariantType);
                Add(builder, answer.Provenance);
                Add(builder, answer.Source?.SourceId);
                Add(builder, answer.Source?.SourceRole);
                Add(builder, answer.Source?.PageNumber);
                Add(builder, answer.Source?.RegionId);
            }

            foreach (var rule in question.RubricRules.OrderBy(rule => rule.OrderIndex))
            {
                Add(builder, rule.Id);
                Add(builder, rule.OrderIndex);
                Add(builder, rule.ConditionType);
                Add(builder, rule.Description);
                Add(builder, rule.Points.Value);
                Add(builder, rule.MutuallyExclusiveGroup);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void Add<T>(StringBuilder builder, T value)
    {
        var text = value?.ToString() ?? "<null>";
        builder.Append(text.Length);
        builder.Append(':');
        builder.Append(text);
        builder.Append('|');
    }
}

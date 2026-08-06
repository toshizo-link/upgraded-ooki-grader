using System.Collections.ObjectModel;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;

namespace OokiGrader.Domain.Templates;

public enum TemplateVersionState
{
    Draft,
    Generating,
    Published,
    Superseded,
    Retired,
}

public enum TemplateSourceRole
{
    BlankTest,
    ContainsModelAnswers,
    ContainsNonModelAnswers,
    SeparateAnswerKey,
}

public enum QuestionType
{
    MultipleChoice,
    Boolean,
    Numeric,
    ExactShortText,
    SemanticShortText,
    MultiPart,
    Subjective,
    Unsupported,
}

public enum GradingMode
{
    Deterministic,
    TranscribeThenRules,
    AiRubric,
    Manual,
}

public enum AcceptedAnswerVariantType
{
    Canonical,
    Equivalent,
    PhoneticException,
    Numeric,
    RegexRestricted,
    Choice,
}

public enum AnswerProvenance
{
    ProvidedModelAnswer,
    TeacherEntered,
    AiProposed,
    DerivedVariant,
}

public sealed record AnswerSourceReference
{
    public AnswerSourceReference(
        string sourceId,
        TemplateSourceRole sourceRole,
        int pageNumber,
        string? regionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);

        SourceId = sourceId;
        SourceRole = sourceRole;
        PageNumber = pageNumber;
        RegionId = string.IsNullOrWhiteSpace(regionId) ? null : regionId;
    }

    public string SourceId { get; }

    public TemplateSourceRole SourceRole { get; }

    public int PageNumber { get; }

    public string? RegionId { get; }
}

public sealed record AcceptedAnswer
{
    public AcceptedAnswer(
        string id,
        string answerText,
        AcceptedAnswerVariantType variantType,
        AnswerProvenance provenance,
        bool teacherVerified,
        AnswerSourceReference? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(answerText);

        Id = id;
        AnswerText = answerText;
        NormalizedText = JapaneseTextNormalizer.NormalizeForComparison(answerText);
        VariantType = variantType;
        Provenance = provenance;
        TeacherVerified = teacherVerified;
        Source = source;
    }

    public string Id { get; }

    public string AnswerText { get; }

    public string NormalizedText { get; }

    public AcceptedAnswerVariantType VariantType { get; }

    public AnswerProvenance Provenance { get; }

    public bool TeacherVerified { get; }

    public AnswerSourceReference? Source { get; }
}

public enum RubricConditionType
{
    ElementPresent,
    ExactPhrase,
    ModelAssessed,
    Manual,
}

public sealed record RubricRule
{
    public RubricRule(
        string id,
        int orderIndex,
        RubricConditionType conditionType,
        string description,
        MilliPoints points,
        bool teacherVerified,
        string? mutuallyExclusiveGroup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentOutOfRangeException.ThrowIfNegative(orderIndex);

        Id = id;
        OrderIndex = orderIndex;
        ConditionType = conditionType;
        Description = description;
        Points = points;
        TeacherVerified = teacherVerified;
        MutuallyExclusiveGroup = string.IsNullOrWhiteSpace(mutuallyExclusiveGroup)
            ? null
            : mutuallyExclusiveGroup;
    }

    public string Id { get; }

    public int OrderIndex { get; }

    public RubricConditionType ConditionType { get; }

    public string Description { get; }

    public MilliPoints Points { get; }

    public bool TeacherVerified { get; }

    public string? MutuallyExclusiveGroup { get; }
}

public enum NumericFormat
{
    WholeNumber,
    FixedPoint,
    Fraction,
    Scientific,
    Any,
}

public sealed class NumericAnswerPolicy
{
    private readonly ReadOnlyCollection<string> _acceptedUnits;

    public NumericAnswerPolicy(
        decimal expectedValue,
        NumericFormat format = NumericFormat.Any,
        decimal? absoluteTolerance = null,
        decimal? relativeTolerance = null,
        IEnumerable<string>? acceptedUnits = null,
        bool unitRequired = false)
    {
        if (absoluteTolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteTolerance));
        }

        if (relativeTolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(relativeTolerance));
        }

        var units = (acceptedUnits ?? [])
            .Select(unit => JapaneseTextNormalizer.NormalizeForComparison(unit))
            .Where(unit => unit.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (unitRequired && units.Length == 0)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "numeric.required_unit_missing",
                    "At least one accepted unit is required when units are mandatory."),
            ]);
        }

        ExpectedValue = expectedValue;
        Format = format;
        AbsoluteTolerance = absoluteTolerance;
        RelativeTolerance = relativeTolerance;
        _acceptedUnits = Array.AsReadOnly(units);
        UnitRequired = unitRequired;
    }

    public decimal ExpectedValue { get; }

    public NumericFormat Format { get; }

    public decimal? AbsoluteTolerance { get; }

    public decimal? RelativeTolerance { get; }

    public IReadOnlyList<string> AcceptedUnits => _acceptedUnits;

    public bool UnitRequired { get; }
}

public sealed class ChoiceAnswerPolicy
{
    private readonly ReadOnlyCollection<string> _allowedChoices;

    public ChoiceAnswerPolicy(string correctChoice, IEnumerable<string> allowedChoices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correctChoice);
        ArgumentNullException.ThrowIfNull(allowedChoices);

        CorrectChoice = JapaneseTextNormalizer.NormalizeForComparison(correctChoice);
        var choices = allowedChoices
            .Select(choice => JapaneseTextNormalizer.NormalizeForComparison(choice))
            .Where(choice => choice.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!choices.Contains(CorrectChoice, StringComparer.Ordinal))
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "choice.correct_not_allowed",
                    "The correct choice must be included in the allowed choices."),
            ]);
        }

        _allowedChoices = Array.AsReadOnly(choices);
    }

    public string CorrectChoice { get; }

    public IReadOnlyList<string> AllowedChoices => _allowedChoices;
}

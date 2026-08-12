using System.Diagnostics.CodeAnalysis;
using OokiGrader.Domain.Common;

namespace OokiGrader.Domain.Templates;

public interface ITemplateUnitPlanner
{
    IReadOnlyList<TemplateUnitPlan> Plan(TestType testType, int pageCount);
}

public sealed record TemplateUnitPlan
{
    public TemplateUnitPlan(
        int sequence,
        int firstPage,
        int lastPage,
        int? stepSetIndex,
        int? stepVariationIndex,
        string? deterministicSuffix)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstPage);
        if (lastPage < firstPage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastPage),
                "The last page cannot precede the first page.");
        }

        if ((stepSetIndex is null) != (stepVariationIndex is null))
        {
            throw new ArgumentException(
                "STEP set and variation indexes must be supplied together.");
        }

        if (stepSetIndex is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                stepSetIndex.Value,
                nameof(stepSetIndex));
            var variationIndex = stepVariationIndex.GetValueOrDefault();
            if (variationIndex is < 1 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stepVariationIndex),
                    "STEP variation index must be from 1 through 3.");
            }

            if (lastPage - firstPage != 1)
            {
                throw new ArgumentException(
                    "STEP units must contain exactly two consecutive pages.",
                    nameof(lastPage));
            }

            var requiredSuffix = $"-{variationIndex}";
            if (!string.Equals(
                    deterministicSuffix,
                    requiredSuffix,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "STEP suffix must match the variation index.",
                    nameof(deterministicSuffix));
            }
        }
        else if (deterministicSuffix is not null)
        {
            throw new ArgumentException(
                "Non-STEP units cannot have a deterministic suffix.",
                nameof(deterministicSuffix));
        }

        Sequence = sequence;
        FirstPage = firstPage;
        LastPage = lastPage;
        StepSetIndex = stepSetIndex;
        StepVariationIndex = stepVariationIndex;
        DeterministicSuffix = deterministicSuffix;
    }

    public int Sequence { get; }

    public int FirstPage { get; }

    public int LastPage { get; }

    public int? StepSetIndex { get; }

    public int? StepVariationIndex { get; }

    public string? DeterministicSuffix { get; }

    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The name is part of the specified planning vocabulary.")]
    public static TemplateUnitPlan Single(int firstPage, int lastPage) =>
        new(
            sequence: 1,
            firstPage,
            lastPage,
            stepSetIndex: null,
            stepVariationIndex: null,
            deterministicSuffix: null);
}

public sealed class TemplateUnitPlanner : ITemplateUnitPlanner
{
    public IReadOnlyList<TemplateUnitPlan> Plan(
        TestType testType,
        int pageCount)
    {
        if (pageCount <= 0)
        {
            throw Validation(
                "PDF_PAGE_COUNT_INVALID",
                "PDF has no pages.",
                "pageCount");
        }

        return testType switch
        {
            TestType.Hop => PlanHop(pageCount),
            TestType.Step => PlanStep(pageCount),
            TestType.ClassPlacement or TestType.Other =>
                [TemplateUnitPlan.Single(1, pageCount)],
            _ => throw new ArgumentOutOfRangeException(nameof(testType)),
        };
    }

    private static List<TemplateUnitPlan> PlanHop(int pageCount)
    {
        var units = new List<TemplateUnitPlan>(pageCount);
        for (var page = 1; page <= pageCount; page++)
        {
            units.Add(new TemplateUnitPlan(
                sequence: page,
                firstPage: page,
                lastPage: page,
                stepSetIndex: null,
                stepVariationIndex: null,
                deterministicSuffix: null));
        }

        return units;
    }

    private static List<TemplateUnitPlan> PlanStep(int pageCount)
    {
        if (pageCount % 6 != 0)
        {
            throw Validation(
                "STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX",
                "STEP PDF page count must be divisible by six.",
                "pageCount");
        }

        var units = new List<TemplateUnitPlan>(pageCount / 2);
        var sequence = 0;
        for (var setIndex = 1; setIndex <= pageCount / 6; setIndex++)
        {
            var setStart = checked(((setIndex - 1) * 6) + 1);
            for (var variationIndex = 1; variationIndex <= 3; variationIndex++)
            {
                sequence++;
                var firstPage = checked(
                    setStart + ((variationIndex - 1) * 2));
                units.Add(new TemplateUnitPlan(
                    sequence,
                    firstPage,
                    checked(firstPage + 1),
                    setIndex,
                    variationIndex,
                    $"-{variationIndex}"));
            }
        }

        return units;
    }

    private static DomainValidationException Validation(
        string code,
        string message,
        string path) =>
        new([new DomainError(code, message, path)]);
}

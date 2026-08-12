using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Application.Templates;

public sealed record StepNameConsistencyResult(
    string? BaseName,
    bool RequiresUserConfirmation,
    IReadOnlyList<string> WarningCodes)
{
    public bool IsConsistent => !RequiresUserConfirmation;

    public string? ErrorCode => WarningCodes.Count == 0 ? null : WarningCodes[0];
}

public static class TemplateNamePolicy
{
    public const int MaximumTitleLength = 500;
    public const string NameRequiredErrorCode = "TEST_NAME_REQUIRED";
    public const string StepNameMismatchErrorCode = "STEP_NAME_MISMATCH";
    public const string StepNameAlreadySuffixedErrorCode =
        "STEP_NAME_ALREADY_SUFFIXED";
    public const string DuplicateNameErrorCode = "DUPLICATE_TEMPLATE_NAME";
    public const string KnownTestNameImmutableErrorCode =
        "KNOWN_TEST_NAME_IMMUTABLE";

    private static readonly Regex RecognizedStepSuffix = new(
        @"-[1-3]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string NormalizePrintedName(string? printedName)
    {
        var normalized = NormalizeOrNull(printedName);
        if (normalized is null)
        {
            throw Validation(
                NameRequiredErrorCode,
                "A test name is required.",
                "printedName");
        }

        return normalized;
    }

    public static string CreateFinalName(
        TestType testType,
        string? baseName,
        int? stepVariationIndex = null)
    {
        if (!Enum.IsDefined(testType))
        {
            throw new ArgumentOutOfRangeException(nameof(testType));
        }

        if (testType is TestType.Step)
        {
            if (stepVariationIndex is null)
            {
                throw Validation(
                    "STEP_VARIATION_REQUIRED",
                    "STEP final names require a variation index.",
                    "stepVariationIndex");
            }

            return AppendStepSuffix(baseName, stepVariationIndex.Value);
        }

        if (stepVariationIndex is not null)
        {
            throw Validation(
                "STEP_VARIATION_NOT_ALLOWED",
                "Only STEP final names may have a variation index.",
                "stepVariationIndex");
        }

        return NormalizePrintedName(baseName);
    }

    /// <summary>
    /// Builds the authoritative title for a test type whose identity is already
    /// known from the teacher-selected batch settings and deterministic split.
    /// The AI-extracted paper title is deliberately not an input.
    /// </summary>
    public static string CreateKnownTestName(
        TestType testType,
        string? subject,
        GradeLevel grade,
        int unitSequence,
        int? stepSetIndex = null,
        int? stepVariationIndex = null)
    {
        if (testType == TestType.Other)
        {
            throw new ArgumentException(
                "Other tests use the paper title instead of a deterministic title.",
                nameof(testType));
        }

        if (!Enum.IsDefined(testType))
        {
            throw new ArgumentOutOfRangeException(nameof(testType));
        }

        if (grade is < GradeLevel.Grade1 or > GradeLevel.Grade6)
        {
            throw Validation(
                GradeResolutionService.RequiredErrorCode,
                "A resolved grade is required for deterministic test names.",
                "grade");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitSequence);
        var normalizedSubject = NormalizePrintedName(subject);
        var prefix = $"{normalizedSubject}{(int)grade}年";
        var name = testType switch
        {
            TestType.Hop when stepSetIndex is null
                && stepVariationIndex is null =>
                $"{prefix}HOP{unitSequence}",
            TestType.Step when stepSetIndex is > 0
                && stepVariationIndex is >= 1 and <= 3 =>
                $"{prefix}STEPセット{stepSetIndex}-{stepVariationIndex}",
            TestType.ClassPlacement when stepSetIndex is null
                && stepVariationIndex is null =>
                $"{prefix}クラス分けテスト",
            TestType.Step => throw new ArgumentException(
                "STEP names require a positive set index and a variation from 1 through 3."),
            _ => throw new ArgumentException(
                "Non-STEP names cannot contain STEP set metadata."),
        };

        return NormalizePrintedName(name);
    }

    public static string AppendStepSuffix(
        string? baseName,
        int variationIndex)
    {
        if (variationIndex is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(variationIndex),
                "STEP variation index must be from 1 through 3.");
        }

        var normalized = NormalizePrintedName(baseName);
        if (RecognizedStepSuffix.IsMatch(normalized))
        {
            throw Validation(
                StepNameAlreadySuffixedErrorCode,
                "STEP base name already contains a variation suffix.",
                "baseName");
        }

        var suffix = $"-{variationIndex}";
        var boundedBase = Truncate(normalized, MaximumTitleLength - suffix.Length)
            .TrimEnd();
        if (boundedBase.Length == 0)
        {
            throw Validation(
                NameRequiredErrorCode,
                "A STEP base name is required.",
                "baseName");
        }

        return boundedBase + suffix;
    }

    public static StepNameConsistencyResult EvaluateStepSetBaseNames(
        IEnumerable<string?> printedBaseNames)
    {
        ArgumentNullException.ThrowIfNull(printedBaseNames);
        var inputNames = printedBaseNames.ToArray();
        if (inputNames.Length != 3)
        {
            throw new ArgumentException(
                "A STEP set must contain exactly three base names.",
                nameof(printedBaseNames));
        }

        var normalizedNames = inputNames
            .Select(NormalizeOrNull)
            .ToArray();
        var warningCodes = new List<string>(2);
        if (normalizedNames.Any(name => name is null))
        {
            warningCodes.Add(NameRequiredErrorCode);
        }

        if (normalizedNames
            .Where(name => name is not null)
            .Any(name => RecognizedStepSuffix.IsMatch(name!)))
        {
            warningCodes.Add(StepNameAlreadySuffixedErrorCode);
        }

        var presentNames = normalizedNames
            .Where(name => name is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (presentNames.Length > 1)
        {
            warningCodes.Add(StepNameMismatchErrorCode);
        }

        if (warningCodes.Count > 0)
        {
            return new StepNameConsistencyResult(
                BaseName: null,
                RequiresUserConfirmation: true,
                warningCodes);
        }

        return new StepNameConsistencyResult(
            presentNames.Single(),
            RequiresUserConfirmation: false,
            WarningCodes: []);
    }

    public static IReadOnlyList<string> FindDuplicateNames(
        IEnumerable<string?> finalNames)
    {
        ArgumentNullException.ThrowIfNull(finalNames);
        return finalNames
            .Select(NormalizePrintedName)
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static void EnsureUniqueFinalNames(IEnumerable<string?> finalNames)
    {
        var duplicates = FindDuplicateNames(finalNames);
        if (duplicates.Count > 0)
        {
            throw Validation(
                DuplicateNameErrorCode,
                "Final template names must be unique.",
                "finalNames");
        }
    }

    private static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var source = value.Normalize(NormalizationForm.FormKC);
        var target = new StringBuilder(source.Length);
        var whitespacePending = false;
        foreach (var rune in source.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                whitespacePending = target.Length > 0;
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control)
            {
                continue;
            }

            if (whitespacePending)
            {
                target.Append(' ');
                whitespacePending = false;
            }

            target.Append(rune);
        }

        if (target.Length == 0)
        {
            return null;
        }

        return Truncate(target.ToString(), MaximumTitleLength).TrimEnd();
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var length = maximumLength;
        if (length > 0
            && char.IsHighSurrogate(value[length - 1])
            && length < value.Length
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }

    private static DomainValidationException Validation(
        string code,
        string message,
        string path) =>
        new([new DomainError(code, message, path)]);
}

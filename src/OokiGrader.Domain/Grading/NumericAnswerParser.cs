using System.Globalization;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Grading;

internal enum NumericParseFailure
{
    None,
    InvalidFormat,
    UnitMissingOrInvalid,
}

internal readonly record struct NumericParseResult(
    bool Success,
    decimal Value,
    NumericParseFailure Failure);

internal static class NumericAnswerParser
{
    public static NumericParseResult Parse(string input, NumericAnswerPolicy policy)
    {
        var normalized = JapaneseTextNormalizer.NormalizeForComparison(input);
        var numericPortion = normalized;

        if (policy.AcceptedUnits.Count > 0)
        {
            var matchedUnit = policy.AcceptedUnits
                .OrderByDescending(unit => unit.Length)
                .FirstOrDefault(unit =>
                    normalized.EndsWith(unit, StringComparison.Ordinal));

            if (matchedUnit is null)
            {
                if (policy.UnitRequired)
                {
                    return new NumericParseResult(
                        false,
                        0,
                        NumericParseFailure.UnitMissingOrInvalid);
                }
            }
            else
            {
                numericPortion = normalized[..^matchedUnit.Length].TrimEnd();
            }
        }

        if (numericPortion.Length == 0)
        {
            return new NumericParseResult(false, 0, NumericParseFailure.InvalidFormat);
        }

        return policy.Format switch
        {
            NumericFormat.WholeNumber => ParseInteger(numericPortion),
            NumericFormat.FixedPoint => ParseDecimal(numericPortion, allowExponent: false),
            NumericFormat.Fraction => ParseFraction(numericPortion),
            NumericFormat.Scientific => ParseDecimal(numericPortion, allowExponent: true),
            NumericFormat.Any => ParseAny(numericPortion),
            _ => new NumericParseResult(false, 0, NumericParseFailure.InvalidFormat),
        };
    }

    public static bool Matches(decimal actual, NumericAnswerPolicy policy)
    {
        var difference = Math.Abs(actual - policy.ExpectedValue);
        if (difference == 0)
        {
            return true;
        }

        if (policy.AbsoluteTolerance is not null
            && difference <= policy.AbsoluteTolerance.Value)
        {
            return true;
        }

        if (policy.RelativeTolerance is not null)
        {
            var permitted = Math.Abs(policy.ExpectedValue) * policy.RelativeTolerance.Value;
            return difference <= permitted;
        }

        return false;
    }

    private static NumericParseResult ParseAny(string value)
    {
        if (value.Contains('/', StringComparison.Ordinal))
        {
            return ParseFraction(value);
        }

        return ParseDecimal(value, allowExponent: true);
    }

    private static NumericParseResult ParseInteger(string value)
    {
        var result = ParseDecimal(value, allowExponent: false);
        return result.Success && result.Value == decimal.Truncate(result.Value)
            ? result
            : new NumericParseResult(false, 0, NumericParseFailure.InvalidFormat);
    }

    private static NumericParseResult ParseDecimal(string value, bool allowExponent)
    {
        var styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (allowExponent)
        {
            styles |= NumberStyles.AllowExponent;
        }

        return decimal.TryParse(value, styles, CultureInfo.InvariantCulture, out var parsed)
            ? new NumericParseResult(true, parsed, NumericParseFailure.None)
            : new NumericParseResult(false, 0, NumericParseFailure.InvalidFormat);
    }

    private static NumericParseResult ParseFraction(string value)
    {
        var pieces = value.Split('/');
        if (pieces.Length != 2
            || !decimal.TryParse(
                pieces[0].Trim(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var numerator)
            || !decimal.TryParse(
                pieces[1].Trim(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var denominator)
            || denominator == 0)
        {
            return new NumericParseResult(false, 0, NumericParseFailure.InvalidFormat);
        }

        try
        {
            return new NumericParseResult(
                true,
                numerator / denominator,
                NumericParseFailure.None);
        }
        catch (OverflowException)
        {
            return new NumericParseResult(false, 0, NumericParseFailure.InvalidFormat);
        }
    }
}

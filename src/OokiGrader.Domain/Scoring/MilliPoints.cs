using OokiGrader.Domain.Common;

namespace OokiGrader.Domain.Scoring;

public readonly record struct MilliPoints : IComparable<MilliPoints>
{
    public static readonly MilliPoints Zero = new(0);

    public MilliPoints(long value)
    {
        if (value < 0)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "points.negative",
                    "Milli-points cannot be negative.",
                    nameof(value)),
            ]);
        }

        Value = value;
    }

    public long Value { get; }

    public decimal ToPoints() => Value / 1000m;

    public static MilliPoints FromPoints(decimal points)
    {
        if (points < 0)
        {
            throw new DomainValidationException(
            [
                new DomainError("points.negative", "Points cannot be negative.", nameof(points)),
            ]);
        }

        var milli = checked(points * 1000m);
        if (milli != decimal.Truncate(milli))
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "points.precision",
                    "Points must be representable in thousandths.",
                    nameof(points)),
            ]);
        }

        return new MilliPoints(checked((long)milli));
    }

    public int CompareTo(MilliPoints other) => Value.CompareTo(other.Value);

    public static MilliPoints operator +(MilliPoints left, MilliPoints right) =>
        new(checked(left.Value + right.Value));

    public static MilliPoints operator -(MilliPoints left, MilliPoints right)
    {
        if (right.Value > left.Value)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "points.negative_result",
                    "Subtracting these point values would produce a negative result."),
            ]);
        }

        return new MilliPoints(left.Value - right.Value);
    }

    public static bool operator <(MilliPoints left, MilliPoints right) =>
        left.Value < right.Value;

    public static bool operator >(MilliPoints left, MilliPoints right) =>
        left.Value > right.Value;

    public static bool operator <=(MilliPoints left, MilliPoints right) =>
        left.Value <= right.Value;

    public static bool operator >=(MilliPoints left, MilliPoints right) =>
        left.Value >= right.Value;

    public override string ToString() => ToPoints().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class PointAwardPolicy
{
    public PointAwardPolicy(MilliPoints maximum, MilliPoints increment)
    {
        if (maximum == MilliPoints.Zero)
        {
            throw Validation("points.maximum_zero", "Maximum points must be greater than zero.");
        }

        if (increment == MilliPoints.Zero)
        {
            throw Validation("points.increment_zero", "Point increment must be greater than zero.");
        }

        if (increment > maximum)
        {
            throw Validation(
                "points.increment_above_maximum",
                "Point increment cannot exceed maximum points.");
        }

        if (maximum.Value % increment.Value != 0)
        {
            throw Validation(
                "points.maximum_not_increment",
                "Maximum points must be an allowed increment.");
        }

        Maximum = maximum;
        Increment = increment;
    }

    public MilliPoints Maximum { get; }

    public MilliPoints Increment { get; }

    public DomainValidationResult ValidateAward(MilliPoints award, string? path = null)
    {
        var errors = new List<DomainError>();

        if (award > Maximum)
        {
            errors.Add(
                new DomainError(
                    "points.above_maximum",
                    $"Awarded points {award.Value} exceed maximum {Maximum.Value}.",
                    path));
        }

        if (award.Value % Increment.Value != 0)
        {
            errors.Add(
                new DomainError(
                    "points.invalid_increment",
                    $"Awarded points must be a multiple of {Increment.Value} milli-points.",
                    path));
        }

        return errors.Count == 0
            ? DomainValidationResult.Valid()
            : DomainValidationResult.Invalid(errors);
    }

    private static DomainValidationException Validation(string code, string message) =>
        new([new DomainError(code, message)]);
}

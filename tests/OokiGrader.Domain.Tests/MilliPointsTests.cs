using OokiGrader.Domain.Common;
using OokiGrader.Domain.Scoring;

namespace OokiGrader.Domain.Tests;

public sealed class MilliPointsTests
{
    [Fact]
    public void UsesExactIntegerThousandths()
    {
        var points = MilliPoints.FromPoints(1.125m);

        Assert.Equal(1125, points.Value);
        Assert.Equal(1.125m, points.ToPoints());
    }

    [Fact]
    public void RejectsPrecisionBelowOneMilliPoint()
    {
        Assert.Throws<DomainValidationException>(
            () => MilliPoints.FromPoints(1.0001m));
    }

    [Fact]
    public void RejectsNegativePoints()
    {
        Assert.Throws<DomainValidationException>(() => new MilliPoints(-1));
    }

    [Fact]
    public void PointPolicyRejectsZeroMaximum()
    {
        Assert.Throws<DomainValidationException>(
            () => new PointAwardPolicy(MilliPoints.Zero, new MilliPoints(1)));
    }

    [Fact]
    public void PointPolicyRejectsAwardAboveMaximum()
    {
        var policy = new PointAwardPolicy(new MilliPoints(2000), new MilliPoints(500));

        var result = policy.ValidateAward(new MilliPoints(2500));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "points.above_maximum");
    }

    [Fact]
    public void PointPolicyRejectsDisallowedIncrement()
    {
        var policy = new PointAwardPolicy(new MilliPoints(2000), new MilliPoints(500));

        var result = policy.ValidateAward(new MilliPoints(1250));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "points.invalid_increment");
    }

    [Fact]
    public void AdditionUsesCheckedInt64Arithmetic()
    {
        Assert.Throws<OverflowException>(
            () => _ = new MilliPoints(long.MaxValue) + new MilliPoints(1));
    }
}

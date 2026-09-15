using OokiGrader.SchoolManager.PassFail;

namespace OokiGrader.SchoolManager.Tests;

public sealed class PassFailTableProjectorTests
{
    [Fact]
    public void ProjectKeepsOnlyTargetClassAndRedactsEveryIdentifier()
    {
        var source = new PassFailSourceTable(
            "小６ 社会暗記テスト合格表",
            ["地理分野不合格数", "第1回", "第2回"],
            [
                new("10000001", "小6", "A", ["0", "○", "●"]),
                new("10000002", "小6", "A", ["1", "●", "○"]),
                new("10000003", "小6", "B", ["0", "○", "○"]),
                new("10000004", "中1", "A", ["0", "○", "○"]),
            ],
            ["10000001", "10000002", "10000003", "10000004", "山田太郎"]);

        var result = PassFailTableProjector.Project(
            source,
            targetStudentNumber: "10000002",
            targetGradeLabel: "小学6年",
            targetClassLabel: "A");

        Assert.Equal("合否表（小6）", result.Title);
        Assert.Equal("小6", result.GradeLabel);
        Assert.Equal("A", result.ClassLabel);
        Assert.Equal(["同級生01", "本人"], result.Rows.Select(row => row.Label));
        Assert.DoesNotContain("10000001", result.CanonicalText);
        Assert.DoesNotContain("10000002", result.CanonicalText);
        Assert.DoesNotContain("10000003", result.CanonicalText);
        Assert.DoesNotContain(result.Rows, row => row.Values.Contains("B"));
    }

    [Fact]
    public void ProjectRejectsMissingOrAmbiguousTarget()
    {
        var source = new PassFailSourceTable(
            "小６ 合否表",
            ["第1回"],
            [
                new("10000001", "小6", "A", ["○"]),
                new("10000001", "小6", "A", ["●"]),
            ],
            ["10000001"]);

        Assert.Throws<PassFailProjectionException>(() =>
            PassFailTableProjector.Project(source, "99999999", "小6", "A"));
        Assert.Throws<PassFailProjectionException>(() =>
            PassFailTableProjector.Project(source, "10000001", "小6", "A"));
    }

    [Fact]
    public void ProjectRejectsSensitiveDataInDistributableCells()
    {
        var source = new PassFailSourceTable(
            "小６ 合否表",
            ["第1回"],
            [new("10000001", "小6", "A", ["山田太郎"])],
            ["10000001", "山田太郎"]);

        var exception = Assert.Throws<PassFailProjectionException>(() =>
            PassFailTableProjector.Project(source, "10000001", "小6", "A"));

        Assert.Equal("pass_fail_privacy_validation_failed", exception.Code);
    }

    [Theory]
    [InlineData("小学６年", "小6")]
    [InlineData("中学校1年", "中1")]
    [InlineData("高校３年", "高3")]
    public void NormalizeGradeCanonicalizesSupportedLabels(
        string value,
        string expected)
    {
        Assert.Equal(expected, PassFailGrade.Normalize(value));
    }
}

using System.Text.Json;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Tests;

public sealed class OrderedScanAssemblyPlannerTests
{
    private readonly OrderedScanAssemblyPlanner _planner = new();

    [Theory]
    [InlineData(TestType.Hop, 9, 1)]
    [InlineData(TestType.Step, 6, 2)]
    [InlineData(TestType.ClassPlacement, 3, 3)]
    [InlineData(TestType.ClassPlacement, 7, 7)]
    [InlineData(TestType.Other, 4, 4)]
    [InlineData(TestType.Other, 12, 12)]
    public void PageCountPolicyCoversEveryTestTypeAndArbitraryWholeTests(
        TestType testType,
        int templatePageCount,
        int expectedPageCount)
    {
        Assert.Equal(
            expectedPageCount,
            OrderedScanPageCountPolicy.Resolve(testType, templatePageCount));
    }

    [Fact]
    public void HopCreatesOneSubmissionForEveryPage()
    {
        var plan = _planner.Plan(1, Observations(1, 1, 1));

        Assert.True(plan.CanFinalizeAutomatically);
        Assert.Equal(3, plan.Groups.Count);
        Assert.All(plan.Groups, group =>
        {
            Assert.True(group.IsComplete);
            Assert.Single(group.Pages);
            Assert.Equal(1, group.Pages[0].TemplatePageNumber);
        });
    }

    [Fact]
    public void StepCreatesIndependentConsecutiveTwoPageSubmissions()
    {
        var plan = _planner.Plan(2, Observations(1, 2, 1, 2, 1, 2));

        Assert.True(plan.CanFinalizeAutomatically);
        Assert.Collection(
            plan.Groups,
            group => AssertGroup(group, 1, 1, 2),
            group => AssertGroup(group, 2, 3, 4),
            group => AssertGroup(group, 3, 5, 6));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void OtherAndClassPlacementSupportAnyPositivePageCount(int pageCount)
    {
        var pageNumbers = Enumerable.Range(1, pageCount)
            .Concat(Enumerable.Range(1, pageCount))
            .ToArray();

        var plan = _planner.Plan(pageCount, Observations(pageNumbers));

        Assert.True(plan.CanFinalizeAutomatically);
        Assert.Equal(2, plan.Groups.Count);
        Assert.All(plan.Groups, group =>
            Assert.Equal(
                Enumerable.Range(1, pageCount),
                group.Pages.Select(page => page.TemplatePageNumber!.Value)));
    }

    [Fact]
    public void NewPageOneResynchronizesAfterAnIncompleteStepSubmission()
    {
        var plan = _planner.Plan(2, Observations(1, 1, 2));

        Assert.False(plan.CanFinalizeAutomatically);
        Assert.Collection(
            plan.Groups,
            group =>
            {
                Assert.Equal(OrderedScanGroupStatus.NeedsReview, group.Status);
                Assert.Equal([2], group.MissingTemplatePageNumbers);
                Assert.Equal([1], group.Pages.Select(page => page.InputOrdinal));
            },
            group => AssertGroup(group, 2, 2, 3));
        Assert.Contains(
            plan.Issues,
            issue => issue.Code
                == OrderedScanAssemblyIssueCodes.MissingTemplatePage
                && issue.GroupOrdinal == 1);
    }

    [Fact]
    public void OrphanedLeadingPageDoesNotShiftLaterGroups()
    {
        var plan = _planner.Plan(2, Observations(2, 1, 2));

        var group = Assert.Single(plan.Groups);
        AssertGroup(group, 1, 2, 3);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == OrderedScanAssemblyIssueCodes.OrphanPage
                && issue.InputOrdinal == 1);
    }

    [Fact]
    public void DuplicateTemplatePageRequiresReviewAndNextPageOneResynchronizes()
    {
        var plan = _planner.Plan(3, Observations(1, 2, 2, 1, 2, 3));

        Assert.Collection(
            plan.Groups,
            first => Assert.Equal(OrderedScanGroupStatus.NeedsReview, first.Status),
            second => AssertGroup(second, 2, 4, 5, 6));
        Assert.Contains(
            plan.Issues,
            issue => issue.Code
                == OrderedScanAssemblyIssueCodes.DuplicateTemplatePage
                && issue.GroupOrdinal == 1);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code
                == OrderedScanAssemblyIssueCodes.MissingTemplatePage
                && issue.ExpectedTemplatePageNumber == 3);
    }

    [Fact]
    public void CompleteButMisorderedGroupRequiresReview()
    {
        var plan = _planner.Plan(4, Observations(1, 3, 2, 4));

        Assert.Equal(OrderedScanGroupStatus.NeedsReview, Assert.Single(plan.Groups).Status);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == OrderedScanAssemblyIssueCodes.PagesOutOfOrder);
    }

    [Fact]
    public void UnclassifiedPageRequiresReviewWithoutPreventingPageOneResync()
    {
        var plan = _planner.Plan(
            2,
            [
                new OrderedScanPageObservation(1, 1),
                new OrderedScanPageObservation(2, null),
                new OrderedScanPageObservation(3, 1),
                new OrderedScanPageObservation(4, 2),
            ]);

        Assert.Equal(OrderedScanGroupStatus.NeedsReview, plan.Groups[0].Status);
        Assert.True(plan.Groups[1].IsComplete);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == OrderedScanAssemblyIssueCodes.UnclassifiedPage);
    }

    [Fact]
    public void OrdinalGapAndDuplicateAreExplicitIssues()
    {
        var plan = _planner.Plan(
            2,
            [
                new OrderedScanPageObservation(1, 1),
                new OrderedScanPageObservation(3, 2),
                new OrderedScanPageObservation(3, 2),
            ]);

        Assert.False(plan.CanFinalizeAutomatically);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == OrderedScanAssemblyIssueCodes.InputOrdinalGap);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code
                == OrderedScanAssemblyIssueCodes.DuplicateInputOrdinal);
    }

    [Fact]
    public void PlannerRejectsUnsortedObservations()
    {
        var exception = Assert.Throws<DomainValidationException>(() => _planner.Plan(
            2,
            [
                new OrderedScanPageObservation(2, 1),
                new OrderedScanPageObservation(1, 2),
            ]));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "ORDERED_SCAN_INPUT_NOT_SORTED");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PlannerRejectsNonPositiveExpectedPageCount(int expectedPageCount)
    {
        Assert.Throws<DomainValidationException>(
            () => _planner.Plan(expectedPageCount, []));
    }

    [Theory]
    [InlineData(OrderedScanBatchStatus.Draft, "\"draft\"")]
    [InlineData(OrderedScanBatchStatus.NeedsReview, "\"needsReview\"")]
    [InlineData(OrderedScanBatchStatus.Expired, "\"expired\"")]
    public void BatchStatusUsesStableApiStrings(
        OrderedScanBatchStatus status,
        string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(status));
    }

    [Theory]
    [InlineData(OrderedScanItemStatus.Pending, "\"pending\"")]
    [InlineData(OrderedScanItemStatus.NeedsReview, "\"needsReview\"")]
    [InlineData(OrderedScanItemStatus.Grouped, "\"grouped\"")]
    public void ItemStatusUsesStableApiStrings(
        OrderedScanItemStatus status,
        string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(status));
    }

    private static OrderedScanPageObservation[] Observations(
        params int[] pageNumbers) =>
        pageNumbers
            .Select((pageNumber, index) =>
                new OrderedScanPageObservation(index + 1, pageNumber))
            .ToArray();

    private static void AssertGroup(
        OrderedScanGroupPlan group,
        int groupOrdinal,
        params int[] inputOrdinals)
    {
        Assert.True(group.IsComplete);
        Assert.Equal(groupOrdinal, group.GroupOrdinal);
        Assert.Empty(group.MissingTemplatePageNumbers);
        Assert.Equal(inputOrdinals, group.Pages.Select(page => page.InputOrdinal));
    }
}

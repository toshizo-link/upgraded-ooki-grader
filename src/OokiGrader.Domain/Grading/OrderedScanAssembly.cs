using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Grading;

public static class OrderedScanPageCountPolicy
{
    public static int Resolve(TestType testType, int templatePageCount)
    {
        if (templatePageCount <= 0)
        {
            throw Validation(
                "ORDERED_SCAN_TEMPLATE_PAGE_COUNT_INVALID",
                "The published template must contain at least one page.",
                "templatePageCount");
        }

        return testType switch
        {
            TestType.Hop => 1,
            TestType.Step => 2,
            TestType.ClassPlacement or TestType.Other => templatePageCount,
            _ => throw new ArgumentOutOfRangeException(nameof(testType)),
        };
    }

    private static DomainValidationException Validation(
        string code,
        string message,
        string path) =>
        new([new DomainError(code, message, path)]);
}

[JsonConverter(typeof(JsonStringEnumConverter<OrderedScanBatchStatus>))]
public enum OrderedScanBatchStatus
{
    [JsonStringEnumMemberName("draft")]
    Draft = 0,

    [JsonStringEnumMemberName("processing")]
    Processing = 1,

    [JsonStringEnumMemberName("completed")]
    Completed = 2,

    [JsonStringEnumMemberName("needsReview")]
    NeedsReview = 3,

    [JsonStringEnumMemberName("failed")]
    Failed = 4,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled = 5,

    [JsonStringEnumMemberName("expired")]
    Expired = 6,
}

[JsonConverter(typeof(JsonStringEnumConverter<OrderedScanItemStatus>))]
public enum OrderedScanItemStatus
{
    [JsonStringEnumMemberName("pending")]
    Pending = 0,

    [JsonStringEnumMemberName("uploaded")]
    Uploaded = 1,

    [JsonStringEnumMemberName("classified")]
    Classified = 2,

    [JsonStringEnumMemberName("grouped")]
    Grouped = 3,

    [JsonStringEnumMemberName("needsReview")]
    NeedsReview = 4,

    [JsonStringEnumMemberName("rejected")]
    Rejected = 5,
}

[JsonConverter(typeof(JsonStringEnumConverter<OrderedScanGroupStatus>))]
public enum OrderedScanGroupStatus
{
    [JsonStringEnumMemberName("complete")]
    Complete = 1,

    [JsonStringEnumMemberName("needsReview")]
    NeedsReview = 2,
}

public static class OrderedScanAssemblyIssueCodes
{
    public const string EmptyBatch = "ORDERED_SCAN_BATCH_EMPTY";
    public const string InputOrdinalGap = "ORDERED_SCAN_INPUT_ORDINAL_GAP";
    public const string DuplicateInputOrdinal =
        "ORDERED_SCAN_INPUT_ORDINAL_DUPLICATE";
    public const string UnclassifiedPage = "ORDERED_SCAN_PAGE_UNCLASSIFIED";
    public const string OrphanPage = "ORDERED_SCAN_PAGE_ORPHANED";
    public const string InvalidTemplatePage =
        "ORDERED_SCAN_TEMPLATE_PAGE_INVALID";
    public const string DuplicateTemplatePage =
        "ORDERED_SCAN_TEMPLATE_PAGE_DUPLICATE";
    public const string PagesOutOfOrder = "ORDERED_SCAN_PAGES_OUT_OF_ORDER";
    public const string MissingTemplatePage =
        "ORDERED_SCAN_TEMPLATE_PAGE_MISSING";
}

public sealed record OrderedScanPageObservation(
    int InputOrdinal,
    int? DetectedTemplatePageNumber);

public sealed record OrderedScanPagePlacement(
    int InputOrdinal,
    int? TemplatePageNumber);

public sealed record OrderedScanAssemblyIssue(
    string Code,
    int? InputOrdinal,
    int? GroupOrdinal,
    int? ExpectedTemplatePageNumber,
    int? ActualTemplatePageNumber,
    string Message);

public sealed record OrderedScanGroupPlan
{
    public OrderedScanGroupPlan(
        int groupOrdinal,
        OrderedScanGroupStatus status,
        IEnumerable<OrderedScanPagePlacement> pages,
        IEnumerable<int> missingTemplatePageNumbers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupOrdinal);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(missingTemplatePageNumbers);

        GroupOrdinal = groupOrdinal;
        Status = status;
        Pages = Array.AsReadOnly(pages.ToArray());
        MissingTemplatePageNumbers = Array.AsReadOnly(
            missingTemplatePageNumbers.ToArray());
    }

    public int GroupOrdinal { get; }

    public OrderedScanGroupStatus Status { get; }

    public IReadOnlyList<OrderedScanPagePlacement> Pages { get; }

    public IReadOnlyList<int> MissingTemplatePageNumbers { get; }

    public bool IsComplete => Status == OrderedScanGroupStatus.Complete;
}

public sealed record OrderedScanAssemblyPlan
{
    public OrderedScanAssemblyPlan(
        int expectedPageCount,
        IEnumerable<OrderedScanGroupPlan> groups,
        IEnumerable<OrderedScanAssemblyIssue> issues)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPageCount);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(issues);

        ExpectedPageCount = expectedPageCount;
        Groups = Array.AsReadOnly(groups.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public int ExpectedPageCount { get; }

    public IReadOnlyList<OrderedScanGroupPlan> Groups { get; }

    public IReadOnlyList<OrderedScanAssemblyIssue> Issues { get; }

    public bool CanFinalizeAutomatically =>
        Groups.Count > 0
        && Groups.All(group => group.IsComplete)
        && Issues.Count == 0;
}

public interface IOrderedScanAssemblyPlanner
{
    OrderedScanAssemblyPlan Plan(
        int expectedPageCount,
        IReadOnlyList<OrderedScanPageObservation> observations);
}

/// <summary>
/// Deterministically groups ordered, single-page scans. A confidently detected
/// template page 1 is the only automatic group boundary, so an incomplete group
/// cannot shift every subsequent student's pages.
/// </summary>
public sealed class OrderedScanAssemblyPlanner : IOrderedScanAssemblyPlanner
{
    public const string CurrentPolicyVersion = "ordered-single-page-scan-v1";

    public OrderedScanAssemblyPlan Plan(
        int expectedPageCount,
        IReadOnlyList<OrderedScanPageObservation> observations)
    {
        if (expectedPageCount <= 0)
        {
            throw Validation(
                "ORDERED_SCAN_EXPECTED_PAGE_COUNT_INVALID",
                "Expected submission page count must be positive.",
                "expectedPageCount");
        }

        ArgumentNullException.ThrowIfNull(observations);

        var groups = new List<OrderedScanGroupPlan>();
        var issues = new List<OrderedScanAssemblyIssue>();
        GroupBuilder? current = null;
        var nextGroupOrdinal = 1;
        var previousInputOrdinal = 0;

        if (observations.Count == 0)
        {
            issues.Add(new OrderedScanAssemblyIssue(
                OrderedScanAssemblyIssueCodes.EmptyBatch,
                InputOrdinal: null,
                GroupOrdinal: null,
                ExpectedTemplatePageNumber: null,
                ActualTemplatePageNumber: null,
                "The ordered scan batch contains no pages."));
        }

        foreach (var observation in observations)
        {
            if (observation.InputOrdinal <= 0)
            {
                throw Validation(
                    "ORDERED_SCAN_INPUT_ORDINAL_INVALID",
                    "Input ordinals must be positive.",
                    "observations.inputOrdinal");
            }

            if (observation.InputOrdinal < previousInputOrdinal)
            {
                throw Validation(
                    "ORDERED_SCAN_INPUT_NOT_SORTED",
                    "Observations must be supplied in ascending input-ordinal order.",
                    "observations");
            }

            if (observation.InputOrdinal == previousInputOrdinal)
            {
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.DuplicateInputOrdinal,
                    observation.InputOrdinal,
                    current?.GroupOrdinal,
                    ExpectedTemplatePageNumber: null,
                    observation.DetectedTemplatePageNumber,
                    $"Input ordinal {observation.InputOrdinal} occurs more than once."));
                current?.MarkNeedsReview();
                continue;
            }

            if (observation.InputOrdinal > previousInputOrdinal + 1)
            {
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.InputOrdinalGap,
                    observation.InputOrdinal,
                    current?.GroupOrdinal,
                    previousInputOrdinal + 1,
                    observation.InputOrdinal,
                    $"Input ordinals {previousInputOrdinal + 1} through " +
                    $"{observation.InputOrdinal - 1} are missing."));
                current?.MarkNeedsReview();
            }

            previousInputOrdinal = observation.InputOrdinal;
            var pageNumber = observation.DetectedTemplatePageNumber;

            if (pageNumber == 1)
            {
                CloseCurrent();
                current = new GroupBuilder(nextGroupOrdinal++);
                current.Add(observation);
                continue;
            }

            if (current is null)
            {
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.OrphanPage,
                    observation.InputOrdinal,
                    GroupOrdinal: null,
                    ExpectedTemplatePageNumber: 1,
                    pageNumber,
                    "A scan cannot be grouped until a template page 1 is detected."));
                continue;
            }

            current.Add(observation);
            if (pageNumber is null)
            {
                current.MarkNeedsReview();
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.UnclassifiedPage,
                    observation.InputOrdinal,
                    current.GroupOrdinal,
                    ExpectedTemplatePageNumber: null,
                    ActualTemplatePageNumber: null,
                    "The scan could not be classified as a template page."));
            }
            else if (pageNumber < 1 || pageNumber > expectedPageCount)
            {
                current.MarkNeedsReview();
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.InvalidTemplatePage,
                    observation.InputOrdinal,
                    current.GroupOrdinal,
                    ExpectedTemplatePageNumber: null,
                    pageNumber,
                    $"Detected template page {pageNumber} is outside the valid range " +
                    $"1 through {expectedPageCount}."));
            }
        }

        CloseCurrent();
        return new OrderedScanAssemblyPlan(expectedPageCount, groups, issues);

        void CloseCurrent()
        {
            if (current is null)
            {
                return;
            }

            var classifiedPages = current.Pages
                .Where(page => page.DetectedTemplatePageNumber is >= 1)
                .Where(page => page.DetectedTemplatePageNumber <= expectedPageCount)
                .ToArray();
            var pageCounts = classifiedPages
                .GroupBy(page => page.DetectedTemplatePageNumber!.Value)
                .ToDictionary(group => group.Key, group => group.Count());
            var missingPages = Enumerable.Range(1, expectedPageCount)
                .Where(page => !pageCounts.ContainsKey(page))
                .ToArray();

            foreach (var duplicate in pageCounts.Where(pair => pair.Value > 1))
            {
                current.MarkNeedsReview();
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.DuplicateTemplatePage,
                    InputOrdinal: null,
                    current.GroupOrdinal,
                    duplicate.Key,
                    duplicate.Key,
                    $"Group {current.GroupOrdinal} contains template page " +
                    $"{duplicate.Key} more than once."));
            }

            foreach (var missingPage in missingPages)
            {
                current.MarkNeedsReview();
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.MissingTemplatePage,
                    InputOrdinal: null,
                    current.GroupOrdinal,
                    missingPage,
                    ActualTemplatePageNumber: null,
                    $"Group {current.GroupOrdinal} is missing template page " +
                    $"{missingPage}."));
            }

            var actualOrder = classifiedPages
                .Select(page => page.DetectedTemplatePageNumber!.Value)
                .ToArray();
            var expectedOrder = Enumerable.Range(1, expectedPageCount).ToArray();
            if (missingPages.Length == 0
                && !actualOrder.SequenceEqual(expectedOrder))
            {
                current.MarkNeedsReview();
                issues.Add(new OrderedScanAssemblyIssue(
                    OrderedScanAssemblyIssueCodes.PagesOutOfOrder,
                    InputOrdinal: null,
                    current.GroupOrdinal,
                    ExpectedTemplatePageNumber: null,
                    ActualTemplatePageNumber: null,
                    $"Group {current.GroupOrdinal} pages are not in template order."));
            }

            groups.Add(new OrderedScanGroupPlan(
                current.GroupOrdinal,
                current.NeedsReview
                    ? OrderedScanGroupStatus.NeedsReview
                    : OrderedScanGroupStatus.Complete,
                current.Pages.Select(page => new OrderedScanPagePlacement(
                    page.InputOrdinal,
                    page.DetectedTemplatePageNumber)),
                missingPages));
            current = null;
        }
    }

    private static DomainValidationException Validation(
        string code,
        string message,
        string path) =>
        new([new DomainError(code, message, path)]);

    private sealed class GroupBuilder(int groupOrdinal)
    {
        public int GroupOrdinal { get; } = groupOrdinal;

        public List<OrderedScanPageObservation> Pages { get; } = [];

        public bool NeedsReview { get; private set; }

        public void Add(OrderedScanPageObservation observation) =>
            Pages.Add(observation);

        public void MarkNeedsReview() => NeedsReview = true;
    }
}

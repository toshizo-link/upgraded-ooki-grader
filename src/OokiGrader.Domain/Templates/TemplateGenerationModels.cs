using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OokiGrader.Domain.Templates;

[JsonConverter(typeof(JsonStringEnumConverter<TemplateGenerationBatchStatus>))]
public enum TemplateGenerationBatchStatus
{
    [JsonStringEnumMemberName("draft")]
    Draft = 0,

    [JsonStringEnumMemberName("validating")]
    Validating = 1,

    [JsonStringEnumMemberName("generating")]
    Generating = 2,

    [JsonStringEnumMemberName("needsFinalCheck")]
    NeedsFinalCheck = 3,

    [JsonStringEnumMemberName("confirming")]
    Confirming = 4,

    [JsonStringEnumMemberName("completed")]
    Completed = 5,

    [JsonStringEnumMemberName("failed")]
    Failed = 6,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled = 7,
}

[JsonConverter(typeof(JsonStringEnumConverter<TemplateGenerationUnitStatus>))]
public enum TemplateGenerationUnitStatus
{
    [JsonStringEnumMemberName("pending")]
    Pending = 0,

    [JsonStringEnumMemberName("queued")]
    Queued = 1,

    [JsonStringEnumMemberName("generating")]
    Generating = 2,

    [JsonStringEnumMemberName("rotating")]
    Rotating = 3,

    [JsonStringEnumMemberName("retryingAfterRotation")]
    RetryingAfterRotation = 4,

    [JsonStringEnumMemberName("extracted")]
    Extracted = 5,

    [JsonStringEnumMemberName("failed")]
    Failed = 6,

    [JsonStringEnumMemberName("confirmed")]
    Confirmed = 7,
}

[JsonConverter(typeof(JsonStringEnumConverter<TestType>))]
public enum TestType
{
    [JsonStringEnumMemberName("hop")]
    Hop = 1,

    [JsonStringEnumMemberName("step")]
    Step = 2,

    [JsonStringEnumMemberName("classPlacement")]
    ClassPlacement = 3,

    [JsonStringEnumMemberName("other")]
    Other = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<AnswerStyle>))]
public enum AnswerStyle
{
    [JsonStringEnumMemberName("normal")]
    Normal = 1,

    [JsonStringEnumMemberName("fillBlank")]
    FillBlank = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<TemplatePromptSystem>))]
public enum TemplatePromptSystem
{
    [JsonStringEnumMemberName("standard")]
    Standard = 1,

    [JsonStringEnumMemberName("classPlacement")]
    ClassPlacement = 2,

    [JsonStringEnumMemberName("fillBlank")]
    FillBlank = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<GradeLevel>))]
public enum GradeLevel
{
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    [JsonStringEnumMemberName("grade1")]
    Grade1 = 1,

    [JsonStringEnumMemberName("grade2")]
    Grade2 = 2,

    [JsonStringEnumMemberName("grade3")]
    Grade3 = 3,

    [JsonStringEnumMemberName("grade4")]
    Grade4 = 4,

    [JsonStringEnumMemberName("grade5")]
    Grade5 = 5,

    [JsonStringEnumMemberName("grade6")]
    Grade6 = 6,
}

[JsonConverter(typeof(JsonStringEnumConverter<GradeEvidence>))]
public enum GradeEvidence
{
    [JsonStringEnumMemberName("none")]
    None = 0,

    [JsonStringEnumMemberName("fileName")]
    FileName = 1,

    [JsonStringEnumMemberName("paper")]
    Paper = 2,

    [JsonStringEnumMemberName("fileNameAndPaper")]
    FileNameAndPaper = 3,

    [JsonStringEnumMemberName("user")]
    User = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<GenerationWarningSeverity>))]
public enum GenerationWarningSeverity
{
    [JsonStringEnumMemberName("information")]
    Information = 1,

    [JsonStringEnumMemberName("warning")]
    Warning = 2,

    [JsonStringEnumMemberName("blocking")]
    Blocking = 3,
}

public sealed record GenerationWarning(
    string Code,
    GenerationWarningSeverity Severity,
    string Message);

public sealed record TemplateGenerationProfile(
    int ProfileVersion,
    TestType TestType,
    string Subject,
    AnswerStyle? AnswerStyle,
    TemplatePromptSystem PromptSystem,
    int SourcePageCount,
    int UnitSequence,
    int FirstPage,
    int LastPage,
    int? StepSetIndex,
    int? StepVariationIndex,
    string? DeterministicSuffix,
    string SplitPolicyVersion,
    string NamingPolicyVersion,
    string ExtractionPromptVersion,
    string ExtractionSchemaVersion)
{
    public const int CurrentProfileVersion = 1;
    public const string CurrentSplitPolicyVersion = "deterministic-split-v1";
    public const string LegacyNamingPolicyVersion =
        "paper-name-step-suffix-v1";
    public const string CurrentNamingPolicyVersion =
        "known-test-deterministic-name-v2";

    public static bool IsSupportedNamingPolicyVersion(string? version) =>
        version is LegacyNamingPolicyVersion or CurrentNamingPolicyVersion;

    /// <summary>
    /// Produces a culture-independent fingerprint of every immutable profile
    /// field. Length-prefixing prevents ambiguous delimiter combinations.
    /// </summary>
    public string ComputeHash()
    {
        var canonical = new StringBuilder(512);
        Append(canonical, ProfileVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, ((int)TestType).ToString(CultureInfo.InvariantCulture));
        Append(canonical, Subject);
        Append(
            canonical,
            AnswerStyle is null
                ? null
                : ((int)AnswerStyle.Value).ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            ((int)PromptSystem).ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            SourcePageCount.ToString(CultureInfo.InvariantCulture));
        Append(canonical, UnitSequence.ToString(CultureInfo.InvariantCulture));
        Append(canonical, FirstPage.ToString(CultureInfo.InvariantCulture));
        Append(canonical, LastPage.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            StepSetIndex?.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            StepVariationIndex?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, DeterministicSuffix);
        Append(canonical, SplitPolicyVersion);
        Append(canonical, NamingPolicyVersion);
        Append(canonical, ExtractionPromptVersion);
        Append(canonical, ExtractionSchemaVersion);

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string? value)
    {
        if (value is null)
        {
            target.Append("-1:");
            return;
        }

        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }
}

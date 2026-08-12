using System.Globalization;
using System.Text.Json;

namespace OokiGrader.Host.Jobs;

/// <summary>
/// Validates the v5 orientation-gated envelope and delegates the established
/// question/answer invariants to the existing canonical extraction validator.
/// Cross-field action rules are deliberately enforced in host code instead of
/// depending on provider support for conditional JSON Schema.
/// </summary>
internal static class OrientationGatedTemplateExtractionValidator
{
    // Keep this envelope bound aligned with the default deterministic-batch
    // source ceiling. Whole-document Class/Other units may legitimately exceed
    // the per-batch unit count even though HOP/STEP units never do.
    private const int MaximumPages = 1_000;

    public static OrientationGatedTemplateExtraction Validate(
        JsonElement root,
        string expectedRequestKey,
        IReadOnlyCollection<TemplateExtractionPageManifest> suppliedPages,
        IReadOnlyDictionary<string, TemplateExtractionSourceEvidence> sources,
        long defaultPointsMilli,
        long? targetTotalPointsMilli)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRequestKey);
        ArgumentNullException.ThrowIfNull(suppliedPages);
        ArgumentNullException.ThrowIfNull(sources);
        if (suppliedPages.Count is 0 or > MaximumPages
            || suppliedPages.Select(page => page.PageId)
                .Distinct(StringComparer.Ordinal).Count() != suppliedPages.Count
            || suppliedPages.Select(page => (page.SourceId, page.PageNumber))
                .Distinct().Count() != suppliedPages.Count)
        {
            throw Invalid("ORIENTATION_RESPONSE_INVALID");
        }

        RequireObject(root, "AI_STRUCTURED_OUTPUT_INVALID");
        RequireExactProperties(
            root,
            ["schema_version", "request_key", "action", "orientation", "metadata", "pages"],
            "AI_STRUCTURED_OUTPUT_INVALID");
        RequireString(root, "schema_version", "template_extract_v5",
            "AI_STRUCTURED_OUTPUT_INVALID");
        RequireString(root, "request_key", expectedRequestKey,
            "AI_STRUCTURED_OUTPUT_INVALID");
        var action = RequireString(root, "action", expected: null,
            "AI_STRUCTURED_OUTPUT_INVALID");
        if (action is not ("rotate" or "extract"))
        {
            throw Invalid("AI_STRUCTURED_OUTPUT_INVALID");
        }

        var orientation = RequireProperty(
            root,
            "orientation",
            "ORIENTATION_RESPONSE_INVALID");
        RequireObject(orientation, "ORIENTATION_RESPONSE_INVALID");
        RequireExactProperties(
            orientation,
            ["pages"],
            "ORIENTATION_RESPONSE_INVALID");
        var orientationPages = RequireProperty(
            orientation,
            "pages",
            "ORIENTATION_RESPONSE_INVALID");
        if (orientationPages.ValueKind != JsonValueKind.Array
            || orientationPages.GetArrayLength() != suppliedPages.Count)
        {
            throw Invalid("ORIENTATION_RESPONSE_INVALID");
        }

        var suppliedById = suppliedPages.ToDictionary(
            page => page.PageId,
            StringComparer.Ordinal);
        var decisions = new List<TemplatePageOrientation>(suppliedPages.Count);
        var seenPageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in orientationPages.EnumerateArray())
        {
            RequireObject(item, "ORIENTATION_RESPONSE_INVALID");
            RequireExactProperties(
                item,
                ["page_id", "clockwise_degrees_to_upright", "confidence"],
                "ORIENTATION_RESPONSE_INVALID");
            var pageId = RequireString(
                item,
                "page_id",
                expected: null,
                "ORIENTATION_RESPONSE_INVALID");
            if (!suppliedById.ContainsKey(pageId) || !seenPageIds.Add(pageId))
            {
                throw Invalid("ORIENTATION_RESPONSE_INVALID");
            }

            var degreesElement = RequireProperty(
                item,
                "clockwise_degrees_to_upright",
                "ORIENTATION_RESPONSE_INVALID");
            if (degreesElement.ValueKind != JsonValueKind.Number
                || !degreesElement.TryGetInt32(out var degrees)
                || degrees is not (0 or 90 or 180 or 270))
            {
                throw Invalid("ORIENTATION_RESPONSE_INVALID");
            }

            var confidenceElement = RequireProperty(
                item,
                "confidence",
                "ORIENTATION_RESPONSE_INVALID");
            if (confidenceElement.ValueKind != JsonValueKind.Number
                || !confidenceElement.TryGetDouble(out var confidence)
                || !double.IsFinite(confidence)
                || confidence is < 0 or > 1)
            {
                throw Invalid("ORIENTATION_RESPONSE_INVALID");
            }

            decisions.Add(new TemplatePageOrientation(
                pageId,
                degrees,
                confidence));
        }

        var metadata = RequireProperty(
            root,
            "metadata",
            "AI_STRUCTURED_OUTPUT_INVALID");
        var pages = RequireProperty(
            root,
            "pages",
            "AI_STRUCTURED_OUTPUT_INVALID");
        if (pages.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("AI_STRUCTURED_OUTPUT_INVALID");
        }

        if (action == "rotate")
        {
            if (metadata.ValueKind != JsonValueKind.Null
                || pages.GetArrayLength() != 0
                || decisions.All(page => page.ClockwiseDegreesToUpright == 0))
            {
                throw Invalid("ORIENTATION_RESPONSE_INVALID");
            }

            return new OrientationGatedTemplateExtraction(
                TemplateExtractionAction.Rotate,
                decisions,
                Extraction: null);
        }

        if (decisions.Any(page => page.ClockwiseDegreesToUpright != 0)
            || metadata.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("ORIENTATION_RESPONSE_INVALID");
        }

        RequireExactProperties(
            metadata,
            ["printed_test_name", "printed_grade_label", "grade_confidence", "warnings"],
            "AI_STRUCTURED_OUTPUT_INVALID");
        _ = ReadNullableString(metadata, "printed_test_name", 500);
        _ = ReadNullableString(metadata, "printed_grade_label", 200);
        var gradeConfidence = RequireProperty(
            metadata,
            "grade_confidence",
            "AI_STRUCTURED_OUTPUT_INVALID");
        if (gradeConfidence.ValueKind != JsonValueKind.Number
            || !gradeConfidence.TryGetDouble(out var confidenceValue)
            || !double.IsFinite(confidenceValue)
            || confidenceValue is < 0 or > 1)
        {
            throw Invalid("AI_STRUCTURED_OUTPUT_INVALID");
        }

        var warnings = RequireProperty(
            metadata,
            "warnings",
            "AI_STRUCTURED_OUTPUT_INVALID");
        ValidateWarnings(warnings);

        using var legacyDocument = CreateLegacyExtractionDocument(
            expectedRequestKey,
            metadata,
            pages);
        var extraction = TemplateExtractionResponseValidator.Validate(
            legacyDocument.RootElement,
            expectedRequestKey,
            sources,
            defaultPointsMilli,
            targetTotalPointsMilli,
            requireGradingRuleFlags: true);
        var expectedExtractionPages = suppliedPages
            .Select(page => string.Create(
                CultureInfo.InvariantCulture,
                $"{page.SourceId}:{page.PageNumber}"))
            .ToHashSet(StringComparer.Ordinal);
        var actualExtractionPages = extraction.Pages
            .Select(page => string.Create(
                CultureInfo.InvariantCulture,
                $"{page.SourceId}:{page.PageNumber}"))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualExtractionPages.SetEquals(expectedExtractionPages))
        {
            throw Invalid("AI_STRUCTURED_OUTPUT_INVALID");
        }

        return new OrientationGatedTemplateExtraction(
            TemplateExtractionAction.Extract,
            decisions,
            extraction);
    }

    private static JsonDocument CreateLegacyExtractionDocument(
        string requestKey,
        JsonElement metadata,
        JsonElement pages)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "template_extract_v4");
            writer.WriteString("request_key", requestKey);
            writer.WriteStartObject("metadata");
            WriteNullableString(writer, "title", metadata.GetProperty("printed_test_name"));
            writer.WriteNull("subject");
            writer.WriteNull("category");
            WriteNullableString(writer, "grade_label", metadata.GetProperty("printed_grade_label"));
            writer.WriteNull("course");
            writer.WriteNumber("confidence", metadata.GetProperty("grade_confidence").GetDouble());
            writer.WritePropertyName("warnings");
            metadata.GetProperty("warnings").WriteTo(writer);
            writer.WriteEndObject();
            writer.WritePropertyName("pages");
            pages.WriteTo(writer);
            writer.WritePropertyName("global_warnings");
            metadata.GetProperty("warnings").WriteTo(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value.GetString());
        }
    }

    private static string? ReadNullableString(
        JsonElement owner,
        string name,
        int maximumLength)
    {
        var value = RequireProperty(owner, name, "AI_STRUCTURED_OUTPUT_INVALID");
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maximumLength)
        {
            throw Invalid("AI_STRUCTURED_OUTPUT_INVALID");
        }

        return text;
    }

    private static void ValidateWarnings(JsonElement warnings)
    {
        if (warnings.ValueKind != JsonValueKind.Array
            || warnings.GetArrayLength() > 100
            || warnings.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String
                || item.GetString() is not { } value
                || string.IsNullOrWhiteSpace(value)
                || value.Length > 1_000))
        {
            throw Invalid("AI_STRUCTURED_OUTPUT_INVALID");
        }
    }

    private static string RequireString(
        JsonElement owner,
        string name,
        string? expected,
        string errorCode)
    {
        var value = RequireProperty(owner, name, errorCode);
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text)
            || text.Length > 200
            || (expected is not null
                && !string.Equals(text, expected, StringComparison.Ordinal)))
        {
            throw Invalid(errorCode);
        }

        return text;
    }

    private static JsonElement RequireProperty(
        JsonElement owner,
        string name,
        string errorCode)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            throw Invalid(errorCode);
        }

        return value;
    }

    private static void RequireObject(JsonElement value, string errorCode)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(errorCode);
        }
    }

    private static void RequireExactProperties(
        JsonElement value,
        IReadOnlyCollection<string> expected,
        string errorCode)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || actual.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
        {
            throw Invalid(errorCode);
        }
    }

    private static InvalidDataException Invalid(string code) => new(code);
}

internal enum TemplateExtractionAction
{
    Rotate,
    Extract,
}

internal sealed record TemplateExtractionPageManifest(
    string PageId,
    string SourceId,
    int PageNumber);

internal sealed record TemplatePageOrientation(
    string PageId,
    int ClockwiseDegreesToUpright,
    double Confidence);

internal sealed record OrientationGatedTemplateExtraction(
    TemplateExtractionAction Action,
    IReadOnlyList<TemplatePageOrientation> Orientation,
    ValidatedTemplateExtraction? Extraction);

using System.Text.Json;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class OrientationGatedTemplateExtractionValidatorTests
{
    [Fact]
    public void AcceptsRotationOnlyResponse()
    {
        var result = Validate(RotationResponse(90));

        Assert.Equal(TemplateExtractionAction.Rotate, result.Action);
        Assert.Null(result.Extraction);
        Assert.Equal(90, Assert.Single(result.Orientation).ClockwiseDegreesToUpright);
    }

    [Fact]
    public void AcceptsWholeDocumentRotationManifestAboveTwoHundredPages()
    {
        const int pageCount = 201;
        var suppliedPages = Enumerable.Range(1, pageCount)
            .Select(page => new TemplateExtractionPageManifest(
                $"page-{page}",
                "source-1",
                page))
            .ToArray();
        var response = JsonSerializer.SerializeToElement(new
        {
            schema_version = "template_extract_v5",
            request_key = "request-1",
            action = "rotate",
            orientation = new
            {
                pages = suppliedPages.Select(page => new
                {
                    page_id = page.PageId,
                    clockwise_degrees_to_upright = page.PageNumber == pageCount
                        ? 90
                        : 0,
                    confidence = 0.98,
                }),
            },
            metadata = (object?)null,
            pages = Array.Empty<object>(),
        });

        var result = OrientationGatedTemplateExtractionValidator.Validate(
            response,
            "request-1",
            suppliedPages,
            new Dictionary<string, TemplateExtractionSourceEvidence>(StringComparer.Ordinal)
            {
                ["source-1"] = new("source-1", "unit_test_paper", pageCount),
            },
            defaultPointsMilli: 1_000,
            targetTotalPointsMilli: null);

        Assert.Equal(TemplateExtractionAction.Rotate, result.Action);
        Assert.Equal(pageCount, result.Orientation.Count);
        Assert.Equal(
            90,
            result.Orientation[^1].ClockwiseDegreesToUpright);
    }

    [Fact]
    public void AcceptsUprightExtractionWithPrintedMetadata()
    {
        var result = Validate(ExtractionResponse(0));

        Assert.Equal(TemplateExtractionAction.Extract, result.Action);
        Assert.NotNull(result.Extraction);
        Assert.Equal("STEP算数 第4回", result.Extraction.Metadata.Title);
        Assert.Equal("小学4年", result.Extraction.Metadata.GradeLabel);
        Assert.Single(result.Extraction.Pages);
    }

    [Fact]
    public void RejectsRotationResponseContainingMetadata()
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            schema_version = "template_extract_v5",
            request_key = "request-1",
            action = "rotate",
            orientation = new
            {
                pages = new[]
                {
                    new
                    {
                        page_id = "page-1",
                        clockwise_degrees_to_upright = 90,
                        confidence = 0.98,
                    },
                },
            },
            metadata = Metadata(),
            pages = Array.Empty<object>(),
        });

        var exception = Assert.Throws<InvalidDataException>(() => Validate(response));
        Assert.Equal("ORIENTATION_RESPONSE_INVALID", exception.Message);
    }

    [Fact]
    public void RejectsRotationResponseWithAllZeroDegrees()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            Validate(RotationResponse(0)));
        Assert.Equal("ORIENTATION_RESPONSE_INVALID", exception.Message);
    }

    [Fact]
    public void RejectsExtractionWithNonZeroRotation()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            Validate(ExtractionResponse(270)));
        Assert.Equal("ORIENTATION_RESPONSE_INVALID", exception.Message);
    }

    [Fact]
    public void RejectsUnknownOrMissingPageId()
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            schema_version = "template_extract_v5",
            request_key = "request-1",
            action = "rotate",
            orientation = new
            {
                pages = new[]
                {
                    new
                    {
                        page_id = "unknown-page",
                        clockwise_degrees_to_upright = 90,
                        confidence = 1.0,
                    },
                },
            },
            metadata = (object?)null,
            pages = Array.Empty<object>(),
        });

        var exception = Assert.Throws<InvalidDataException>(() => Validate(response));
        Assert.Equal("ORIENTATION_RESPONSE_INVALID", exception.Message);
    }

    [Fact]
    public void RejectsUnknownRootProperty()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "schema_version":"template_extract_v5",
              "request_key":"request-1",
              "action":"rotate",
              "orientation":{"pages":[{"page_id":"page-1","clockwise_degrees_to_upright":90,"confidence":1}]},
              "metadata":null,
              "pages":[],
              "category":"hop"
            }
            """);

        var exception = Assert.Throws<InvalidDataException>(() =>
            Validate(document.RootElement));
        Assert.Equal("AI_STRUCTURED_OUTPUT_INVALID", exception.Message);
    }

    [Fact]
    public void RejectsV5ExtractionQuestionMissingGradingRuleFlags()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "schema_version":"template_extract_v5",
              "request_key":"request-1",
              "action":"extract",
              "orientation":{"pages":[{"page_id":"page-1","clockwise_degrees_to_upright":0,"confidence":1}]},
              "metadata":{"printed_test_name":"確認","printed_grade_label":null,"grade_confidence":1,"warnings":[]},
              "pages":[{"source_id":"source-1","page_number":1,"detected_answer_slot_count":1,"questions":[{
                "source_key":"slot-1","display_label":"1","question_text":"答えなさい。",
                "answer_slot_ordinal":1,"answer_slot_count":1,"filled_answer_removed":true,
                "is_embedded_fill_blank":false,"question_type":"exact_short_text",
                "expected_answer":"東京","answer_provenance":"ai_proposed","answer_source":null,
                "accepted_variants":[],"suggested_points_milli":1000,
                "allow_non_kanji_suggestion":false,"requires_teacher_answer":false,
                "confidence":1,"warnings":[]
              }]}]
            }
            """);

        Assert.Throws<InvalidDataException>(() => Validate(document.RootElement));
    }

    private static OrientationGatedTemplateExtraction Validate(JsonElement root) =>
        OrientationGatedTemplateExtractionValidator.Validate(
            root,
            "request-1",
            [new TemplateExtractionPageManifest("page-1", "source-1", 1)],
            new Dictionary<string, TemplateExtractionSourceEvidence>(StringComparer.Ordinal)
            {
                ["source-1"] = new("source-1", "blank_test", 1),
            },
            defaultPointsMilli: 1_000,
            targetTotalPointsMilli: null);

    private static JsonElement RotationResponse(int degrees) =>
        JsonSerializer.SerializeToElement(new
        {
            schema_version = "template_extract_v5",
            request_key = "request-1",
            action = "rotate",
            orientation = new
            {
                pages = new[]
                {
                    new
                    {
                        page_id = "page-1",
                        clockwise_degrees_to_upright = degrees,
                        confidence = 0.98,
                    },
                },
            },
            metadata = (object?)null,
            pages = Array.Empty<object>(),
        });

    private static JsonElement ExtractionResponse(int degrees) =>
        JsonSerializer.SerializeToElement(new
        {
            schema_version = "template_extract_v5",
            request_key = "request-1",
            action = "extract",
            orientation = new
            {
                pages = new[]
                {
                    new
                    {
                        page_id = "page-1",
                        clockwise_degrees_to_upright = degrees,
                        confidence = 0.99,
                    },
                },
            },
            metadata = Metadata(),
            pages = new[]
            {
                new
                {
                    source_id = "source-1",
                    page_number = 1,
                    detected_answer_slot_count = 1,
                    questions = new[]
                    {
                        new
                        {
                            source_key = "page-1-slot-1",
                            display_label = "1",
                            question_text = "1 + 1 はいくつですか。",
                            answer_slot_ordinal = 1,
                            answer_slot_count = 1,
                            filled_answer_removed = true,
                            is_embedded_fill_blank = false,
                            question_type = "numeric",
                            expected_answer = "2",
                            answer_provenance = "ai_proposed",
                            answer_source = (object?)null,
                            accepted_variants = Array.Empty<string>(),
                            suggested_points_milli = 1_000,
                            allow_non_kanji_suggestion = false,
                            requires_complete_answer_suggestion = false,
                            answer_order_insensitive_suggestion = false,
                            requires_teacher_answer = false,
                            confidence = 0.99,
                            warnings = Array.Empty<string>(),
                        },
                    },
                },
            },
        });

    private static object Metadata() => new
    {
        printed_test_name = "STEP算数 第4回",
        printed_grade_label = "小学4年",
        grade_confidence = 0.98,
        warnings = Array.Empty<string>(),
    };
}

using System.Globalization;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

internal static class QuestionEntityDomainMapper
{
    public static QuestionDefinition Map(
        QuestionEntity entity,
        TemplateVersionEntity version)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(version);

        var acceptedAnswers = entity.AcceptedAnswers
            .Select(answer => MapAnswer(answer, version))
            .ToArray();
        var canonical = entity.AcceptedAnswers.FirstOrDefault(
            answer => answer.VariantType == "canonical");
        NumericAnswerPolicy? numericPolicy = null;
        ChoiceAnswerPolicy? choicePolicy = null;
        if (entity.QuestionType == "numeric"
            && canonical is not null
            && TryParseNumeric(canonical.AnswerText, out var expectedNumber))
        {
            numericPolicy = new NumericAnswerPolicy(expectedNumber);
        }

        if (entity.QuestionType is "multiple_choice" or "boolean"
            && canonical is not null)
        {
            var choices = entity.AcceptedAnswers
                .Select(answer => answer.AnswerText)
                .Append(canonical.AnswerText)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            choicePolicy = new ChoiceAnswerPolicy(canonical.AnswerText, choices);
        }

        return new QuestionDefinition(
            entity.Id,
            entity.LogicalQuestionId,
            entity.OrderIndex,
            entity.DisplayLabel,
            entity.QuestionText,
            ParseQuestionType(entity.QuestionType),
            ParseGradingMode(entity.GradingMode),
            new MilliPoints(entity.MaxPointsMilli),
            new MilliPoints(entity.PointIncrementMilli),
            entity.AllowNonKanji,
            entity.RequiresReviewAlways,
            entity.TeacherVerified,
            acceptedAnswers,
            numericPolicy: numericPolicy,
            choicePolicy: choicePolicy,
            kanjiPolicyNote: entity.KanjiPolicyNote);
    }

    private static AcceptedAnswer MapAnswer(
        AcceptedAnswerEntity entity,
        TemplateVersionEntity version)
    {
        AnswerSourceReference? source = null;
        if (entity.AnswerProvenance == "provided_model_answer"
            && entity.SourceFileReferenceId is not null
            && entity.SourcePageNumber is > 0)
        {
            var templateSource = version.Sources.FirstOrDefault(item =>
                item.FileReferenceId == entity.SourceFileReferenceId);
            if (templateSource is not null)
            {
                source = new AnswerSourceReference(
                    templateSource.Id,
                    ParseSourceRole(templateSource.SourceRole),
                    entity.SourcePageNumber.Value,
                    entity.SourceRegionId);
            }
        }

        return new AcceptedAnswer(
            entity.Id,
            entity.AnswerText,
            ParseAnswerVariant(entity.VariantType),
            ParseAnswerProvenance(entity.AnswerProvenance),
            entity.TeacherVerified,
            source);
    }

    private static bool TryParseNumeric(string value, out decimal number)
    {
        var normalized = JapaneseTextNormalizer.NormalizeForComparison(value);
        if (decimal.TryParse(
                normalized,
                NumberStyles.AllowLeadingSign
                    | NumberStyles.AllowDecimalPoint
                    | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out number))
        {
            return true;
        }

        var pieces = normalized.Split('/');
        if (pieces.Length == 2
            && decimal.TryParse(
                pieces[0],
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var numerator)
            && decimal.TryParse(
                pieces[1],
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var denominator)
            && denominator != 0)
        {
            try
            {
                number = numerator / denominator;
                return true;
            }
            catch (OverflowException)
            {
            }
        }

        number = default;
        return false;
    }

    private static QuestionType ParseQuestionType(string value) => value switch
    {
        "multiple_choice" => QuestionType.MultipleChoice,
        "boolean" => QuestionType.Boolean,
        "numeric" => QuestionType.Numeric,
        "exact_short_text" => QuestionType.ExactShortText,
        "semantic_short_text" => QuestionType.SemanticShortText,
        "multi_part" => QuestionType.MultiPart,
        "subjective" => QuestionType.Subjective,
        "unsupported" => QuestionType.Unsupported,
        _ => Invalid<QuestionType>("question.type_invalid", value),
    };

    private static GradingMode ParseGradingMode(string value) => value switch
    {
        "deterministic" => GradingMode.Deterministic,
        "transcribe_then_rules" => GradingMode.TranscribeThenRules,
        "ai_rubric" => GradingMode.AiRubric,
        "manual" => GradingMode.Manual,
        _ => Invalid<GradingMode>("question.grading_mode_invalid", value),
    };

    private static AcceptedAnswerVariantType ParseAnswerVariant(string value) =>
        value switch
        {
            "canonical" => AcceptedAnswerVariantType.Canonical,
            "equivalent" => AcceptedAnswerVariantType.Equivalent,
            "phonetic_exception" => AcceptedAnswerVariantType.PhoneticException,
            "numeric" => AcceptedAnswerVariantType.Numeric,
            "regex_restricted" => AcceptedAnswerVariantType.RegexRestricted,
            "choice" => AcceptedAnswerVariantType.Choice,
            _ => Invalid<AcceptedAnswerVariantType>(
                "answer.variant_invalid",
                value),
        };

    private static AnswerProvenance ParseAnswerProvenance(string value) =>
        value switch
        {
            "provided_model_answer" => AnswerProvenance.ProvidedModelAnswer,
            "teacher_entered" => AnswerProvenance.TeacherEntered,
            "ai_proposed" => AnswerProvenance.AiProposed,
            "derived_variant" => AnswerProvenance.DerivedVariant,
            _ => Invalid<AnswerProvenance>(
                "answer.provenance_invalid",
                value),
        };

    private static TemplateSourceRole ParseSourceRole(string value) =>
        value switch
        {
            "blank_test" => TemplateSourceRole.BlankTest,
            "contains_model_answers" => TemplateSourceRole.ContainsModelAnswers,
            "contains_non_model_answers" =>
                TemplateSourceRole.ContainsNonModelAnswers,
            "separate_answer_key" => TemplateSourceRole.SeparateAnswerKey,
            _ => Invalid<TemplateSourceRole>(
                "template.source_role_invalid",
                value),
        };

    private static T Invalid<T>(string code, string value) =>
        throw new DomainValidationException(
        [
            new DomainError(code, $"Unsupported persisted value '{value}'."),
        ]);
}

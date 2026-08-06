using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Grading;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;
using Xunit.Abstractions;
using DomainQuestionDefinition = OokiGrader.Domain.Templates.QuestionDefinition;

namespace OokiGrader.ProviderContract.Tests;

public sealed class GeminiGradingAccuracyEvaluationTests(ITestOutputHelper output)
{
    private const string DefaultModelId = "gemini-3.5-flash-lite";
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    [LiveAccuracyFact(
        "OOKI_GEMINI_API_KEY",
        "OOKI_EXTERNAL_FIXTURE_ROOT",
        "OOKI_GRADING_EVAL_MEDIA_DIR")]
    public async Task ExactModelProducesBasicGradingAccuracyEvidence()
    {
        var repositoryRoot = RequiredDirectory("OOKI_EXTERNAL_FIXTURE_ROOT");
        var mediaDirectory = RequiredDirectory("OOKI_GRADING_EVAL_MEDIA_DIR");
        var requestedModel = OptionalValue("OOKI_GRADING_EVAL_MODEL_ID")
            ?? DefaultModelId;
        var thinkingLevel = ResolveThinkingLevel(requestedModel);
        var connection = Connection(requestedModel);
        var fixtureDirectory = Path.Combine(
            repositoryRoot,
            "tmp",
            "handwritten-exam-fixtures");
        var questions = LoadQuestions(fixtureDirectory);
        var students = LoadStudentTruth(fixtureDirectory);
        var credential = Encoding.UTF8.GetBytes(
            RequiredValue("OOKI_GEMINI_API_KEY"));

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OokiGrader/0.1");
            var client = new GeminiDirectClient(httpClient);
            using var catalog = new ApprovedPromptBundleCatalog();
            var bundle = catalog.GetRequired(AiTaskTypes.InitialGrading);
            var runs = new List<EvaluationRun>();
            foreach (var student in students)
            {
                runs.Add(await GradeAsync(
                        client,
                        connection,
                        bundle,
                        credential,
                        thinkingLevel,
                        student.StudentId,
                        questions,
                        LoadPages(mediaDirectory, student.StudentId),
                        student)
                    .ConfigureAwait(false));
            }

            var japanese = await GradeJapaneseCanonicalAsync(
                    client,
                    connection,
                    bundle,
                    credential,
                    thinkingLevel,
                    repositoryRoot)
                .ConfigureAwait(false);
            var evidence = CreateEvidence(
                repositoryRoot,
                mediaDirectory,
                fixtureDirectory,
                requestedModel,
                thinkingLevel,
                bundle,
                runs,
                japanese);
            var destination = Environment.GetEnvironmentVariable(
                "OOKI_GRADING_EVAL_OUTPUT");
            destination = string.IsNullOrWhiteSpace(destination)
                ? Path.Combine(
                    repositoryRoot,
                    "output",
                    "accuracy",
                    $"{SafeFileName(requestedModel)}-basic-evidence.json")
                : Path.GetFullPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(
                    destination,
                    JsonSerializer.Serialize(evidence, IndentedJsonOptions),
                    Encoding.UTF8)
                .ConfigureAwait(false);

            output.WriteLine($"Accuracy evidence written to {destination}");
            output.WriteLine(
                JsonSerializer.Serialize(evidence.Metrics, IndentedJsonOptions));
            Assert.True(File.Exists(destination));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private static async Task<EvaluationRun> GradeAsync(
        GeminiDirectClient client,
        AiConnectionSettings connection,
        AiPromptBundle bundle,
        byte[] credential,
        string thinkingLevel,
        string studentId,
        IReadOnlyList<EvaluationQuestion> questions,
        IReadOnlyList<MediaFile> pages,
        StudentTruth truth)
    {
        var requestKey = $"basic-accuracy-{studentId.ToLowerInvariant()}";
        var response = await client.GenerateAsync(
                connection,
                credential,
                CreateRequest(
                    requestKey,
                    bundle,
                    questions,
                    pages,
                    thinkingLevel))
            .ConfigureAwait(false);
        var predictions = ParsePredictions(
            response.StructuredOutput,
            requestKey,
            questions);
        var systemValidation = ValidateSystemResponse(
            response.StructuredOutput,
            requestKey,
            questions);
        return new EvaluationRun(
            studentId,
            response.ActualModel,
            response.Latency.TotalMilliseconds,
            response.Usage,
            pages.Select(page => page.Sha256).ToArray(),
            predictions,
            systemValidation.Predictions,
            systemValidation.ErrorCode,
            truth);
    }

    private static async Task<EvaluationRun> GradeJapaneseCanonicalAsync(
        GeminiDirectClient client,
        AiConnectionSettings connection,
        AiPromptBundle bundle,
        byte[] credential,
        string thinkingLevel,
        string repositoryRoot)
    {
        var imagePath = Path.Combine(
            repositoryRoot,
            "tmp",
            "pdfs",
            "user-guide",
            "fixtures",
            "rendered",
            "hanako.png");
        var questions = JapaneseQuestions();
        var truth = new StudentTruth(
            "JapaneseSyntheticHanako",
            questions.ToDictionary(
                question => question.Number,
                question => question.AcceptedAnswer),
            questions.ToDictionary(
                question => question.Number,
                question => question.MaximumPointsMilli / 1_000));
        return await GradeAsync(
                client,
                connection,
                bundle,
                credential,
                thinkingLevel,
                truth.StudentId,
                questions,
                [LoadMedia(imagePath)],
                truth)
            .ConfigureAwait(false);
    }

    private static AiProviderRequest CreateRequest(
        string requestKey,
        AiPromptBundle bundle,
        IReadOnlyList<EvaluationQuestion> questions,
        IReadOnlyList<MediaFile> pages,
        string thinkingLevel)
    {
        var media = pages.Select(
            (page, index) => new
            {
                media_index = index,
                page_number = index + 1,
                page_label = Path.GetFileName(page.Path),
            });
        var rubric = questions.Select(question => new
        {
            question_id = question.Id,
            order_index = question.Number - 1,
            display_label = question.Number.ToString(CultureInfo.InvariantCulture),
            question_text = question.Text,
            question_type = question.Type,
            grading_mode = question.GradingMode,
            maximum_points_milli = question.MaximumPointsMilli,
            point_increment_milli = 1_000,
            allow_non_kanji = false,
            rubric_text = question.RubricText,
            accepted_answers = new[] { question.AcceptedAnswer },
        });
        var instruction =
            """
            The attached media are every page of one completed Japanese test,
            in page order. Match answers to the supplied questions using printed
            question labels, question text, and document layout. Do not infer or
            return student identity. Transcribe each visible answer exactly,
            preserving the observed script. Grade only against the teacher-supplied
            rubric and accepted answers. Include every question ID either once
            in results or once in missing_question_ids. Recommend review whenever
            evidence is ambiguous, incomplete, subjective, unexpected, or
            unreadable.

            """
            + JsonSerializer.Serialize(new
            {
                schema_version = "answer_transcribe_grade_v1",
                request_key = requestKey,
                media,
                questions = rubric,
            });
        return new AiProviderRequest(
            requestKey,
            bundle.TaskType,
            bundle.PromptVersion,
            bundle.SchemaVersion,
            bundle.SystemInstruction,
            instruction,
            bundle.ResponseJsonSchema,
            pages.Select(page => new AiMediaPart(
                    "image/png",
                    page.Bytes,
                    page.Sha256))
                .ToArray(),
            MaxOutputTokens: 16_384,
            MediaResolution: "MEDIA_RESOLUTION_HIGH",
            ThinkingLevel: thinkingLevel);
    }

    private static Prediction[] ParsePredictions(
        JsonElement response,
        string requestKey,
        IReadOnlyList<EvaluationQuestion> questions)
    {
        Assert.Equal(
            "answer_transcribe_grade_v1",
            response.GetProperty("schema_version").GetString());
        Assert.Equal(requestKey, response.GetProperty("request_key").GetString());
        var allowed = questions.Select(question => question.Id)
            .ToHashSet(StringComparer.Ordinal);
        var predictions = response.GetProperty("results")
            .EnumerateArray()
            .Select(item => new Prediction(
                item.GetProperty("question_id").GetString()!,
                item.GetProperty("transcription").GetString()!,
                item.GetProperty("legibility").GetString()!,
                item.GetProperty("blank").GetBoolean(),
                item.GetProperty("proposed_outcome").GetString()!,
                item.GetProperty("proposed_points_milli").GetInt32(),
                item.GetProperty("confidence").GetDouble()))
            .ToArray();
        Assert.Equal(
            predictions.Length,
            predictions.Select(item => item.QuestionId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(predictions, item => Assert.Contains(item.QuestionId, allowed));
        var reportedMissing = response.GetProperty("missing_question_ids")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var actualMissing = allowed
            .Except(predictions.Select(item => item.QuestionId), StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(reportedMissing.SetEquals(actualMissing));
        return predictions;
    }

    private static EvaluationEvidence CreateEvidence(
        string repositoryRoot,
        string mediaDirectory,
        string fixtureDirectory,
        string requestedModel,
        string thinkingLevel,
        AiPromptBundle bundle,
        IReadOnlyList<EvaluationRun> runs,
        EvaluationRun japanese)
    {
        var realRuns = runs.ToArray();
        var metrics = ComputeMetrics(realRuns, japanese);
        var sourceFiles = new[]
        {
            Path.Combine(fixtureDirectory, "Question.txt"),
            Path.Combine(fixtureDirectory, "answerkey.txt"),
            Path.Combine(fixtureDirectory, "Teacher_manual_marks_Anonymized.csv"),
        };
        return new EvaluationEvidence(
            3,
            "basic-exploratory",
            DateTimeOffset.UtcNow,
            requestedModel,
            thinkingLevel,
            bundle.PromptVersion,
            bundle.SchemaVersion,
            bundle.ContentHash,
            "gemini-initial-grading-full-page-v4-local-reconciliation-direct-run",
            "English teacher-scored real handwriting plus one synthetic Japanese canonical-answer sheet",
            sourceFiles.ToDictionary(
                path => Path.GetRelativePath(repositoryRoot, path),
                Sha256,
                StringComparer.Ordinal),
            Directory.GetFiles(mediaDirectory, "*.png")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToDictionary(
                    path => Path.GetRelativePath(repositoryRoot, path),
                    Sha256,
                    StringComparer.Ordinal),
            realRuns,
            japanese,
            metrics,
            [
                "This is a small exploratory sample and is not production approval evidence.",
                "The scored real-paper corpus is English university material, not Japanese cram-school material.",
                "Only 14 nonblank short answers have explicit teacher item scores.",
                "The source CSV MCQ totals conflict with answer-key-derived totals for Student_18 and Student_19, so MCQ aggregate totals are excluded.",
                "The Japanese sheet uses a handwriting-style font rather than genuine student handwriting.",
                "All semantic short-answer proposals remain teacher-review-only in Ooki Grader.",
            ]);
    }

    private static EvaluationMetrics ComputeMetrics(
        IReadOnlyList<EvaluationRun> runs,
        EvaluationRun japanese)
    {
        var attemptedChoices = 0;
        var exactChoiceTranscriptions = 0;
        var choiceScoreAgreement = 0;
        var choiceTruePositive = 0;
        var choiceTrueNegative = 0;
        var choiceFalsePositive = 0;
        var choiceFalseNegative = 0;
        var systemChoiceScoreAgreement = 0;
        var systemChoiceTruePositive = 0;
        var systemChoiceTrueNegative = 0;
        var systemChoiceFalsePositive = 0;
        var systemChoiceFalseNegative = 0;
        var explicitShortScores = 0;
        var exactShortScoreAgreement = 0;
        var shortAbsoluteError = 0d;
        var shortOverCredit = 0;
        var shortUnderCredit = 0;
        var systemExactShortScoreAgreement = 0;
        var systemShortAbsoluteError = 0d;
        var systemShortOverCredit = 0;
        var systemShortUnderCredit = 0;
        var blankShortAnswers = 0;
        var correctlyDetectedBlankShortAnswers = 0;
        var systemCorrectlyDetectedBlankShortAnswers = 0;
        var covered = 0;
        var possible = 0;
        var systemAcceptedRuns = 0;
        var systemAcceptedItems = 0;
        var systemDeterministicRecomputedItems = 0;
        var systemQuarantinedItems = 0;
        foreach (var run in runs)
        {
            var predictionByNumber = run.Predictions.ToDictionary(
                prediction => ParseQuestionNumber(prediction.QuestionId));
            var systemPredictionByNumber = run.SystemPredictions.ToDictionary(
                prediction => ParseQuestionNumber(prediction.QuestionId));
            possible += 35;
            covered += run.Predictions.Count;
            if (run.SystemValidationErrorCode is null)
            {
                systemAcceptedRuns++;
                systemAcceptedItems += run.SystemPredictions.Count;
            }
            systemDeterministicRecomputedItems += run.SystemPredictions.Count(
                prediction => prediction.ReconciliationReasonCode
                    is "ai_deterministic_recomputed");
            systemQuarantinedItems += run.SystemPredictions.Count(
                prediction => prediction.ReconciliationReasonCode
                    is "ai_invalid_point_award" or "ai_manual_question"
                        or "ai_deterministic_review_required");
            foreach (var pair in run.Truth.Responses)
            {
                if (pair.Key <= 20 && IsChoice(pair.Value))
                {
                    attemptedChoices++;
                    var expectedPoints = run.Truth.Points[pair.Key] * 1_000;
                    if (predictionByNumber.TryGetValue(pair.Key, out var prediction))
                    {
                        if (Normalize(pair.Value) == Normalize(prediction.Transcription))
                        {
                            exactChoiceTranscriptions++;
                        }

                        if (prediction.ProposedPointsMilli == expectedPoints)
                        {
                            choiceScoreAgreement++;
                        }

                        var expectedCredit = expectedPoints > 0;
                        var proposedCredit = prediction.ProposedPointsMilli > 0;
                        if (expectedCredit && proposedCredit)
                        {
                            choiceTruePositive++;
                        }
                        else if (!expectedCredit && !proposedCredit)
                        {
                            choiceTrueNegative++;
                        }
                        else if (!expectedCredit)
                        {
                            choiceFalsePositive++;
                        }
                        else
                        {
                            choiceFalseNegative++;
                        }
                    }

                    if (systemPredictionByNumber.TryGetValue(
                            pair.Key,
                            out var systemPrediction))
                    {
                        if (systemPrediction.ProposedPointsMilli == expectedPoints)
                        {
                            systemChoiceScoreAgreement++;
                        }

                        var expectedCredit = expectedPoints > 0;
                        var proposedCredit = systemPrediction.ProposedPointsMilli > 0;
                        if (expectedCredit && proposedCredit)
                        {
                            systemChoiceTruePositive++;
                        }
                        else if (!expectedCredit && !proposedCredit)
                        {
                            systemChoiceTrueNegative++;
                        }
                        else if (!expectedCredit)
                        {
                            systemChoiceFalsePositive++;
                        }
                        else
                        {
                            systemChoiceFalseNegative++;
                        }
                    }
                }
            }

            foreach (var pair in run.Truth.Points.Where(pair => pair.Key > 20))
            {
                if (run.Truth.Responses.TryGetValue(pair.Key, out var response)
                    && string.IsNullOrWhiteSpace(response))
                {
                    blankShortAnswers++;
                    if (predictionByNumber.TryGetValue(pair.Key, out var prediction)
                        && prediction.Blank
                        && prediction.ProposedPointsMilli == 0)
                    {
                        correctlyDetectedBlankShortAnswers++;
                    }

                    if (systemPredictionByNumber.TryGetValue(
                            pair.Key,
                            out var systemPrediction)
                        && systemPrediction.Blank
                        && systemPrediction.ProposedPointsMilli == 0)
                    {
                        systemCorrectlyDetectedBlankShortAnswers++;
                    }

                    continue;
                }

                explicitShortScores++;
                if (!predictionByNumber.TryGetValue(pair.Key, out var observed))
                {
                    shortAbsoluteError += 2;
                    shortUnderCredit++;
                    continue;
                }

                var observedPoints = observed.ProposedPointsMilli / 1_000d;
                var difference = observedPoints - pair.Value;
                shortAbsoluteError += Math.Abs(difference);
                if (difference == 0)
                {
                    exactShortScoreAgreement++;
                }
                else if (difference > 0)
                {
                    shortOverCredit++;
                }
                else
                {
                    shortUnderCredit++;
                }

                if (!systemPredictionByNumber.TryGetValue(
                        pair.Key,
                        out var systemObserved))
                {
                    systemShortAbsoluteError += 2;
                    systemShortUnderCredit++;
                    continue;
                }

                var systemObservedPoints = systemObserved.ProposedPointsMilli / 1_000d;
                var systemDifference = systemObservedPoints - pair.Value;
                systemShortAbsoluteError += Math.Abs(systemDifference);
                if (systemDifference == 0)
                {
                    systemExactShortScoreAgreement++;
                }
                else if (systemDifference > 0)
                {
                    systemShortOverCredit++;
                }
                else
                {
                    systemShortUnderCredit++;
                }
            }
        }

        var japaneseByNumber = japanese.Predictions.ToDictionary(
            prediction => ParseQuestionNumber(prediction.QuestionId));
        var japaneseExactTranscriptions = japanese.Truth.Responses.Count(pair =>
            japaneseByNumber.TryGetValue(pair.Key, out var prediction)
            && Normalize(pair.Value) == Normalize(prediction.Transcription));
        var japaneseScoreAgreement = japanese.Truth.Points.Count(pair =>
            japaneseByNumber.TryGetValue(pair.Key, out var prediction)
            && prediction.ProposedPointsMilli == pair.Value * 1_000);
        var japaneseSystemByNumber = japanese.SystemPredictions.ToDictionary(
            prediction => ParseQuestionNumber(prediction.QuestionId));
        var japaneseSystemScoreAgreement = japanese.Truth.Points.Count(pair =>
            japaneseSystemByNumber.TryGetValue(pair.Key, out var prediction)
            && prediction.ProposedPointsMilli == pair.Value * 1_000);
        return new EvaluationMetrics(
            possible,
            covered,
            Rate(covered, possible),
            runs.Count,
            systemAcceptedRuns,
            systemAcceptedItems,
            Rate(systemAcceptedItems, possible),
            attemptedChoices,
            exactChoiceTranscriptions,
            Rate(exactChoiceTranscriptions, attemptedChoices),
            Wilson95(exactChoiceTranscriptions, attemptedChoices),
            choiceScoreAgreement,
            Rate(choiceScoreAgreement, attemptedChoices),
            Wilson95(choiceScoreAgreement, attemptedChoices),
            choiceTruePositive,
            choiceTrueNegative,
            choiceFalsePositive,
            choiceFalseNegative,
            Rate(choiceTruePositive, choiceTruePositive + choiceFalsePositive),
            Rate(choiceFalsePositive, choiceFalsePositive + choiceTrueNegative),
            Rate(choiceFalseNegative, choiceFalseNegative + choiceTruePositive),
            explicitShortScores,
            exactShortScoreAgreement,
            Rate(exactShortScoreAgreement, explicitShortScores),
            Wilson95(exactShortScoreAgreement, explicitShortScores),
            explicitShortScores == 0
                ? 0
                : shortAbsoluteError / explicitShortScores,
            shortOverCredit,
            shortUnderCredit,
            blankShortAnswers,
            correctlyDetectedBlankShortAnswers,
            Rate(correctlyDetectedBlankShortAnswers, blankShortAnswers),
            japanese.Truth.Responses.Count,
            japaneseExactTranscriptions,
            Rate(japaneseExactTranscriptions, japanese.Truth.Responses.Count),
            japaneseScoreAgreement,
            Rate(japaneseScoreAgreement, japanese.Truth.Points.Count),
            systemDeterministicRecomputedItems,
            systemQuarantinedItems,
            systemChoiceScoreAgreement,
            Rate(systemChoiceScoreAgreement, attemptedChoices),
            systemChoiceTruePositive,
            systemChoiceTrueNegative,
            systemChoiceFalsePositive,
            systemChoiceFalseNegative,
            Rate(
                systemChoiceTruePositive,
                systemChoiceTruePositive + systemChoiceFalsePositive),
            Rate(
                systemChoiceFalsePositive,
                systemChoiceFalsePositive + systemChoiceTrueNegative),
            Rate(
                systemChoiceFalseNegative,
                systemChoiceFalseNegative + systemChoiceTruePositive),
            systemExactShortScoreAgreement,
            Rate(systemExactShortScoreAgreement, explicitShortScores),
            explicitShortScores == 0
                ? 0
                : systemShortAbsoluteError / explicitShortScores,
            systemShortOverCredit,
            systemShortUnderCredit,
            systemCorrectlyDetectedBlankShortAnswers,
            Rate(systemCorrectlyDetectedBlankShortAnswers, blankShortAnswers),
            japaneseSystemScoreAgreement,
            Rate(japaneseSystemScoreAgreement, japanese.Truth.Points.Count));
    }

    private static SystemValidationResult ValidateSystemResponse(
        JsonElement response,
        string requestKey,
        IReadOnlyList<EvaluationQuestion> questions)
    {
        try
        {
            var validated = AiGradingResponseValidator.Validate(
                response,
                requestKey,
                questions.ToDictionary(
                    question => question.Id,
                    ToDomainQuestion,
                    StringComparer.Ordinal));
            var predictions = validated.Observations.Select(observation =>
                    new Prediction(
                        observation.QuestionId,
                        observation.Observation.Transcription,
                        observation.Observation.Quality switch
                        {
                            AnswerQuality.Clear => "clear",
                            AnswerQuality.Ambiguous => "ambiguous",
                            AnswerQuality.Unreadable => "unreadable",
                            AnswerQuality.Cropped => "cropped",
                            _ => throw new InvalidOperationException(
                                "Unsupported answer quality."),
                        },
                        observation.Observation.ExplicitlyBlank,
                        observation.ProposedOutcome,
                        checked((int)observation.ProposedPointsMilli),
                        observation.ProviderConfidenceBasisPoints / 10_000d,
                        observation.ProviderReasonCode))
                .ToArray();
            return new SystemValidationResult(predictions, null);
        }
        catch (InvalidDataException exception)
        {
            return new SystemValidationResult([], exception.Message);
        }
    }

    private static DomainQuestionDefinition ToDomainQuestion(
        EvaluationQuestion question)
    {
        var accepted = new AcceptedAnswer(
            $"{question.Id}-canonical",
            question.AcceptedAnswer,
            AcceptedAnswerVariantType.Canonical,
            AnswerProvenance.TeacherEntered,
            teacherVerified: true);
        var questionType = question.Type switch
        {
            "multiple_choice" => QuestionType.MultipleChoice,
            "exact_short_text" => QuestionType.ExactShortText,
            "semantic_short_text" => QuestionType.SemanticShortText,
            _ => throw new InvalidOperationException("Unsupported evaluation question type."),
        };
        var gradingMode = question.GradingMode switch
        {
            "deterministic" => GradingMode.Deterministic,
            "transcribe_then_rules" => GradingMode.TranscribeThenRules,
            "ai_rubric" => GradingMode.AiRubric,
            _ => throw new InvalidOperationException("Unsupported evaluation grading mode."),
        };
        var rubric = gradingMode == GradingMode.AiRubric
            ? new[]
            {
                new RubricRule(
                    $"{question.Id}-rubric",
                    0,
                    RubricConditionType.ModelAssessed,
                    question.RubricText,
                    new MilliPoints(question.MaximumPointsMilli),
                    teacherVerified: true),
            }
            : [];
        var choices = questionType == QuestionType.MultipleChoice
            ? question.AcceptedAnswer is "ア" or "イ" or "ウ"
                ? new[] { "ア", "イ", "ウ" }
                : new[] { "A", "B", "C", "D" }
            : null;
        return new DomainQuestionDefinition(
            question.Id,
            $"logical-{question.Id}",
            question.Number - 1,
            question.Number.ToString(CultureInfo.InvariantCulture),
            question.Text,
            questionType,
            gradingMode,
            new MilliPoints(question.MaximumPointsMilli),
            new MilliPoints(1_000),
            allowNonKanji: false,
            requiresReviewAlways: questionType == QuestionType.SemanticShortText,
            teacherVerified: true,
            acceptedAnswers: [accepted],
            rubricRules: rubric,
            choicePolicy: choices is null
                ? null
                : new ChoiceAnswerPolicy(question.AcceptedAnswer, choices));
    }

    private static EvaluationQuestion[] LoadQuestions(
        string fixtureDirectory)
    {
        var answerRows = File.ReadLines(Path.Combine(fixtureDirectory, "answerkey.txt"))
            .Skip(1)
            .Select(ParseCsvLine)
            .ToDictionary(
                row => int.Parse(row[0], CultureInfo.InvariantCulture),
                row => (Type: row[1], Answer: row[2]));
        var text = ParseQuestionText(Path.Combine(fixtureDirectory, "Question.txt"));
        return answerRows.OrderBy(pair => pair.Key)
            .Select(pair => new EvaluationQuestion(
                pair.Key,
                $"q{pair.Key}",
                text[pair.Key],
                pair.Value.Type == "MCQ" ? "multiple_choice" : "semantic_short_text",
                pair.Value.Type == "MCQ" ? "deterministic" : "ai_rubric",
                pair.Value.Type == "MCQ" ? 1_000 : 2_000,
                pair.Value.Answer,
                pair.Value.Type == "MCQ"
                    ? $"Award one point only for choice {pair.Value.Answer}."
                    : $"Award 0, 1, or 2 points by semantic agreement with this teacher answer: {pair.Value.Answer}"))
            .ToArray();
    }

    private static StudentTruth[] LoadStudentTruth(
        string fixtureDirectory)
    {
        var lines = File.ReadAllLines(
            Path.Combine(fixtureDirectory, "Teacher_manual_marks_Anonymized.csv"));
        var headers = ParseCsvLine(lines[0]);
        var wanted = new HashSet<string>(
            ["Student_18", "Student_19", "Student_26"],
            StringComparer.Ordinal);
        var answerKey = File.ReadLines(Path.Combine(fixtureDirectory, "answerkey.txt"))
            .Skip(1)
            .Select(ParseCsvLine)
            .ToDictionary(
                row => int.Parse(row[0], CultureInfo.InvariantCulture),
                row => row[2]);
        return lines.Skip(1)
            .Select(ParseCsvLine)
            .Where(row => wanted.Contains(row[0]))
            .Select(row =>
            {
                var values = headers.Zip(row).ToDictionary(
                    pair => pair.First,
                    pair => pair.Second,
                    StringComparer.Ordinal);
                var responses = Enumerable.Range(1, 35).ToDictionary(
                    number => number,
                    number => number <= 20
                        ? values[number.ToString(CultureInfo.InvariantCulture)]
                        : values[number.ToString(CultureInfo.InvariantCulture)]);
                var points = new Dictionary<int, int>();
                for (var number = 1; number <= 20; number++)
                {
                    var observed = responses[number];
                    points[number] = IsChoice(observed)
                        && string.Equals(
                            observed,
                            answerKey[number],
                            StringComparison.OrdinalIgnoreCase)
                            ? 1
                            : 0;
                }

                for (var number = 21; number <= 35; number++)
                {
                    var value = responses[number];
                    points[number] = string.IsNullOrWhiteSpace(value)
                        ? 0
                        : checked((int)double.Parse(
                            value,
                            CultureInfo.InvariantCulture));
                }

                return new StudentTruth(row[0], responses, points);
            })
            .OrderBy(item => item.StudentId, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<int, string> ParseQuestionText(string path)
    {
        var result = new Dictionary<int, string>();
        int? current = null;
        var builder = new StringBuilder();
        void Store()
        {
            if (current.HasValue)
            {
                result[current.Value] = builder.ToString().Trim();
            }

            builder.Clear();
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('.', StringComparison.Ordinal);
            if (separator > 0
                && int.TryParse(
                    line.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number)
                && number is >= 1 and <= 35)
            {
                Store();
                current = number;
                builder.Append(line[(separator + 1)..].Trim());
            }
            else if (current.HasValue
                && line.Length > 0
                && !line.StartsWith("Part ", StringComparison.Ordinal)
                && !line.StartsWith("Instructions:", StringComparison.Ordinal))
            {
                builder.Append(' ').Append(line);
            }
        }

        Store();
        return result;
    }

    private static IReadOnlyList<EvaluationQuestion> JapaneseQuestions() =>
    [
        new(1, "q1", "日本の首都を漢字で書きなさい。", "exact_short_text", "transcribe_then_rules", 8_000, "東京", "東京のみを正答とする。"),
        new(2, "q2", "ASEAN（アセアン）を日本語で何というか。", "exact_short_text", "transcribe_then_rules", 10_000, "東南アジア諸国連合", "正式な日本語名称を正答とする。"),
        new(3, "q3", "インドで最も多くの人が信仰している宗教を書きなさい。", "exact_short_text", "transcribe_then_rules", 8_000, "ヒンドゥー教", "ヒンドゥー教を正答とする。"),
        new(4, "q4", "東南アジアの気候として最も適切なものを選びなさい。", "multiple_choice", "deterministic", 8_000, "イ", "選択肢イのみを正答とする。"),
        new(5, "q5", "輸出品の変化を工業化という言葉を使って説明しなさい。", "semantic_short_text", "ai_rubric", 16_000, "工業化が進み、天然ゴム中心から機械類中心へ変化した。", "工業化、天然ゴム中心から機械類中心への変化を含む。"),
    ];

    private static MediaFile[] LoadPages(
        string directory,
        string studentId) => Directory.GetFiles(
            directory,
            $"{studentId}-page-*.png")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(LoadMedia)
        .ToArray();

    private static MediaFile LoadMedia(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new MediaFile(path, bytes, Sha256(bytes));
    }

    private static AiConnectionSettings Connection(string modelId) => new(
        "basic-grading-accuracy-evaluation",
        AiProviders.GeminiDirect,
        new Uri("https://generativelanguage.googleapis.com/"),
        modelId,
        TimeSpan.FromMinutes(5));

    private static string ResolveThinkingLevel(string modelId)
    {
        var configured = OptionalValue("OOKI_GRADING_EVAL_THINKING_LEVEL");
        var resolved = configured?.ToUpperInvariant()
            ?? (modelId.StartsWith(
                    "gemini-3.1-pro",
                    StringComparison.OrdinalIgnoreCase)
                ? "LOW"
                : "MINIMAL");
        if (resolved is not ("MINIMAL" or "LOW" or "MEDIUM" or "HIGH"))
        {
            throw new InvalidOperationException(
                "OOKI_GRADING_EVAL_THINKING_LEVEL must be MINIMAL, LOW, MEDIUM, or HIGH.");
        }

        if (resolved == "MINIMAL"
            && modelId.StartsWith(
                "gemini-3.1-pro",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Gemini 3.1 Pro does not support the MINIMAL thinking level.");
        }

        return resolved;
    }

    private static string SafeFileName(string modelId)
    {
        var value = string.Concat(modelId.Select(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-'
                ? character
                : '_'));
        return value.Length > 0 ? value : "gemini-model";
    }

    private static string RequiredDirectory(string variable)
    {
        var path = Path.GetFullPath(RequiredValue(variable));
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"{variable} does not exist.");
        }

        return path;
    }

    private static string RequiredValue(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{variable} is required.");

    private static string? OptionalValue(string variable) =>
        Environment.GetEnvironmentVariable(variable)?.Trim() is { Length: > 0 } value
            ? value
            : null;

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (current == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(current);
            }
        }

        values.Add(value.ToString());
        return values.ToArray();
    }

    private static int ParseQuestionNumber(string questionId) =>
        int.Parse(questionId.AsSpan(1), CultureInfo.InvariantCulture);

    private static bool IsChoice(string value) => value.Trim() is
        "A" or "B" or "C" or "D";

    private static string Normalize(string value) => value.Trim().Normalize();

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : numerator / (double)denominator;

    private static ConfidenceInterval Wilson95(int successes, int total)
    {
        if (total == 0)
        {
            return new ConfidenceInterval(0, 0);
        }

        const double z = 1.959963984540054;
        var proportion = successes / (double)total;
        var denominator = 1 + (z * z / total);
        var center = (proportion + (z * z / (2 * total))) / denominator;
        var margin = z * Math.Sqrt(
            (proportion * (1 - proportion) / total)
            + (z * z / (4d * total * total))) / denominator;
        return new ConfidenceInterval(
            Math.Max(0, center - margin),
            Math.Min(1, center + margin));
    }

    private static string Sha256(string path) => Sha256(File.ReadAllBytes(path));

    private static string Sha256(byte[] bytes) => Convert.ToHexString(
            SHA256.HashData(bytes))
        .ToLowerInvariant();

    private sealed class LiveAccuracyFactAttribute : FactAttribute
    {
        public LiveAccuracyFactAttribute(params string[] requiredVariables)
        {
            var missing = requiredVariables.Where(variable =>
                    string.IsNullOrWhiteSpace(
                        Environment.GetEnvironmentVariable(variable)))
                .ToArray();
            if (missing.Length > 0)
            {
                Skip = "Live grading accuracy evaluation requires: "
                    + string.Join(", ", missing);
            }
        }
    }

    private sealed record EvaluationQuestion(
        int Number,
        string Id,
        string Text,
        string Type,
        string GradingMode,
        int MaximumPointsMilli,
        string AcceptedAnswer,
        string RubricText);

    private sealed record MediaFile(string Path, byte[] Bytes, string Sha256);

    private sealed record Prediction(
        string QuestionId,
        string Transcription,
        string Legibility,
        bool Blank,
        string ProposedOutcome,
        int ProposedPointsMilli,
        double Confidence,
        string? ReconciliationReasonCode = null);

    private sealed record SystemValidationResult(
        IReadOnlyList<Prediction> Predictions,
        string? ErrorCode);

    private sealed record StudentTruth(
        string StudentId,
        IReadOnlyDictionary<int, string> Responses,
        IReadOnlyDictionary<int, int> Points);

    private sealed record EvaluationRun(
        string StudentId,
        string? ActualModel,
        double LatencyMilliseconds,
        AiUsage Usage,
        IReadOnlyList<string> MediaSha256,
        IReadOnlyList<Prediction> Predictions,
        IReadOnlyList<Prediction> SystemPredictions,
        string? SystemValidationErrorCode,
        StudentTruth Truth);

    private sealed record ConfidenceInterval(double Lower, double Upper);

    private sealed record EvaluationMetrics(
        int RealItemSlots,
        int RealItemsReturned,
        double ResultCoverage,
        int RealRuns,
        int SystemAcceptedRuns,
        int SystemAcceptedItems,
        double SystemUsableCoverage,
        int AttemptedChoices,
        int ExactChoiceTranscriptions,
        double ExactChoiceTranscriptionRate,
        ConfidenceInterval ExactChoiceTranscriptionWilson95,
        int ChoiceScoreAgreement,
        double ChoiceScoreAgreementRate,
        ConfidenceInterval ChoiceScoreAgreementWilson95,
        int ChoiceTruePositive,
        int ChoiceTrueNegative,
        int ChoiceFalsePositive,
        int ChoiceFalseNegative,
        double ChoiceAutoCreditPrecision,
        double ChoiceIncorrectCreditFalsePositiveRate,
        double ChoiceUnderCreditFalseNegativeRate,
        int ExplicitShortAnswerScores,
        int ExactShortAnswerScoreAgreement,
        double ExactShortAnswerScoreAgreementRate,
        ConfidenceInterval ExactShortAnswerScoreAgreementWilson95,
        double ShortAnswerMeanAbsoluteErrorPoints,
        int ShortAnswerOverCreditCount,
        int ShortAnswerUnderCreditCount,
        int BlankShortAnswers,
        int CorrectBlankShortAnswers,
        double BlankShortAnswerAccuracy,
        int JapaneseSyntheticItems,
        int JapaneseSyntheticExactTranscriptions,
        double JapaneseSyntheticExactTranscriptionRate,
        int JapaneseSyntheticScoreAgreement,
        double JapaneseSyntheticScoreAgreementRate,
        int SystemDeterministicRecomputedItems,
        int SystemQuarantinedItems,
        int SystemChoiceScoreAgreement,
        double SystemChoiceScoreAgreementRate,
        int SystemChoiceTruePositive,
        int SystemChoiceTrueNegative,
        int SystemChoiceFalsePositive,
        int SystemChoiceFalseNegative,
        double SystemChoiceAutoCreditPrecision,
        double SystemChoiceIncorrectCreditFalsePositiveRate,
        double SystemChoiceUnderCreditFalseNegativeRate,
        int SystemExactShortAnswerScoreAgreement,
        double SystemExactShortAnswerScoreAgreementRate,
        double SystemShortAnswerMeanAbsoluteErrorPoints,
        int SystemShortAnswerOverCreditCount,
        int SystemShortAnswerUnderCreditCount,
        int SystemCorrectBlankShortAnswers,
        double SystemBlankShortAnswerAccuracy,
        int SystemJapaneseSyntheticScoreAgreement,
        double SystemJapaneseSyntheticScoreAgreementRate);

    private sealed record EvaluationEvidence(
        int SchemaVersion,
        string EvidenceClass,
        DateTimeOffset EvaluatedAt,
        string RequestedModel,
        string ThinkingLevel,
        string PromptVersion,
        string ResponseSchemaVersion,
        string PromptContentHash,
        string Pipeline,
        string DatasetDescription,
        IReadOnlyDictionary<string, string> SourceSha256,
        IReadOnlyDictionary<string, string> EvaluationMediaSha256,
        IReadOnlyList<EvaluationRun> RealHandwrittenRuns,
        EvaluationRun JapaneseSyntheticRun,
        EvaluationMetrics Metrics,
        IReadOnlyList<string> Limitations);
}

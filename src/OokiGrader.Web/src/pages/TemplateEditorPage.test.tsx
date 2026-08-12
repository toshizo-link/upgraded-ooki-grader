import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../lib/api";
import type { TemplateQuestion } from "../types";
import {
  answersForQuestionEdit,
  changesForGradingPreset,
  DEFAULT_AI_RUBRIC,
  defaultPointIncrementMilli,
  defaultsForQuestionTypeChange,
  gradingPresetForQuestion,
  needsIndividualReview,
  needsProposalVerification,
  allowNonKanjiForKanjiRequired,
  isKanjiRequired,
  isTemplateEditorReadOnly,
  QuestionProperties,
  newQuestionPayload,
  questionPayload,
  templateSourcePreviewKind,
  templateSourceRoleLabel,
  validationFromPublishError,
} from "./TemplateEditorPage";

afterEach(cleanup);

function generatedQuestion(
  changes: Partial<TemplateQuestion> = {},
): TemplateQuestion {
  return {
    id: "question-1",
    displayLabel: "問1",
    order: 1,
    questionText: "日本の首都を答えなさい。",
    questionType: "exact_short_text",
    gradingMode: "transcribe_then_rules",
    maxPointsMilli: 1000,
    pointIncrementMilli: 1000,
    allowNonKanji: false,
    requiresCompleteAnswer: false,
    answerOrderInsensitive: false,
    acceptedAnswers: [
      {
        id: "answer-1",
        text: "東京",
        variantType: "canonical",
        provenance: "ai_proposed",
        teacherVerified: false,
      },
    ],
    canonicalAnswer: "東京",
    requiresReviewAlways: false,
    answerRegion: {
      pageNumber: 1,
      xMillionths: 100_000,
      yMillionths: 200_000,
      widthMillionths: 300_000,
      heightMillionths: 80_000,
    },
    questionRegion: {
      pageNumber: 1,
      xMillionths: 100_000,
      yMillionths: 100_000,
      widthMillionths: 600_000,
      heightMillionths: 80_000,
    },
    proposalState: "proposed",
    teacherVerified: false,
    warnings: [
      "先生による確認が必要です。",
      "未確認の解答候補があります。",
    ],
    revision: 1,
    ...changes,
  };
}

describe("template proposal review routing", () => {
  it("routes a complete generated question to safe bulk verification", () => {
    const question = generatedQuestion();

    expect(needsProposalVerification(question)).toBe(true);
    expect(needsIndividualReview(question)).toBe(false);
  });

  it("keeps substantive extraction warnings in individual review", () => {
    const question = generatedQuestion({
      warnings: [
        "先生による確認が必要です。",
        "解答欄が別の問題と重なっています。",
      ],
    });

    expect(needsIndividualReview(question)).toBe(true);
  });

  it("mirrors server-side substantive AI notes in the exception list", () => {
    expect(
      needsIndividualReview(
        generatedQuestion({
          teacherNote:
            "[AI確認] 正答はAIによる提案です。先生が根拠資料と照合してください。",
        }),
      ),
    ).toBe(false);
    expect(
      needsIndividualReview(
        generatedQuestion({
          teacherNote:
            "[AI確認] [answer.source_conflict_or_ambiguity] 模範解答に不一致があります。",
        }),
      ),
    ).toBe(true);
  });

  it("keeps always-review proposals but ignores legacy coordinates", () => {
    expect(
      needsIndividualReview(
        generatedQuestion({
          requiresReviewAlways: true,
        }),
      ),
    ).toBe(true);
    expect(
      needsIndividualReview(
        generatedQuestion({
          questionRegion: undefined,
        }),
      ),
    ).toBe(false);
  });

  it.each([
    "semantic_short_text",
    "multi_part",
    "subjective",
    "unsupported",
  ])("keeps %s proposals out of bulk verification", (questionType) => {
    expect(
      needsIndividualReview(
        generatedQuestion({
          questionType,
        }),
      ),
    ).toBe(true);
  });

  it("does not keep an already verified subjective question in template review", () => {
    const question = generatedQuestion({
      questionType: "subjective",
      teacherVerified: true,
      acceptedAnswers: [
        {
          text: "採点基準を参照",
          variantType: "canonical",
          teacherVerified: true,
        },
      ],
      warnings: [],
    });

    expect(needsProposalVerification(question)).toBe(false);
    expect(needsIndividualReview(question)).toBe(false);
  });
});

describe("template source preview", () => {
  it.each([
    ["application/pdf", "scan", "pdf"],
    [undefined, "中1社会.pdf", "pdf"],
    ["image/png", "scan", "image"],
    [undefined, "問題用紙.JPEG", "image"],
    ["image/tiff", "問題用紙.tiff", "unsupported"],
  ] as const)(
    "uses the appropriate viewer for %s / %s",
    (mimeType, displayName, expected) => {
      expect(templateSourcePreviewKind({ mimeType, displayName })).toBe(
        expected,
      );
    },
  );

  it("labels non-model answers without implying they are authoritative", () => {
    expect(templateSourceRoleLabel("containsNonModelAnswers")).toBe(
      "記入済み答案（正解には不使用）",
    );
  });
});

describe("question grading rules", () => {
  it.each([
    "multiple_choice",
    "numeric",
    "exact_short_text",
    "semantic_short_text",
    "multi_part",
    "subjective",
  ])(
    "defaults %s to AI rubric grading when the teacher changes the type",
    (questionType) => {
      expect(
        defaultsForQuestionTypeChange(generatedQuestion(), questionType),
      ).toEqual({
        questionType,
        gradingMode: "ai_rubric",
        rubric: DEFAULT_AI_RUBRIC,
        requiresReviewAlways: false,
      });
    },
  );

  it("keeps unsupported extraction in the safe manual fallback", () => {
    expect(
      defaultsForQuestionTypeChange(generatedQuestion(), "unsupported"),
    ).toEqual({
      questionType: "unsupported",
      gradingMode: "manual",
      requiresReviewAlways: false,
    });
  });

  it("preserves an existing rubric when the question type changes", () => {
    expect(
      defaultsForQuestionTypeChange(
        generatedQuestion({ rubric: "先生が設定した採点基準" }),
        "subjective",
      ),
    ).toEqual({
      questionType: "subjective",
      gradingMode: "ai_rubric",
      requiresReviewAlways: false,
    });
  });

  it("creates a new question with simple one-point AI defaults", () => {
    expect(newQuestionPayload(2, 5000)).toMatchObject({
      displayLabel: "問3",
      order: 3,
      gradingMode: "ai_rubric",
      pointIncrementMilli: 1000,
      rubric: DEFAULT_AI_RUBRIC,
      requiresReviewAlways: false,
    });
  });

  it("uses the largest safe increment up to one point for unusual totals", () => {
    expect(defaultPointIncrementMilli(5000)).toBe(1000);
    expect(defaultPointIncrementMilli(1750)).toBe(250);
    expect(newQuestionPayload(0, 1750).pointIncrementMilli).toBe(250);
  });

  it("maps the simplified teacher choices to explicit grading settings", () => {
    const question = generatedQuestion({
      gradingMode: "ai_rubric",
      rubric: DEFAULT_AI_RUBRIC,
    });

    expect(gradingPresetForQuestion(question)).toBe("ai");
    expect(changesForGradingPreset(question, "exact")).toEqual({
      questionType: "exact_short_text",
      gradingMode: "transcribe_then_rules",
      requiresReviewAlways: false,
    });
    expect(changesForGradingPreset(question, "numeric")).toEqual({
      questionType: "numeric",
      gradingMode: "transcribe_then_rules",
      requiresReviewAlways: false,
    });
    expect(changesForGradingPreset(question, "manual")).toEqual({
      questionType: "subjective",
      gradingMode: "manual",
      requiresReviewAlways: false,
    });
  });

  it("keeps an imported or copied explicit manual mode unchanged", () => {
    const question = generatedQuestion({
      questionType: "subjective",
      gradingMode: "manual",
    });

    expect(questionPayload(question)).toMatchObject({
      questionType: "subjective",
      gradingMode: "manual",
    });
  });

  it("selects AI rubric grading when a question is changed to descriptive", () => {
    const onChange = vi.fn();

    render(
      <QuestionProperties
        question={generatedQuestion()}
        readOnly={false}
        onChange={onChange}
        onAccept={() => undefined}
        accepting={false}
        acceptDisabled={false}
      />,
    );

    fireEvent.change(screen.getByLabelText("解答形式（詳細）"), {
      target: { value: "subjective" },
    });

    expect(onChange).toHaveBeenLastCalledWith({
      questionType: "subjective",
      gradingMode: "ai_rubric",
      rubric: DEFAULT_AI_RUBRIC,
      requiresReviewAlways: false,
    });
    expect(
      screen.getByRole("option", { name: "記述（AIが採点）" }),
    ).toBeInTheDocument();
  });

  it("shows a loaded manual descriptive question without rewriting it", () => {
    const onChange = vi.fn();

    render(
      <QuestionProperties
        question={
          generatedQuestion({
            questionType: "subjective",
            gradingMode: "manual",
          })
        }
        readOnly={false}
        onChange={onChange}
        onAccept={() => undefined}
        accepting={false}
        acceptDisabled={false}
      />,
    );

    expect(screen.getByLabelText("採点方法")).toHaveValue("manual");
    expect(onChange).not.toHaveBeenCalled();
  });

  it("offers one primary AI-first grading selector", () => {
    const onChange = vi.fn();
    render(
      <QuestionProperties
        question={generatedQuestion()}
        readOnly={false}
        onChange={onChange}
        onAccept={() => undefined}
        accepting={false}
        acceptDisabled={false}
      />,
    );

    fireEvent.change(screen.getByLabelText("採点方法"), {
      target: { value: "ai" },
    });

    expect(onChange).toHaveBeenLastCalledWith({
      questionType: "exact_short_text",
      gradingMode: "ai_rubric",
      rubric: DEFAULT_AI_RUBRIC,
      requiresReviewAlways: false,
    });
    expect(
      screen.getByRole("option", { name: "AIで判定（おすすめ）" }),
    ).toBeInTheDocument();
  });

  it("maps the positive 漢字必須 option to the compatible inverse field", () => {
    expect(isKanjiRequired(generatedQuestion())).toBe(true);
    expect(allowNonKanjiForKanjiRequired(true)).toBe(false);
    expect(allowNonKanjiForKanjiRequired(false)).toBe(true);
  });

  it("sends 完答 and 順不同 independently", () => {
    const completeOnly = questionPayload(
      generatedQuestion({
        requiresCompleteAnswer: true,
        answerOrderInsensitive: false,
      }),
    );
    const unorderedOnly = questionPayload(
      generatedQuestion({
        requiresCompleteAnswer: false,
        answerOrderInsensitive: true,
      }),
    );

    expect(completeOnly).toMatchObject({
      requiresCompleteAnswer: true,
      answerOrderInsensitive: false,
    });
    expect(unorderedOnly).toMatchObject({
      requiresCompleteAnswer: false,
      answerOrderInsensitive: true,
    });
  });

  it("keeps accepted forms, phonetic exceptions, and other typed answers separate", () => {
    const question = generatedQuestion({
      acceptedAnswers: [
        {
          id: "canonical",
          text: "漢字",
          variantType: "canonical",
          provenance: "provided_model_answer",
        },
        {
          id: "accepted",
          text: "旧字体",
          variantType: "accepted",
          provenance: "teacher_entered",
        },
        {
          id: "exception",
          text: "かんじ",
          variantType: "explicitException",
          provenance: "teacher_entered",
        },
        {
          id: "pattern",
          text: "^漢字$",
          variantType: "regex_restricted",
          provenance: "teacher_entered",
        },
      ],
      canonicalAnswer: "漢字",
    });

    const acceptedAnswers = answersForQuestionEdit(
      question,
      "漢字",
      "旧字体\n異体字",
      "かんじ\nカンジ",
    );
    const payload = questionPayload({ ...question, acceptedAnswers });

    expect(payload.acceptedAnswers).toEqual([
      expect.objectContaining({
        id: "canonical",
        text: "漢字",
        variantType: "canonical",
      }),
      expect.objectContaining({
        id: "accepted",
        text: "旧字体",
        variantType: "accepted",
      }),
      { text: "異体字", variantType: "accepted" },
      expect.objectContaining({
        id: "exception",
        text: "かんじ",
        variantType: "explicitException",
      }),
      { text: "カンジ", variantType: "explicitException" },
      expect.objectContaining({
        id: "pattern",
        text: "^漢字$",
        variantType: "regex_restricted",
      }),
    ]);
  });

  it("shows a dedicated phonetic-exception field and preserves it when variants change", () => {
    const onChange = vi.fn();
    const question = generatedQuestion({
      acceptedAnswers: [
        { text: "漢字", variantType: "canonical" },
        { text: "別表記", variantType: "accepted" },
        {
          id: "exception",
          text: "かんじ",
          variantType: "explicitException",
          provenance: "teacher_entered",
        },
      ],
      canonicalAnswer: "漢字",
      allowNonKanji: false,
    });

    render(
      <QuestionProperties
        question={question}
        readOnly={false}
        onChange={onChange}
        onAccept={() => undefined}
        accepting={false}
        acceptDisabled={false}
      />,
    );

    expect(screen.getByLabelText("漢字必須の例外（読み）")).toHaveValue(
      "かんじ",
    );
    fireEvent.change(screen.getByLabelText("正解として認める別表記"), {
      target: { value: "新しい別表記" },
    });

    expect(onChange).toHaveBeenLastCalledWith(
      expect.objectContaining({
        acceptedAnswers: expect.arrayContaining([
          expect.objectContaining({
            id: "exception",
            text: "かんじ",
            variantType: "explicitException",
          }),
        ]),
      }),
    );
  });
});

describe("publish validation recovery", () => {
  it("shows every publish blocker and resolves question paths", () => {
    const questions = [
      generatedQuestion({ id: "question-1", order: 1 }),
      generatedQuestion({ id: "question-2", order: 2 }),
    ];
    const validation = validationFromPublishError(
      new ApiError(422, {
        code: "TEMPLATE_PUBLISH_BLOCKED",
        title: "受付開始前の確認が必要です",
        errors: [
          {
            field: "questions[1].rubricRules",
            code: "rubric.required",
            message: "問2の採点基準を確認してください。",
          },
          {
            code: "template.source_required",
            message: "問題用紙を確認してください。",
          },
        ],
      }),
      questions,
    );

    expect(validation?.issues).toEqual([
      expect.objectContaining({
        questionId: "question-2",
        message: "問2の採点基準を確認してください。",
      }),
      expect.objectContaining({
        questionId: undefined,
        message: "問題用紙を確認してください。",
      }),
    ]);
  });
});

describe("template editor lifecycle", () => {
  it("is read-only for archived drafts", () => {
    expect(
      isTemplateEditorReadOnly(
        { lifecycleState: "archived" },
        { state: "draft" },
      ),
    ).toBe(true);
  });

  it.each(["published", "superseded", "retired", "generating", "validating"])(
    "is read-only for a %s version",
    (state) => {
      expect(
        isTemplateEditorReadOnly(
          { lifecycleState: "active" },
          { state },
        ),
      ).toBe(true);
    },
  );

  it("allows editing only for a non-archived draft", () => {
    expect(
      isTemplateEditorReadOnly(
        { lifecycleState: "draft" },
        { state: "draft" },
      ),
    ).toBe(false);
  });
});

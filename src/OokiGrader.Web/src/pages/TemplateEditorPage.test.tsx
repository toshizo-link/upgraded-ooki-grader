import { describe, expect, it } from "vitest";
import type { TemplateQuestion } from "../types";
import {
  needsIndividualReview,
  needsProposalVerification,
  templateSourcePreviewKind,
  templateSourceRoleLabel,
} from "./TemplateEditorPage";

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

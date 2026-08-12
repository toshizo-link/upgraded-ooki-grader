import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter, Route, Routes } from "../router";
import type { GradeReviewItem, PagedResponse } from "../types";
import { ReviewPage } from "./ReviewPage";

const gradeQueue: PagedResponse<GradeReviewItem> = {
  items: [
    {
      id: "review-1",
      submissionId: "submission/1",
      resultId: "result-1",
      sourceResultRevision: 3,
      studentDisplayName: "山田 太郎",
      testTitle: "理科6年STEPセット1-1",
      questionId: "question-1",
      questionLabel: "大問1",
      questionText: "植物の働きを答えなさい",
      expectedAnswers: ["蒸散"],
      transcription: "蒸発",
      proposedOutcome: "incorrect",
      proposedPointsMilli: 0,
      maxPointsMilli: 2000,
      pointIncrementMilli: 1000,
      warning: "確認してください",
    },
  ],
  nextCursor: null,
  totalApproximate: 1,
};

vi.mock("../auth/SessionContext", () => ({
  useSession: () => ({ hasAnyRole: () => true }),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: (key: string) => ({
    data:
      key === "review-grading"
        ? gradeQueue
        : { items: [], nextCursor: null, totalApproximate: 0 },
    error: undefined,
    status: "success" as const,
    reload: vi.fn(),
  }),
}));

beforeEach(() => {
  window.history.replaceState(null, "", "/review?tab=grading");
});

afterEach(() => cleanup());

describe("ReviewPage grading workspace navigation", () => {
  it("links the selected queue item to its whole-submission workspace", async () => {
    render(
      <BrowserRouter>
        <Routes>
          <Route path="/review" element={<ReviewPage />} />
          <Route
            path="/submissions/:submissionId/grading"
            element={<div>答案別採点画面</div>}
          />
        </Routes>
      </BrowserRouter>,
    );

    const link = await screen.findByRole("link", {
      name: "この答案をまとめて確認",
    });
    expect(link).toHaveAttribute(
      "href",
      "/submissions/submission%2F1/grading",
    );
  });
});

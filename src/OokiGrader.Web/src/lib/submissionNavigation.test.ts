import { describe, expect, it } from "vitest";
import { submissionWorkflowHref } from "./submissionNavigation";

describe("submissionWorkflowHref", () => {
  it.each([
    ["needs_name_review", "/review?tab=name&submission=answer%2F1"],
    ["needsGradeReview", "/submissions/answer%2F1/grading"],
    ["ready_to_finalize", "/submissions/answer%2F1/grading"],
    ["finalized", "/results/answer%2F1"],
  ])("routes %s to its matching workflow", (state, expected) => {
    expect(submissionWorkflowHref({ id: "answer/1", state })).toBe(expected);
  });

  it("does not send in-progress work to an unrelated review queue", () => {
    expect(
      submissionWorkflowHref({ id: "answer-1", state: "grading" }),
    ).toBeUndefined();
  });
});

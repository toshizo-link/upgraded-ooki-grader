import type { SubmissionSummary } from "../types";

type SubmissionNavigationSource = Pick<SubmissionSummary, "id" | "state">;

export function submissionWorkflowHref({
  id,
  state,
}: SubmissionNavigationSource): string | undefined {
  const encodedId = encodeURIComponent(id);
  if (state === "finalized") return `/results/${encodedId}`;
  if (state === "needs_name_review" || state === "needsNameReview") {
    return `/review?tab=name&submission=${encodedId}`;
  }
  if (state === "needs_grade_review" || state === "needsGradeReview") {
    return `/submissions/${encodedId}/grading`;
  }
  if (state === "ready_to_finalize" || state === "readyToFinalize") {
    return `/submissions/${encodedId}/grading`;
  }
  return undefined;
}

import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter, Route, Routes } from "../router";
import type { ResultDetail, RuntimeCapabilities } from "../types";
import { ResultDetailPage } from "./ResultDetailPage";

const roleState = vi.hoisted(() => ({ canCorrect: true }));
const apiState = vi.hoisted(() => ({ post: vi.fn() }));

const result: ResultDetail = {
  submissionId: "submission/1",
  resultRevision: 4,
  student: {
    id: "student-1",
    displayName: "山田 太郎",
    studentNumber: "S001",
  },
  testTitle: "理科6年STEPセット1-1",
  testDate: "2026-08-11T00:00:00Z",
  templateVersionNumber: 2,
  earnedPointsMilli: 8000,
  possiblePointsMilli: 10000,
  percentageBasisPoints: 8000,
  status: "finalized",
  scanAvailable: true,
  questions: [],
  finalizedAt: "2026-08-11T01:00:00Z",
};

const capabilities: RuntimeCapabilities = {
  reports: { pdfExport: true },
  ai: {
    provider: "geminiDirect",
    modelId: "gemini-test",
    templateGeneration: { enabled: true, ready: true },
    nameTranscription: { enabled: true, ready: true },
    semanticGrading: { enabled: true, ready: true },
    geminiBatch: { enabled: false, ready: false },
    openRouterEnabled: false,
  },
  safety: {
    automaticAssignment: false,
    automaticFinalization: false,
  },
};

vi.mock("../auth/SessionContext", () => ({
  useSession: () => ({ hasAnyRole: () => roleState.canCorrect }),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: (key: string) => ({
    data: key.startsWith("result:")
      ? result
      : key === "runtime-capabilities"
        ? capabilities
        : { items: [], nextCursor: null, totalApproximate: 0 },
    error: undefined,
    status: "success" as const,
    reload: vi.fn(),
  }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return {
    ...actual,
    api: { ...actual.api, post: apiState.post },
  };
});

beforeEach(() => {
  window.history.replaceState(null, "", "/results/submission%2F1");
  roleState.canCorrect = true;
  apiState.post.mockReset();
  apiState.post.mockResolvedValue({});
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("ResultDetailPage grading workspace navigation", () => {
  it("links a teacher to the whole-answer workspace and reopens into it", async () => {
    renderPage();

    expect(
      screen.getByRole("link", { name: "答案全体を見る" }),
    ).toHaveAttribute("href", "/submissions/submission%2F1/grading");

    fireEvent.click(screen.getByRole("button", { name: "採点を修正" }));
    fireEvent.click(
      screen.getByRole("button", { name: "開き直して採点へ" }),
    );

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/submissions/submission%2F1:reopen",
        {
          reasonCode: "teacher_judgment",
          note: "",
          sourceRevision: 4,
        },
        { idempotencyKey: expect.any(String) },
      ),
    );
    expect(await screen.findByText("答案別採点画面")).toBeVisible();
  });

  it("does not expose the teacher-only workspace to a read-only reviewer", () => {
    roleState.canCorrect = false;
    renderPage();

    expect(
      screen.queryByRole("link", { name: "答案全体を見る" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "採点を修正" }),
    ).not.toBeInTheDocument();
  });
});

function renderPage() {
  render(
    <BrowserRouter>
      <Routes>
        <Route path="/results/:submissionId" element={<ResultDetailPage />} />
        <Route
          path="/submissions/:submissionId/grading"
          element={<div>答案別採点画面</div>}
        />
      </Routes>
    </BrowserRouter>,
  );
}

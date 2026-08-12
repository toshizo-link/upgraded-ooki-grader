import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter, Route, Routes } from "../router";
import type { TestSessionSummary } from "../types";
import { SessionDetailPage } from "./SessionDetailPage";

const queryState = vi.hoisted(() => ({
  session: undefined as TestSessionSummary | undefined,
  reloadSession: vi.fn(),
  reloadSubmissions: vi.fn(),
  reloadSummary: vi.fn(),
}));
const apiState = vi.hoisted(() => ({ post: vi.fn() }));

vi.mock("../auth/SessionContext", () => ({
  useSession: () => ({
    hasAnyRole: (...roles: string[]) =>
      roles.includes("administrator") || roles.includes("teacher"),
  }),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: (key: string) => {
    if (key.startsWith("session:")) {
      return {
        data: queryState.session,
        error: undefined,
        status: "success" as const,
        reload: queryState.reloadSession,
      };
    }
    if (key.startsWith("session-summary:")) {
      return {
        data: {
          submissionCount: 0,
          finalizedCount: 0,
          attentionCount: 0,
        },
        error: undefined,
        status: "success" as const,
        reload: queryState.reloadSummary,
      };
    }
    return {
      data: { items: [], totalApproximate: 0 },
      error: undefined,
      status: "success" as const,
      reload: queryState.reloadSubmissions,
    };
  },
}));

vi.mock("../components/OrderedScanUploadBoard", () => ({
  OrderedScanUploadBoard: ({ isOpen }: { isOpen: boolean }) => (
    <div data-testid="ordered-scan-board">
      {isOpen ? "答案受付中" : "答案受付停止中"}
    </div>
  ),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>(
    "../lib/api",
  );
  return {
    ...actual,
    api: { ...actual.api, post: apiState.post },
  };
});

beforeEach(() => {
  window.history.replaceState(null, "", "/sessions/session-1");
  queryState.session = makeSession();
  apiState.post.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("SessionDetailPage archival", () => {
  it("uses the canonical template title as the session heading", () => {
    renderPage();

    expect(
      screen.getByRole("heading", { level: 1, name: "4年理科 HOP" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("heading", { level: 1, name: "4年理科 HOP 8月" }),
    ).not.toBeInTheDocument();
  });

  it("archives a closed session only after confirmation", async () => {
    renderPage();

    expect(screen.getByRole("button", { name: "受付を再開" })).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "アーカイブ" }));

    expect(
      screen.getByRole("heading", {
        name: "このテスト実施をアーカイブしますか？",
      }),
    ).toBeVisible();
    expect(
      screen.getByText(/アーカイブ後は答案受付を再開できません/),
    ).toBeVisible();
    expect(
      screen.getByText(/すべての答案が確定または取消済みになり/),
    ).toBeVisible();
    expect(screen.getByText(/重複確認、順番取り込み/)).toBeVisible();

    fireEvent.click(screen.getByRole("button", { name: "アーカイブする" }));

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/test-sessions/session-1:archive",
        {},
        { idempotencyKey: expect.any(String) },
      ),
    );
    expect(queryState.reloadSession).toHaveBeenCalledOnce();
    expect(queryState.reloadSummary).toHaveBeenCalledOnce();
  });

  it("renders archived sessions read-only without an erroneous reopen action", () => {
    queryState.session = makeSession({ state: "archived" });
    renderPage();

    expect(
      screen.getByText("このテスト実施はアーカイブされています"),
    ).toBeVisible();
    expect(screen.getByText("答案受付停止中")).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "受付を再開" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "アーカイブ" }),
    ).not.toBeInTheDocument();
  });

  it("does not offer archive until an open session is closed", () => {
    queryState.session = makeSession({ state: "open" });
    renderPage();

    expect(screen.getByRole("button", { name: "受付を終了" })).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "アーカイブ" }),
    ).not.toBeInTheDocument();
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <Routes>
        <Route
          path="/sessions/:sessionId"
          element={<SessionDetailPage />}
        />
      </Routes>
    </BrowserRouter>,
  );
}

function makeSession(
  changes: Partial<TestSessionSummary> = {},
): TestSessionSummary {
  return {
    id: "session-1",
    sessionName: "4年理科 HOP 8月",
    templateId: "template-1",
    templateVersionId: "version-1",
    templateTitle: "4年理科 HOP",
    templateVersionNumber: 1,
    testDate: "2026-08-10",
    priority: "economy",
    state: "closed",
    expectedSubmissionPageCount: 1,
    revision: 4,
    ...changes,
  };
}

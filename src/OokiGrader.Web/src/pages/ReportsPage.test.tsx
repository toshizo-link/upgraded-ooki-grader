import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter } from "../router";
import type { SubmissionSummary } from "../types";
import { ReportsPage } from "./ReportsPage";

const queryState = vi.hoisted(() => ({
  items: [] as Array<SubmissionSummary & {
    testTitle?: string;
    testDate?: string;
    templateId?: string;
    subject?: string;
    category?: string;
    course?: string;
    classLabel?: string;
  }>,
}));
const apiState = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
}));

vi.mock("../auth/SessionContext", () => ({
  useSession: () => ({ hasAnyRole: () => true }),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: () => ({
    data: {
      items: queryState.items,
      nextCursor: null,
      totalApproximate: queryState.items.length,
      facets: {
        students: [{ value: "student-1", label: "佐藤 花子", count: 2 }],
        templates: [{ value: "template-1", label: "4年理科 HOP", count: 2 }],
        subjects: [{ value: "理科", label: "理科", count: 2 }],
      },
    },
    error: undefined,
    status: "success" as const,
    reload: vi.fn(),
  }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return {
    ...actual,
    api: {
      ...actual.api,
      get: apiState.get,
      post: apiState.post,
    },
  };
});

beforeEach(() => {
  window.history.replaceState(null, "", "/reports?subject=%E7%90%86%E7%A7%91");
  queryState.items = [makeResult("submission-1", "4年理科 HOP")];
  apiState.get.mockReset();
  apiState.post.mockReset();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.useRealTimers();
});

describe("ReportsPage bulk result export", () => {
  it("previews and confirms the exact checked result IDs before creating", async () => {
    apiState.post.mockImplementation((path: string) => {
      if (path.endsWith(":preview")) return Promise.resolve(makePreview());
      return Promise.resolve(makeStatus("verified"));
    });
    renderPage();

    fireEvent.click(
      screen.getByRole("checkbox", {
        name: "佐藤 花子・4年理科 HOPを選択",
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "選択した結果を一括出力" }));

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/transcript-exports:preview",
        { selector: { submissionIds: ["submission-1"] } },
        { idempotency: false },
      ),
    );
    expect(await screen.findByText("対象の確定結果")).toBeVisible();
    expect(screen.getByText(/チェックを付けた結果行だけ/)).toBeVisible();

    fireEvent.click(
      screen.getByRole("checkbox", { name: /上記の1名・1件が出力対象/ }),
    );
    fireEvent.click(
      screen.getByRole("button", { name: "この対象で一括出力を開始" }),
    );

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/transcript-exports",
        {
          sourceFingerprint: "a".repeat(64),
          selector: { submissionIds: ["submission-1"] },
        },
        { idempotencyKey: expect.any(String) },
      ),
    );
    expect(await screen.findByRole("link", { name: "ZIPをダウンロード" })).toHaveAttribute(
      "href",
      "/api/v1/transcript-exports/export-1/file",
    );
    expect(window.location.search).toContain("bulkExport=export-1");
  });

  it("exports all filtered pages and polls progress to completion", async () => {
    vi.useFakeTimers();
    apiState.post.mockImplementation((path: string) => {
      if (path.endsWith(":preview")) {
        return Promise.resolve(
          makePreview({ filter: { subject: "理科", sort: "-testDate" } }),
        );
      }
      return Promise.resolve(makeStatus("queued"));
    });
    apiState.get.mockResolvedValue(makeStatus("verified"));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "絞り込み結果を一括出力" }));
    await act(async () => Promise.resolve());
    expect(apiState.post).toHaveBeenCalledWith(
      "/transcript-exports:preview",
      {
        selector: {
          filter: expect.objectContaining({ subject: "理科", sort: "-testDate" }),
        },
      },
      { idempotency: false },
    );
    fireEvent.click(
      screen.getByRole("checkbox", { name: /上記の1名・1件が出力対象/ }),
    );
    fireEvent.click(
      screen.getByRole("button", { name: "この対象で一括出力を開始" }),
    );
    await act(async () => Promise.resolve());

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_500);
    });
    expect(apiState.get).toHaveBeenCalledWith("/transcript-exports/export-1");
    expect(screen.getByRole("link", { name: "ZIPをダウンロード" })).toBeVisible();
  });

  it("returns to a fresh preview when a completed job reports failure", async () => {
    apiState.post.mockImplementation((path: string) => {
      if (path.endsWith(":preview")) return Promise.resolve(makePreview());
      return Promise.resolve(
        makeStatus("failed", "PDFを作成できませんでした。"),
      );
    });
    renderPage();

    fireEvent.click(
      screen.getByRole("checkbox", {
        name: "佐藤 花子・4年理科 HOPを選択",
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "選択した結果を一括出力" }));
    fireEvent.click(
      await screen.findByRole("checkbox", { name: /上記の1名・1件が出力対象/ }),
    );
    fireEvent.click(
      screen.getByRole("button", { name: "この対象で一括出力を開始" }),
    );

    expect(await screen.findByText("PDFを作成できませんでした。")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "同じ対象をもう一度準備" }));
    await waitFor(() =>
      expect(
        apiState.post.mock.calls.filter(([path]) =>
          String(path).endsWith(":preview"),
        ),
      ).toHaveLength(2),
    );
  });

  it("reuses the acknowledged preview idempotency key after an ambiguous create failure", async () => {
    let createAttempts = 0;
    apiState.post.mockImplementation((path: string) => {
      if (path.endsWith(":preview")) return Promise.resolve(makePreview());
      createAttempts += 1;
      return createAttempts === 1
        ? Promise.reject(new TypeError("Failed to fetch"))
        : Promise.resolve(makeStatus("verified"));
    });
    renderPage();

    fireEvent.click(
      screen.getByRole("checkbox", {
        name: "佐藤 花子・4年理科 HOPを選択",
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "選択した結果を一括出力" }));
    fireEvent.click(
      await screen.findByRole("checkbox", { name: /上記の1名・1件が出力対象/ }),
    );
    fireEvent.click(
      screen.getByRole("button", { name: "この対象で一括出力を開始" }),
    );

    expect(await screen.findByText("開始結果を確認できませんでした")).toBeVisible();
    const firstCreateCall = apiState.post.mock.calls.find(
      ([path]) => path === "/transcript-exports",
    );
    const firstKey = firstCreateCall?.[2]?.idempotencyKey;
    expect(firstKey).toEqual(expect.any(String));

    fireEvent.click(screen.getByRole("button", { name: "再読み込み" }));
    expect(await screen.findByRole("link", { name: "ZIPをダウンロード" })).toBeVisible();

    const createCalls = apiState.post.mock.calls.filter(
      ([path]) => path === "/transcript-exports",
    );
    expect(createCalls).toHaveLength(2);
    expect(createCalls[1]?.[2]?.idempotencyKey).toBe(firstKey);
    expect(createCalls[1]?.[1]).toEqual(createCalls[0]?.[1]);
  });

  it("recovers a durable export from the bounded URL ID after reload", async () => {
    window.history.replaceState(
      null,
      "",
      "/reports?subject=%E7%90%86%E7%A7%91&bulkExport=01K2BULKEXPORT0000000000000",
    );
    apiState.get.mockResolvedValue(makeStatus("verified", undefined, "01K2BULKEXPORT0000000000000"));
    renderPage();

    await waitFor(() =>
      expect(apiState.get).toHaveBeenCalledWith(
        "/transcript-exports/01K2BULKEXPORT0000000000000",
      ),
    );
    expect(await screen.findByText("1件のZIPをダウンロードできます。")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "状況を開く" }));
    expect(screen.getByRole("link", { name: "ZIPをダウンロード" })).toHaveAttribute(
      "href",
      "/api/v1/transcript-exports/01K2BULKEXPORT0000000000000/file",
    );
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <ReportsPage />
    </BrowserRouter>,
  );
}

function makeResult(id: string, title: string) {
  return {
    id,
    state: "finalized",
    studentId: "student-1",
    studentDisplayName: "佐藤 花子",
    studentNumber: "S001",
    testTitle: title,
    testDate: "2026-08-01",
    templateId: "template-1",
    subject: "理科",
    totalEarnedPointsMilli: 80_000,
    totalPossiblePointsMilli: 100_000,
    scanPayloadState: "scan_available" as const,
  };
}

function makePreview(
  selector: unknown = { submissionIds: ["submission-1"] },
) {
  return {
    normalizedSelector: selector,
    studentCount: 1,
    resultCount: 1,
    sourceFingerprint: "a".repeat(64),
  };
}

function makeStatus(state: string, safeErrorDetail?: string, id = "export-1") {
  return {
    id,
    state,
    progressBasisPoints: state === "verified" ? 10_000 : 0,
    processedResultCount: state === "verified" ? 1 : 0,
    studentCount: 1,
    resultCount: 1,
    sourceFingerprint: "a".repeat(64),
    safeErrorDetail,
    normalizedSelector: { submissionIds: ["submission-1"] },
    fileUrl: state === "verified" ? "/api/v1/transcript-exports/export-1/file" : undefined,
  };
}

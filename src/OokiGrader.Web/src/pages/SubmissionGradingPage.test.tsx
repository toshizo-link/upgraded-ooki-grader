import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter, Route, Routes } from "../router";
import { ApiError } from "../lib/api";
import type {
  SubmissionBulkConfirmResponse,
  SubmissionGradingWorkspace,
} from "../types";
import { SubmissionGradingPage } from "./SubmissionGradingPage";

const queryState = vi.hoisted(() => ({
  data: undefined as
    | { workspace: SubmissionGradingWorkspace; etag?: string }
    | undefined,
  reload: vi.fn(),
}));
const apiState = vi.hoisted(() => ({ post: vi.fn() }));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: () => ({
    data: queryState.data,
    error: undefined,
    status: queryState.data ? ("success" as const) : ("loading" as const),
    reload: queryState.reload,
  }),
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
  window.history.replaceState(
    null,
    "",
    "/submissions/submission-1/grading",
  );
  queryState.data = { workspace: makeWorkspace(), etag: '"12"' };
  apiState.post.mockResolvedValue(undefined);
  vi.spyOn(window, "confirm").mockReturnValue(true);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.clearAllMocks();
});

describe("SubmissionGradingPage workflow", () => {
  it("shows a two-page STEP PDF and follows each result's evidence page", () => {
    const { container } = renderPage();

    expect(
      screen.getByRole("heading", { level: 1, name: "理科6年STEPセット1-1" }),
    ).toBeVisible();
    expect(screen.getByText("山田 太郎")).toBeVisible();
    expect(screen.getByTitle("答案PDF（1ページ目）")).toHaveAttribute(
      "src",
      "/api/v1/submissions/submission-1/original-pdf#page=1&zoom=page-width",
    );

    fireEvent.click(screen.getByRole("button", { name: /大問2/ }));
    expect(screen.getByTitle("答案PDF（2ページ目）")).toHaveAttribute(
      "src",
      expect.stringContaining("#page=2"),
    );

    fireEvent.click(screen.getByRole("button", { name: "ページ画像" }));
    expect(screen.getByAltText("答案の2ページ目")).toHaveAttribute(
      "src",
      "/api/v1/review/pages/page-2/content",
    );
    expect(
      container.querySelectorAll(".submission-page-viewer__canvas img"),
    ).toHaveLength(1);
    expect(
      container.querySelectorAll(".submission-page-thumbnails img[loading='lazy']"),
    ).toHaveLength(2);
    expect(
      container.querySelector(
        "img[src='/api/v1/review/pages/page-1/content']",
      ),
    ).not.toBeInTheDocument();
  });

  it("autosaves one result through the revision-safe override contract", async () => {
    renderPage();

    expect(
      screen.queryByRole("button", { name: "この採点を保存・確認" }),
    ).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("読み取り結果"), {
      target: { value: "蒸散" },
    });
    fireEvent.change(screen.getByLabelText("点数"), {
      target: { value: "1" },
    });
    fireEvent.change(screen.getByLabelText(/変更・確認理由/), {
      target: { value: "partial_credit" },
    });

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/submissions/submission-1/results/result-1:override",
        {
          sourceResultRevision: 3,
          awardedPointsMilli: 1000,
          outcome: "partial",
          transcriptionCorrection: "蒸散",
          reasonCode: "partial_credit",
          note: "",
        },
        { idempotencyKey: expect.any(String) },
      ),
      { timeout: 2_000 },
    );
    expect(queryState.reload).toHaveBeenCalledOnce();
  });

  it("always sends the effective transcription when only the score changes", async () => {
    renderPage();

    fireEvent.change(screen.getByLabelText("点数"), {
      target: { value: "1" },
    });

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/submissions/submission-1/results/result-1:override",
        expect.objectContaining({
          awardedPointsMilli: 1000,
          outcome: "partial",
          transcriptionCorrection: "蒸発",
        }),
        { idempotencyKey: expect.any(String) },
      ),
      { timeout: 2_000 },
    );
  });

  it("reuses the idempotency key when retrying the same override after a transport failure", async () => {
    apiState.post
      .mockRejectedValueOnce(new Error("接続が切れました"))
      .mockResolvedValueOnce(undefined);
    renderPage();

    fireEvent.change(screen.getByLabelText("点数"), {
      target: { value: "1" },
    });
    expect(
      await screen.findByText("接続が切れました", {}, { timeout: 2_000 }),
    ).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "保存を再試行" }));

    await waitFor(() => expect(apiState.post).toHaveBeenCalledTimes(2));
    const firstOptions = apiState.post.mock.calls[0]?.[2] as
      | { idempotencyKey?: string }
      | undefined;
    const secondOptions = apiState.post.mock.calls[1]?.[2] as
      | { idempotencyKey?: string }
      | undefined;
    expect(firstOptions?.idempotencyKey).toBeTruthy();
    expect(secondOptions?.idempotencyKey).toBe(firstOptions?.idempotencyKey);
  });

  it("serializes autosaves and sends the latest edit with the next revision", async () => {
    let resolveFirst: (() => void) | undefined;
    let resolveSecond: (() => void) | undefined;
    apiState.post
      .mockImplementationOnce(
        () =>
          new Promise<void>((resolve) => {
            resolveFirst = resolve;
          }),
      )
      .mockImplementationOnce(
        () =>
          new Promise<void>((resolve) => {
            resolveSecond = resolve;
          }),
      );
    renderPage();

    fireEvent.change(screen.getByLabelText("点数"), {
      target: { value: "1" },
    });
    await waitFor(() => expect(apiState.post).toHaveBeenCalledOnce(), {
      timeout: 2_000,
    });

    fireEvent.change(screen.getByLabelText("点数"), {
      target: { value: "2" },
    });
    resolveFirst?.();

    await waitFor(() => expect(apiState.post).toHaveBeenCalledTimes(2), {
      timeout: 2_000,
    });
    expect(queryState.reload).not.toHaveBeenCalled();
    expect(apiState.post.mock.calls[1]?.[1]).toEqual(
      expect.objectContaining({
        sourceResultRevision: 4,
        awardedPointsMilli: 2_000,
        outcome: "correct",
      }),
    );

    resolveSecond?.();
    await waitFor(() => expect(queryState.reload).toHaveBeenCalledOnce());
  });

  it("freezes the unresolved snapshot and requires acknowledgment before bulk confirmation", async () => {
    apiState.post.mockResolvedValue(makeBulkResponse());
    renderPage();

    fireEvent.click(
      screen.getByRole("button", { name: "未確認2問を一括確認" }),
    );
    const confirmButton = screen.getByRole("button", {
      name: "2問を確認済みにする",
    });
    expect(confirmButton).toBeDisabled();

    fireEvent.click(
      screen.getByRole("checkbox", {
        name: /この答案の未確認2問を確認しました/,
      }),
    );
    expect(confirmButton).toBeEnabled();
    fireEvent.click(confirmButton);

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/submissions/submission-1/results:confirm-unresolved",
        {
          sourceSubmissionRevision: 12,
          gradingRunId: "run-1",
          sourceResultSourceRevision: 7,
          items: [
            { resultId: "result-1", sourceResultRevision: 3 },
            { resultId: "result-2", sourceResultRevision: 4 },
          ],
        },
        { idempotencyKey: expect.any(String), etag: '"12"' },
      ),
    );
    expect(
      screen.getByText("確認済み 1件・対象外 1件・更新あり 0件"),
    ).toBeVisible();
    expect(queryState.reload).toHaveBeenCalledOnce();
  });

  it("reports every frozen item as stale on a 412 and offers a reload", async () => {
    apiState.post.mockRejectedValue(
      new ApiError(412, {
        status: 412,
        code: "GRADING_WORKSPACE_STALE",
        title: "採点結果が更新されています",
        detail: "最新の状態を読み込んでください。",
      }),
    );
    renderPage();

    fireEvent.click(
      screen.getByRole("button", { name: "未確認2問を一括確認" }),
    );
    fireEvent.click(screen.getByRole("checkbox", { name: /未確認2問/ }));
    fireEvent.click(
      screen.getByRole("button", { name: "2問を確認済みにする" }),
    );

    expect(
      await screen.findByText("確認済み 0件・対象外 0件・更新あり 2件"),
    ).toBeVisible();
    expect(
      screen.getByText(/確認を開いた後に採点結果が更新されました/),
    ).toBeVisible();
    fireEvent.click(
      screen.getByRole("button", { name: "最新の状態を読み込む" }),
    );
    expect(queryState.reload).toHaveBeenCalledOnce();
  });

  it("falls back safely when retention removed the PDF and normalized pages", () => {
    queryState.data = {
      workspace: makeWorkspace({
        originalPdf: null,
        submission: {
          ...makeWorkspace().submission,
          scanPayloadState: "scan_deleted",
          scanDeletedAt: "2026-08-11T03:00:00Z",
        },
        pages: makeWorkspace().pages.map((page) => ({
          ...page,
          available: false,
          contentUrl: null,
          thumbnailUrl: null,
        })),
      }),
    };
    renderPage();

    expect(
      screen.getByText("答案画像の保存期間が終了しています"),
    ).toBeVisible();
    expect(screen.queryByTitle(/答案PDF/)).not.toBeInTheDocument();
    expect(
      screen.getByText("このページの画像は表示できません"),
    ).toBeVisible();
    expect(screen.getByText("蒸発")).toBeVisible();
  });

  it.each([
    ["finalized submission", "finalized", "closed"],
    ["archived session", "needs_grade_review", "archived"],
  ])(
    "keeps a %s readable but disables grading mutations",
    (_, submissionState, sessionState) => {
    const base = makeWorkspace();
    queryState.data = {
      workspace: makeWorkspace({
        submission: {
          ...base.submission,
          state: submissionState,
        },
        session: {
          ...base.session,
          state: sessionState,
        },
        canBulkConfirm: false,
        canFinalize: false,
      }),
    };
    renderPage();

    expect(screen.getByText("この答案は読み取り専用です")).toBeVisible();
    expect(screen.getByTitle("答案PDF（1ページ目）")).toBeVisible();
    expect(screen.getByLabelText("読み取り結果")).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: "この採点を保存・確認" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "未確認2問を一括確認" }),
    ).toBeDisabled();
    },
  );

  it("keeps the current result selected until its edit is saved", async () => {
    renderPage();

    fireEvent.change(screen.getByLabelText("読み取り結果"), {
      target: { value: "自動保存する訂正" },
    });
    fireEvent.click(screen.getByRole("button", { name: /大問2/ }));

    expect(window.confirm).not.toHaveBeenCalled();
    expect(screen.getByLabelText("読み取り結果")).toHaveValue(
      "自動保存する訂正",
    );
    expect(screen.getByRole("heading", { name: "植物の働きを答えなさい" })).toBeVisible();

    await waitFor(() => expect(apiState.post).toHaveBeenCalledOnce(), {
      timeout: 2_000,
    });
    fireEvent.click(screen.getByRole("button", { name: /大問2/ }));
    expect(
      screen.getByRole("heading", { name: "気体を二つ答えなさい" }),
    ).toBeVisible();
  });

  it("lets a teacher review only questions marked incorrect", () => {
    renderPage();

    fireEvent.click(
      screen.getByRole("button", { name: "不正解のみ 1" }),
    );

    expect(
      screen.getByRole("button", { name: "不正解のみ 1" }),
    ).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: /大問1/ })).toBeVisible();
    expect(screen.queryByRole("button", { name: /大問2/ })).not.toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "植物の働きを答えなさい" }),
    ).toBeVisible();

    fireEvent.click(screen.getByRole("button", { name: "すべて 2" }));
    expect(screen.getByRole("button", { name: /大問2/ })).toBeVisible();
  });

  it("keeps the current filter until its edit is saved", async () => {
    renderPage();

    fireEvent.change(screen.getByLabelText("読み取り結果"), {
      target: { value: "未保存の訂正" },
    });
    fireEvent.click(
      screen.getByRole("button", { name: "不正解のみ 1" }),
    );

    expect(window.confirm).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "すべて 2" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(screen.getByLabelText("読み取り結果")).toHaveValue("未保存の訂正");

    await waitFor(() => expect(apiState.post).toHaveBeenCalledOnce(), {
      timeout: 2_000,
    });
    fireEvent.click(
      screen.getByRole("button", { name: "不正解のみ 1" }),
    );
    expect(
      screen.getByRole("button", { name: "不正解のみ 1" }),
    ).toHaveAttribute("aria-pressed", "true");
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <Routes>
        <Route
          path="/submissions/:submissionId/grading"
          element={<SubmissionGradingPage />}
        />
        <Route path="/sessions/:sessionId" element={<div>session page</div>} />
      </Routes>
    </BrowserRouter>,
  );
}

function makeWorkspace(
  changes: Partial<SubmissionGradingWorkspace> = {},
): SubmissionGradingWorkspace {
  return {
    submission: {
      id: "submission-1",
      state: "needs_grade_review",
      revision: 12,
      fileName: "step-yamada.pdf",
      uploadedAt: "2026-08-10T02:00:00Z",
      pageCount: 2,
      scanPayloadState: "scan_available",
    },
    session: {
      id: "session-1",
      state: "closed",
      testDate: "2026-08-10",
      classLabel: "6年A組",
    },
    test: {
      templateVersionId: "version-1",
      templateVersionNumber: 2,
      title: "理科6年STEPセット1-1",
      subject: "理科",
      gradeLabel: "6年",
      category: "STEP",
      course: "標準",
    },
    student: {
      id: "student-1",
      displayName: "山田 太郎",
      studentNumber: "S001",
      schoolClass: "6年A組",
      gradeLabel: "6年",
    },
    gradingRun: {
      id: "run-1",
      state: "completed",
      resultSourceRevision: 7,
      earnedPointsMilli: 3000,
      possiblePointsMilli: 4000,
    },
    originalPdf: {
      available: true,
      url: "/api/v1/submissions/submission-1/original-pdf",
      contentType: "application/pdf",
    },
    pages: [
      {
        id: "page-1",
        pageNumber: 1,
        widthPixels: 1600,
        heightPixels: 2200,
        rotationDegrees: 0,
        qualityState: "usable",
        available: true,
        contentUrl: "/api/v1/review/pages/page-1/content",
        thumbnailUrl:
          "/api/v1/submissions/submission-1/pages/page-1/thumbnail",
      },
      {
        id: "page-2",
        pageNumber: 2,
        widthPixels: 1600,
        heightPixels: 2200,
        rotationDegrees: 0,
        qualityState: "usable",
        available: true,
        contentUrl: "/api/v1/review/pages/page-2/content",
        thumbnailUrl:
          "/api/v1/submissions/submission-1/pages/page-2/thumbnail",
      },
    ],
    results: [
      {
        resultId: "result-1",
        questionId: "question-1",
        orderIndex: 1,
        displayLabel: "大問1",
        questionText: "植物の働きを答えなさい",
        questionType: "shortAnswer",
        gradingMode: "ai",
        pageNumbers: [1],
        expectedAnswers: ["蒸散"],
        transcription: "蒸発",
        outcome: "incorrect",
        awardedPointsMilli: 0,
        maxPointsMilli: 2000,
        pointIncrementMilli: 1000,
        reason: "意味が異なる可能性があります",
        explanation: "用語を確認してください",
        confidenceBasisPoints: 6100,
        kanjiRequired: true,
        requiresCompleteAnswer: false,
        answerOrderInsensitive: false,
        reviewRequired: true,
        reviewStatus: "pending",
        sourceResultRevision: 3,
      },
      {
        resultId: "result-2",
        questionId: "question-2",
        orderIndex: 2,
        displayLabel: "大問2",
        questionText: "気体を二つ答えなさい",
        questionType: "shortAnswer",
        gradingMode: "ai",
        pageNumbers: [2],
        expectedAnswers: ["酸素・二酸化炭素"],
        transcription: "酸素・二酸化炭素",
        outcome: "correct",
        awardedPointsMilli: 3000,
        maxPointsMilli: 3000,
        pointIncrementMilli: 1000,
        reason: "正答と一致",
        explanation: "順不同で一致しています",
        confidenceBasisPoints: 9400,
        kanjiRequired: false,
        requiresCompleteAnswer: true,
        answerOrderInsensitive: true,
        reviewRequired: true,
        reviewStatus: "pending",
        sourceResultRevision: 4,
      },
    ],
    unresolvedSnapshot: [
      { resultId: "result-1", sourceResultRevision: 3 },
      { resultId: "result-2", sourceResultRevision: 4 },
    ],
    bulkConfirmationLimit: 300,
    canBulkConfirm: true,
    canFinalize: false,
    ...changes,
  };
}

function makeBulkResponse(): SubmissionBulkConfirmResponse {
  return {
    confirmed: [
      {
        resultId: "result-1",
        code: "RESULT_CONFIRMED",
        sourceResultRevision: 4,
      },
    ],
    skipped: [
      {
        resultId: "result-2",
        code: "RESULT_ALREADY_CONFIRMED",
        sourceResultRevision: 4,
      },
    ],
    gradingRun: {
      id: "run-1",
      state: "completed",
      resultSourceRevision: 8,
      earnedPointsMilli: 3000,
      possiblePointsMilli: 4000,
    },
    submission: { state: "ready_to_finalize", revision: 13 },
    canFinalize: true,
  };
}

import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter } from "../router";
import type {
  TemplateGenerationBatchSummary,
  TemplateSummary,
} from "../types";
import { TemplatesPage } from "./TemplatesPage";

const capabilityState = vi.hoisted(() => ({ enabled: true }));
const queryState = vi.hoisted(() => ({
  items: [] as TemplateSummary[],
  reload: vi.fn(),
}));
const generationQueryState = vi.hoisted(() => ({
  items: [] as TemplateGenerationBatchSummary[],
  reload: vi.fn(),
  browserRecoveryOnly: false,
  status: "success" as "loading" | "success" | "empty" | "error",
}));
const apiState = vi.hoisted(() => ({
  delete: vi.fn(),
  get: vi.fn(),
  post: vi.fn(),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: (key: string) =>
    key === "template-generation-resumable"
      ? {
          data:
            generationQueryState.status === "loading"
              ? undefined
              : {
                  items: generationQueryState.items,
                  limit: 20,
                  browserRecoveryOnly:
                    generationQueryState.browserRecoveryOnly,
                },
          error:
            generationQueryState.status === "error"
              ? new Error("temporary failure")
              : undefined,
          status: generationQueryState.status,
          reload: generationQueryState.reload,
        }
      : {
          data: {
            items: queryState.items,
            page: 1,
            pageSize: 100,
            totalCount: queryState.items.length,
          },
          error: undefined,
          status: "success" as const,
          reload: queryState.reload,
        },
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>(
    "../lib/api",
  );
  return {
    ...actual,
    api: {
      ...actual.api,
      delete: apiState.delete,
      get: apiState.get,
      post: apiState.post,
    },
  };
});

vi.mock("../hooks/useRuntimeCapabilities", () => ({
  useRuntimeCapabilities: () => ({
    data: {
      ai: { templateGeneration: { enabled: capabilityState.enabled } },
    },
    error: undefined,
    status: "success" as const,
    reload: vi.fn(),
  }),
}));

beforeEach(() => {
  window.history.replaceState(null, "", "/templates");
  capabilityState.enabled = true;
  queryState.items = [];
  generationQueryState.items = [];
  generationQueryState.browserRecoveryOnly = false;
  generationQueryState.status = "success";
  apiState.delete.mockResolvedValue(undefined);
  apiState.get.mockResolvedValue(undefined);
  apiState.post.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("TemplatesPage template-generation capability", () => {
  it("links to the create route when generation is enabled", () => {
    renderPage();

    expect(screen.getByRole("link", { name: "ひな形を作成" })).toHaveAttribute(
      "href",
      "/templates/new",
    );
  });

  it("shows a non-actionable CTA when generation is disabled", () => {
    capabilityState.enabled = false;
    renderPage();

    expect(screen.getByText("ひな形作成は停止中")).toBeVisible();
    expect(screen.queryByRole("link", { name: /ひな形.*作成/ })).not.toBeInTheDocument();
  });

  it("reopens multiple saved generation batches with status-specific actions", () => {
    generationQueryState.items = [
      makeGenerationBatch({
        id: "step-running",
        status: "generating",
        completedUnitCount: 21,
        expectedUnitCount: 36,
      }),
      makeGenerationBatch({
        id: "step-final-check",
        status: "needsFinalCheck",
        subject: "算数",
        completedUnitCount: 36,
        expectedUnitCount: 36,
      }),
      makeGenerationBatch({
        id: "hop-failed",
        status: "failed",
        testType: "hop",
        failedUnitCount: 2,
      }),
    ];
    renderPage();

    expect(
      screen.getByRole("heading", { name: "作成中・確認待ちのひな形" }),
    ).toBeVisible();
    expect(screen.getByText(/21 \/ 36件 完了/)).toBeVisible();
    expect(screen.getByText(/2件失敗・成功済みは保持されています/)).toBeVisible();
    expect(screen.getByRole("link", { name: "最終確認へ" })).toHaveAttribute(
      "href",
      "/templates/generation/step-final-check/final-check",
    );
    expect(
      screen.getByRole("link", { name: "確認・再試行へ" }),
    ).toHaveAttribute("href", "/templates/generation/hop-failed");
    expect(
      screen.getByRole("progressbar", { name: "STEP 理科の生成進捗" }),
    ).toHaveAttribute("aria-valuenow", "58");
  });

  it("explains when the durable server list falls back to this browser", () => {
    generationQueryState.items = [makeGenerationBatch()];
    generationQueryState.browserRecoveryOnly = true;
    renderPage();

    expect(
      screen.getByText("現在は、このブラウザで開始・表示した作業を復元しています。"),
    ).toBeVisible();
    expect(screen.getByRole("link", { name: "生成状況を見る" })).toHaveAttribute(
      "href",
      "/templates/generation/batch-running",
    );
  });
});

describe("TemplatesPage lifecycle actions", () => {
  it("archives a template only after explaining what remains", async () => {
    queryState.items = [makeTemplate()];
    renderPage();

    expect(
      screen.getByRole("link", { name: "「4年理科 HOP」を開く" }),
    ).toHaveAttribute(
      "href",
      "/templates/template-1/versions/version-1",
    );
    fireEvent.click(screen.getByRole("button", { name: "アーカイブ" }));

    expect(
      screen.getByRole("heading", {
        name: "「4年理科 HOP」をアーカイブしますか？",
      }),
    ).toBeVisible();
    expect(
      screen.getByText(/過去のテスト実施、答案、採点結果は削除されません/),
    ).toBeVisible();

    fireEvent.click(screen.getByRole("button", { name: "アーカイブする" }));

    await waitFor(() =>
      expect(apiState.delete).toHaveBeenCalledWith("/templates/template-1", {
        etag: '"rev-7"',
        idempotencyKey: expect.any(String),
      }),
    );
    expect(queryState.reload).toHaveBeenCalledOnce();
    expect(
      screen.getByText("「4年理科 HOP」をアーカイブしました。"),
    ).toBeVisible();
  });

  it("restores an archived template after confirmation", async () => {
    queryState.items = [
      makeTemplate({ lifecycleState: "archived", revision: 11 }),
    ];
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "復元" }));
    expect(
      screen.getByRole("heading", { name: "「4年理科 HOP」を復元しますか？" }),
    ).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "復元する" }));

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/templates/template-1:restore",
        { revision: 11 },
        {
          etag: '"rev-11"',
          idempotencyKey: expect.any(String),
        },
      ),
    );
    expect(queryState.reload).toHaveBeenCalledOnce();
    expect(screen.getByText("「4年理科 HOP」を復元しました。")).toBeVisible();
  });

  it("keeps the confirmation open when archiving fails", async () => {
    queryState.items = [makeTemplate()];
    apiState.delete.mockRejectedValue(new Error("最新版を読み込み直してください。"));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "アーカイブ" }));
    fireEvent.click(screen.getByRole("button", { name: "アーカイブする" }));

    expect(
      await screen.findByText("最新版を読み込み直してください。"),
    ).toBeVisible();
    expect(
      screen.getByRole("heading", {
        name: "「4年理科 HOP」をアーカイブしますか？",
      }),
    ).toBeVisible();
    expect(queryState.reload).not.toHaveBeenCalled();
  });
});

describe("TemplatesPage list controls", () => {
  it("restores the full template filter set from the URL", () => {
    window.history.replaceState(
      null,
      "",
      "/templates?state=archived&subject=%E7%90%86%E7%A7%91&category=HOP&course=%E6%9C%AC%E7%A7%91&grade=%E5%B0%8F4&testType=step&sort=subject",
    );
    queryState.items = [
      makeTemplate({
        lifecycleState: "archived",
        subject: "理科",
        category: "HOP",
        course: "本科",
        gradeLabel: "小4",
        testType: "step",
      }),
    ];
    renderPage();

    expect(screen.getByLabelText("ひな形の状態")).toHaveValue("archived");
    expect(screen.getByLabelText("教科")).toHaveValue("理科");
    expect(screen.getByLabelText("カテゴリ")).toHaveValue("HOP");
    expect(screen.getByLabelText("コース")).toHaveValue("本科");
    expect(screen.getByLabelText("学年")).toHaveValue("小4");
    expect(screen.getByLabelText("テスト種別")).toHaveValue("step");
    expect(screen.getByLabelText("並び順")).toHaveValue("subject");
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <TemplatesPage />
    </BrowserRouter>,
  );
}

function makeTemplate(
  changes: Partial<TemplateSummary> = {},
): TemplateSummary {
  return {
    id: "template-1",
    title: "4年理科 HOP",
    subject: "理科",
    lifecycleState: "active",
    activeVersionId: "version-1",
    activeVersionNumber: 1,
    questionCount: 10,
    totalPointsMilli: 100_000,
    revision: 7,
    ...changes,
  };
}

function makeGenerationBatch(
  changes: Partial<TemplateGenerationBatchSummary> = {},
): TemplateGenerationBatchSummary {
  return {
    id: "batch-running",
    status: "generating",
    testType: "step",
    subject: "理科",
    answerStyle: null,
    sourcePageCount: 72,
    expectedUnitCount: 36,
    completedUnitCount: 12,
    failedUnitCount: 0,
    updatedAt: "2026-08-10T20:55:00+09:00",
    rowVersion: 8,
    ...changes,
  };
}

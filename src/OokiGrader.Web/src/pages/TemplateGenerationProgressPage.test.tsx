import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter, Route, Routes } from "../router";
import type { TemplateGenerationBatch } from "../types";
import {
  generationProgressCopy,
  TemplateGenerationProgressPage,
} from "./TemplateGenerationProgressPage";
import { recentTemplateGenerationBatchIds } from "../lib/templateGeneration";

const queryState = vi.hoisted(() => ({
  data: undefined as TemplateGenerationBatch | undefined,
  reload: vi.fn(),
  status: "success" as "loading" | "success" | "error",
  error: undefined as Error | undefined,
}));
const apiState = vi.hoisted(() => ({
  startGeneration: vi.fn(),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: () => ({
    data: queryState.data,
    error: queryState.error,
    status: queryState.status,
    reload: queryState.reload,
  }),
}));

vi.mock("../lib/templateGeneration", async () => {
  const actual = await vi.importActual<typeof import("../lib/templateGeneration")>(
    "../lib/templateGeneration",
  );
  return {
    ...actual,
    templateGenerationApi: {
      ...actual.templateGenerationApi,
      startGeneration: apiState.startGeneration,
    },
  };
});

beforeEach(() => {
  window.history.replaceState(null, "", "/templates/generation/batch-1");
  queryState.data = makeBatch();
  queryState.status = "success";
  queryState.error = undefined;
  window.localStorage.clear();
  apiState.startGeneration.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.clearAllMocks();
});

describe("TemplateGenerationProgressPage", () => {
  it("shows deterministic unit progress without an AI classification phase", () => {
    renderPage();

    expect(
      screen.getByText("テンプレート 1 / 3 を生成しています"),
    ).toBeVisible();
    expect(screen.getByText("1〜2ページ")).toBeVisible();
    expect(screen.getByText("5〜6ページ")).toBeVisible();
    expect(screen.queryByText(/分類|カテゴリ判定|タイプ判定/)).not.toBeInTheDocument();
  });

  it("uses the orientation correction and corrected-retry progress messages", () => {
    const rotating = makeBatch();
    rotating.units[1]!.status = "rotating";
    expect(generationProgressCopy(rotating).title).toBe(
      "ページの向きを補正しています",
    );

    rotating.units[1]!.status = "retryingAfterRotation";
    expect(generationProgressCopy(rotating).title).toBe(
      "補正後のテンプレートを生成しています",
    );
  });

  it("offers final check only after every unit is extracted", () => {
    queryState.data = {
      ...makeBatch(),
      status: "needsFinalCheck",
      completedUnitCount: 3,
      units: makeBatch().units.map((unit) => ({ ...unit, status: "extracted" })),
    };
    renderPage();

    expect(screen.getByText("最終確認を準備しました")).toBeVisible();
    expect(screen.getByRole("button", { name: "最終確認へ" })).toBeEnabled();
  });

  it("keeps polling after a transient status request failure", () => {
    vi.useFakeTimers();
    queryState.status = "error";
    queryState.error = new Error("temporary failure");
    renderPage();

    expect(
      screen.getByText("最新の生成状況を取得できませんでした"),
    ).toBeVisible();
    vi.advanceTimersByTime(5000);
    expect(queryState.reload).toHaveBeenCalledTimes(1);
  });

  it("remembers a directly opened generation URL so the list can recover it", () => {
    renderPage();

    expect(recentTemplateGenerationBatchIds()).toContain("batch-1");
  });

  it("can start a saved draft without uploading the PDF again", async () => {
    queryState.data = { ...makeBatch(), status: "draft", rowVersion: 9 };
    renderPage();

    expect(screen.getByText("作成予定が保存されています")).toBeVisible();
    fireEvent.click(
      screen.getByRole("button", { name: "テンプレート生成を開始" }),
    );

    await waitFor(() =>
      expect(apiState.startGeneration).toHaveBeenCalledWith("batch-1", 9),
    );
    expect(queryState.reload).toHaveBeenCalledOnce();
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <Routes>
        <Route
          path="/templates/generation/:batchId"
          element={<TemplateGenerationProgressPage />}
        />
      </Routes>
    </BrowserRouter>,
  );
}

function makeBatch(): TemplateGenerationBatch {
  return {
    batchId: "batch-1",
    status: "generating",
    testType: "step",
    subject: "算数",
    answerStyle: null,
    promptSystem: "standard",
    sourcePageCount: 6,
    expectedUnitCount: 3,
    completedUnitCount: 1,
    units: [1, 2, 3].map((variation) => ({
      id: `unit-${variation}`,
      sequence: variation,
      status: variation === 1 ? "extracted" : "generating",
      firstPage: variation * 2 - 1,
      lastPage: variation * 2,
      stepSetIndex: 1,
      stepVariationIndex: variation,
      deterministicSuffix: `-${variation}`,
      rowVersion: 2,
    })),
    rowVersion: 3,
  };
}

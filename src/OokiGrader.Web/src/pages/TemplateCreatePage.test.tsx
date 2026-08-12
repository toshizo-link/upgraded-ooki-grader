import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../lib/api";
import { BrowserRouter } from "../router";
import type { TemplateGenerationBatch, UploadFinalizeResponse } from "../types";
import { TemplateCreatePage } from "./TemplateCreatePage";

const mocks = vi.hoisted(() => ({
  uploadFile: vi.fn<() => Promise<UploadFinalizeResponse>>(),
  createBatch: vi.fn<() => Promise<TemplateGenerationBatch>>(),
  startGeneration: vi.fn<() => Promise<void>>(),
  cancelBatch: vi.fn<() => Promise<void>>(),
}));

vi.mock("../lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/api")>();
  return { ...actual, uploadFile: mocks.uploadFile };
});

vi.mock("../lib/templateGeneration", async (importOriginal) => {
  const actual = await importOriginal<
    typeof import("../lib/templateGeneration")
  >();
  return {
    ...actual,
    templateGenerationApi: {
      ...actual.templateGenerationApi,
      createBatch: mocks.createBatch,
      startGeneration: mocks.startGeneration,
      cancelBatch: mocks.cancelBatch,
    },
  };
});

beforeEach(() => {
  window.history.replaceState(null, "", "/templates/new");
  mocks.uploadFile.mockResolvedValue({
    uploadId: "upload-1",
    state: "completed",
    rowVersion: 4,
  });
  mocks.createBatch.mockResolvedValue(makeBatch("hop", 3));
  mocks.startGeneration.mockResolvedValue(undefined);
  mocks.cancelBatch.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("TemplateCreatePage", () => {
  it("shows test settings before any upload surface and removes legacy routing controls", () => {
    const { container } = renderPage();

    expect(screen.getByLabelText("試験タイプ")).toBeVisible();
    expect(screen.getByLabelText("教科")).toBeVisible();
    expect(container.querySelector('input[type="file"]')).not.toBeInTheDocument();
    expect(screen.queryByLabelText("学年")).not.toBeInTheDocument();
    expect(screen.queryByText("資料の種類")).not.toBeInTheDocument();
    expect(screen.queryByText("自動判定")).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("試験タイプ"), {
      target: { value: "hop" },
    });
    expect(container.querySelector('input[type="file"]')).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("教科"), {
      target: { value: "算数" },
    });
    expect(container.querySelector('input[type="file"]')).toBeInTheDocument();
  });

  it("requires answer style only for Other before showing upload", () => {
    const { container } = renderPage();
    fireEvent.change(screen.getByLabelText("試験タイプ"), {
      target: { value: "other" },
    });
    fireEvent.change(screen.getByLabelText("教科"), {
      target: { value: "国語" },
    });

    expect(screen.getByLabelText("問題形式")).toBeVisible();
    expect(container.querySelector('input[type="file"]')).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("問題形式"), {
      target: { value: "fillBlank" },
    });
    expect(container.querySelector('input[type="file"]')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("試験タイプ"), {
      target: { value: "step" },
    });
    expect(screen.queryByLabelText("問題形式")).not.toBeInTheDocument();
  });

  it("carries Other fill-blank as the only configurable answer style", async () => {
    mocks.createBatch.mockResolvedValue({
      ...makeBatch("hop", 1),
      testType: "other",
      subject: "国語",
      answerStyle: "fillBlank",
      promptSystem: "fillBlank",
    });
    const { container } = renderPage();
    fireEvent.change(screen.getByLabelText("試験タイプ"), {
      target: { value: "other" },
    });
    fireEvent.change(screen.getByLabelText("教科"), {
      target: { value: "国語" },
    });
    fireEvent.change(screen.getByLabelText("問題形式"), {
      target: { value: "fillBlank" },
    });
    fireEvent.change(container.querySelector('input[type="file"]') as HTMLInputElement, {
      target: {
        files: [new File(["pdf"], "穴埋め.pdf", { type: "application/pdf" })],
      },
    });

    await screen.findByText("PDF全体から1件のテンプレートを生成します。");
    expect(mocks.createBatch).toHaveBeenCalledWith(
      expect.objectContaining({
        testType: "other",
        subject: "国語",
        answerStyle: "fillBlank",
      }),
    );
  });

  it("uploads one PDF after settings and carries trusted settings into HOP planning", async () => {
    const { container } = renderPage();
    selectSettings("hop", "算数");
    const fileInput = container.querySelector(
      'input[type="file"]',
    ) as HTMLInputElement;

    fireEvent.change(fileInput, {
      target: {
        files: [new File(["pdf"], "HOP算数_小4.pdf", { type: "application/pdf" })],
      },
    });

    expect(
      await screen.findByText("1ページごとに分割し、3件のテンプレートを生成します。"),
    ).toBeVisible();
    expect(mocks.createBatch).toHaveBeenCalledWith({
      sourceId: "upload-1",
      testType: "hop",
      subject: "算数",
      answerStyle: null,
      expectedSourceRowVersion: 4,
    });
    expect(screen.getAllByText("3ページ").length).toBeGreaterThan(0);
    expect(screen.getByText("3件")).toBeVisible();
  });

  it("shows the deterministic STEP ranges and resets fixed suffixes", async () => {
    mocks.createBatch.mockResolvedValue(makeBatch("step", 12));
    const { container } = renderPage();
    selectSettings("step", "理科");

    fireEvent.change(container.querySelector('input[type="file"]') as HTMLInputElement, {
      target: {
        files: [new File(["pdf"], "STEP理科.pdf", { type: "application/pdf" })],
      },
    });

    expect(
      await screen.findByText(
        "2ページごとに分割し、3件を1セットとして -1 / -2 / -3 を付けます。6件のテンプレートを生成します。",
      ),
    ).toBeVisible();
    expect(screen.getByText("1〜2ページ")).toBeVisible();
    expect(screen.getByText("11〜12ページ")).toBeVisible();
    expect(screen.getAllByText("-1")).toHaveLength(2);
    expect(screen.getAllByText("-2")).toHaveLength(2);
    expect(screen.getAllByText("-3")).toHaveLength(2);
  });

  it("blocks an invalid STEP page count with the stable validation message", async () => {
    mocks.createBatch.mockRejectedValue(
      new ApiError(400, {
        code: "STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX",
        title: "Validation failed",
      }),
    );
    const { container } = renderPage();
    selectSettings("step", "社会");

    fireEvent.change(container.querySelector('input[type="file"]') as HTMLInputElement, {
      target: {
        files: [new File(["pdf"], "STEP_8pages.pdf", { type: "application/pdf" })],
      },
    });

    expect(
      await screen.findByText("STEPのPDFは、ページ数が6の倍数である必要があります。"),
    ).toBeVisible();
    expect(mocks.startGeneration).not.toHaveBeenCalled();
  });

  it("cancels and invalidates the plan when a trusted setting changes", async () => {
    const { container } = renderPage();
    selectSettings("hop", "算数");
    fireEvent.change(container.querySelector('input[type="file"]') as HTMLInputElement, {
      target: {
        files: [new File(["pdf"], "HOP.pdf", { type: "application/pdf" })],
      },
    });
    await screen.findByText("1ページごとに分割し、3件のテンプレートを生成します。");

    fireEvent.change(screen.getByLabelText("教科"), {
      target: { value: "国語" },
    });

    expect(screen.queryByText(/1ページごとに分割し/)).not.toBeInTheDocument();
    await waitFor(() =>
      expect(mocks.cancelBatch).toHaveBeenCalledWith(
        "batch-1",
        1,
        expect.any(String),
      ),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "この設定で再計画" }),
      ).toBeEnabled(),
    );
    expect(screen.queryByRole("button", { name: "テンプレート生成を開始" })).not.toBeInTheDocument();
  });

  it("starts the batch and navigates to its progress page", async () => {
    const { container } = renderPage();
    selectSettings("hop", "算数");
    fireEvent.change(container.querySelector('input[type="file"]') as HTMLInputElement, {
      target: {
        files: [new File(["pdf"], "HOP.pdf", { type: "application/pdf" })],
      },
    });
    const start = await screen.findByRole("button", {
      name: "テンプレート生成を開始",
    });
    fireEvent.click(start);

    await waitFor(() =>
      expect(window.location.pathname).toBe("/templates/generation/batch-1"),
    );
    expect(mocks.startGeneration).toHaveBeenCalledWith("batch-1", 1);
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <TemplateCreatePage />
    </BrowserRouter>,
  );
}

function selectSettings(testType: "hop" | "step", subject: "算数" | "理科" | "社会") {
  fireEvent.change(screen.getByLabelText("試験タイプ"), {
    target: { value: testType },
  });
  fireEvent.change(screen.getByLabelText("教科"), {
    target: { value: subject },
  });
}

function makeBatch(
  testType: "hop" | "step",
  pageCount: number,
): TemplateGenerationBatch {
  const units =
    testType === "hop"
      ? Array.from({ length: pageCount }, (_, index) => ({
          id: `unit-${index + 1}`,
          sequence: index + 1,
          status: "pending" as const,
          firstPage: index + 1,
          lastPage: index + 1,
          rowVersion: 1,
        }))
      : Array.from({ length: pageCount / 2 }, (_, index) => ({
          id: `unit-${index + 1}`,
          sequence: index + 1,
          status: "pending" as const,
          firstPage: index * 2 + 1,
          lastPage: index * 2 + 2,
          stepSetIndex: Math.floor(index / 3) + 1,
          stepVariationIndex: (index % 3) + 1,
          deterministicSuffix: `-${(index % 3) + 1}`,
          rowVersion: 1,
        }));
  return {
    batchId: "batch-1",
    status: "draft",
    testType,
    subject: testType === "step" ? "理科" : "算数",
    answerStyle: null,
    promptSystem: "standard",
    sourcePageCount: pageCount,
    expectedUnitCount: units.length,
    units,
    rowVersion: 1,
  };
}

import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../lib/api";
import { BrowserRouter, Route, Routes } from "../router";
import type { TemplateGenerationBatch } from "../types";
import { TemplateGenerationFinalCheckPage } from "./TemplateGenerationFinalCheckPage";

const state = vi.hoisted(() => ({
  data: undefined as TemplateGenerationBatch | undefined,
  reload: vi.fn(),
  updateUnit: vi.fn<() => Promise<void>>(),
  updateStepSet: vi.fn<() => Promise<void>>(),
  confirmBatch: vi.fn<() => Promise<TemplateGenerationBatch>>(),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: () => ({
    data: state.data,
    error: undefined,
    status: state.data ? "success" : "loading",
    reload: state.reload,
  }),
}));

vi.mock("../lib/templateGeneration", async (importOriginal) => {
  const actual = await importOriginal<
    typeof import("../lib/templateGeneration")
  >();
  return {
    ...actual,
    templateGenerationApi: {
      ...actual.templateGenerationApi,
      updateUnit: state.updateUnit,
      updateStepSet: state.updateStepSet,
      confirmBatch: state.confirmBatch,
    },
  };
});

beforeEach(() => {
  window.history.replaceState(
    null,
    "",
    "/templates/generation/batch-1/final-check",
  );
  state.data = makeOtherBatch();
  state.updateUnit.mockResolvedValue(undefined);
  state.updateStepSet.mockResolvedValue(undefined);
  state.confirmBatch.mockResolvedValue({
    ...makeOtherBatch(),
    status: "completed",
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("TemplateGenerationFinalCheckPage", () => {
  it("requires a grade only when filename and paper evidence are both missing", () => {
    renderPage();

    expect(
      screen.getByText(
        "学年がファイル名またはテスト用紙から確認できませんでした。学年を選択してください。",
      ),
    ).toBeVisible();
    expect(screen.getByLabelText("学年")).toHaveValue("");
    expect(
      screen.getByRole("button", { name: "確認してテンプレートを作成" }),
    ).toBeDisabled();
  });

  it("shows both grade values and requires an explicit choice on conflict", () => {
    const batch = makeOtherBatch();
    batch.units[0] = {
      ...batch.units[0]!,
      filenameGrade: "grade3",
      paperGrade: "grade4",
      blockingWarnings: [
        { code: "GRADE_CONFLICT", severity: "blocking" },
      ],
    };
    state.data = batch;
    renderPage();

    expect(screen.getByText("学年が一致しません")).toBeVisible();
    expect(
      screen.getByText("ファイル名は3年生、テスト用紙は4年生です。正しい学年を選択してください。"),
    ).toBeVisible();
    expect(screen.getByLabelText("学年")).toHaveValue("");
  });

  it("does not bulk-apply a grade to an ambiguous filename conflict", () => {
    const batch = makeOtherBatch();
    batch.units[0] = {
      ...batch.units[0]!,
      blockingWarnings: [
        { code: "FILENAME_GRADE_CONFLICT", severity: "blocking" },
      ],
    };
    state.data = batch;
    renderPage();

    expect(
      screen.queryByRole("button", {
        name: "未設定のすべてにこの学年を適用",
      }),
    ).not.toBeInTheDocument();
  });

  it("keeps final-check fields read-only until every extraction succeeds", () => {
    state.data = { ...makeOtherBatch(), status: "generating" };
    renderPage();

    expect(screen.getByLabelText("テスト名")).toBeDisabled();
    expect(screen.getByLabelText("学年")).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: "変更を保存" }),
    ).not.toBeInTheDocument();
  });

  it("shows deterministic STEP names and never offers an AI-derived name edit", () => {
    state.data = makeStepBatch();
    renderPage();

    expect(screen.getAllByText("算数4年STEPセット1-1").length).toBeGreaterThan(0);
    expect(screen.getAllByText("算数4年STEPセット1-2").length).toBeGreaterThan(0);
    expect(screen.getAllByText("算数4年STEPセット1-3").length).toBeGreaterThan(0);
    expect(screen.queryByLabelText("基本名（枝番を除く）")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("テスト名")).not.toBeInTheDocument();
    expect(state.updateStepSet).not.toHaveBeenCalled();
  });

  it("uses the fixed HOP sequence without showing a name input", () => {
    const batch = makeOtherBatch();
    state.data = {
      ...batch,
      testType: "hop",
      subject: "理科",
      units: [{
        ...batch.units[0]!,
        sequence: 2,
        resolvedGrade: "grade6",
        printedTestName: "AIが付けた別名",
        finalTemplateName: "AIが付けた別名",
        blockingWarnings: [],
      }],
      blockingWarnings: [],
      finalCheckReady: true,
    };
    renderPage();

    expect(screen.getAllByText("理科6年HOP2").length).toBeGreaterThan(0);
    expect(screen.queryByLabelText("テスト名")).not.toBeInTheDocument();
  });

  it("surfaces a row-version conflict and never silently discards the edit", async () => {
    const batch = makeOtherBatch();
    batch.units[0] = {
      ...batch.units[0]!,
      resolvedGrade: "grade4",
      blockingWarnings: [],
    };
    batch.blockingWarnings = [];
    batch.finalCheckReady = true;
    state.data = batch;
    state.updateUnit.mockRejectedValue(
      new ApiError(409, {
        code: "STALE_ROW_VERSION",
        title: "Conflict",
      }),
    );
    renderPage();

    expect(screen.queryByText("学年の確認が必要です")).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("テスト名"), {
      target: { value: "新しい名前" },
    });
    fireEvent.click(screen.getByRole("button", { name: "変更を保存" }));

    expect(
      await screen.findByText(
        "別の画面で内容が更新されました。最新の内容を読み直してください。",
      ),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "最新の内容を読み込む" }),
    ).toBeEnabled();
    expect(screen.getByLabelText("テスト名")).toHaveValue("新しい名前");
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <Routes>
        <Route
          path="/templates/generation/:batchId/final-check"
          element={<TemplateGenerationFinalCheckPage />}
        />
      </Routes>
    </BrowserRouter>,
  );
}

function makeOtherBatch(): TemplateGenerationBatch {
  return {
    batchId: "batch-1",
    status: "needsFinalCheck",
    testType: "other",
    subject: "国語",
    answerStyle: "normal",
    promptSystem: "standard",
    sourceDisplayName: "国語テスト.pdf",
    sourcePageCount: 2,
    expectedUnitCount: 1,
    completedUnitCount: 1,
    failedUnitCount: 0,
    finalCheckReady: false,
    blockingWarnings: [
      { code: "GRADE_REQUIRED", severity: "blocking" },
    ],
    units: [
      {
        id: "unit-1",
        sequence: 1,
        status: "extracted",
        firstPage: 1,
        lastPage: 2,
        printedTestName: "国語まとめテスト",
        finalTemplateName: "国語まとめテスト",
        filenameGrade: null,
        paperGrade: null,
        resolvedGrade: "unknown",
        questionCount: 12,
        blockingWarnings: [
          { code: "GRADE_REQUIRED", severity: "blocking" },
        ],
        rowVersion: 2,
      },
    ],
    rowVersion: 4,
  };
}

function makeStepBatch(): TemplateGenerationBatch {
  return {
    batchId: "batch-1",
    status: "needsFinalCheck",
    testType: "step",
    subject: "算数",
    answerStyle: null,
    promptSystem: "standard",
    sourcePageCount: 6,
    expectedUnitCount: 3,
    completedUnitCount: 3,
    failedUnitCount: 0,
    finalCheckReady: false,
    blockingWarnings: [
      { code: "STEP_NAME_MISMATCH", severity: "blocking" },
    ],
    units: [1, 2, 3].map((variation) => ({
      id: `unit-${variation}`,
      sequence: variation,
      status: "extracted",
      firstPage: variation * 2 - 1,
      lastPage: variation * 2,
      stepSetIndex: 1,
      stepVariationIndex: variation,
      deterministicSuffix: `-${variation}`,
      printedTestName: `読み取り名${variation}`,
      filenameGrade: "grade4",
      paperGrade: "grade4",
      resolvedGrade: "grade4",
      questionCount: 10,
      blockingWarnings: [
        { code: "STEP_NAME_MISMATCH", severity: "blocking" },
      ],
      rowVersion: 2,
    })),
    rowVersion: 4,
  };
}

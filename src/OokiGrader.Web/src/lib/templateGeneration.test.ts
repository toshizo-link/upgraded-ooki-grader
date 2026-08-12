import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "./api";
import {
  deterministicPlanMessage,
  recentTemplateGenerationBatchIds,
  rememberTemplateGenerationBatchId,
  normalizeTemplateGenerationBatch,
  templateGenerationApi,
  warningMessage,
} from "./templateGeneration";

const apiState = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
}));

vi.mock("./api", async () => {
  const actual = await vi.importActual<typeof import("./api")>("./api");
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
  window.localStorage.clear();
  apiState.get.mockReset();
  apiState.post.mockReset();
});

describe("template generation adapter", () => {
  it("normalizes the server snapshot without editing generated OpenAPI files", () => {
    const batch = normalizeTemplateGenerationBatch({
      id: "batch-1",
      status: "needsFinalCheck",
      testType: "step",
      subject: "算数",
      answerStyle: null,
      promptSystem: "standard",
      sourcePageCount: 6,
      expectedUnitCount: 3,
      completedUnitCount: 3,
      failedUnitCount: 0,
      finalCheckReady: true,
      rowVersion: 7,
      units: [
        {
          id: "unit-1",
          sequence: 1,
          status: "extracted",
          firstPage: 1,
          lastPage: 2,
          stepSetIndex: 1,
          stepVariationIndex: 1,
          suffix: "-1",
          filenameGrade: "grade4",
          paperGrade: "grade4",
          resolvedGrade: "grade4",
          appliedRotations: [
            { pageNumber: 2, clockwiseDegrees: 90 },
          ],
          warnings: [
            {
              code: "ORIENTATION_CORRECTED",
              severity: "information",
            },
          ],
          rowVersion: 3,
        },
      ],
    });

    expect(batch.batchId).toBe("batch-1");
    expect(batch.status).toBe("needsFinalCheck");
    expect(batch.units[0]).toMatchObject({
      deterministicSuffix: "-1",
      resolvedGrade: "grade4",
      rowVersion: 3,
    });
    expect(batch.units[0]?.appliedRotations?.[0]?.clockwiseDegrees).toBe(90);
  });

  it("describes each deterministic client preview", () => {
    expect(deterministicPlanMessage("hop", 4)).toBe(
      "1ページごとに分割し、4件のテンプレートを生成します。",
    );
    expect(deterministicPlanMessage("step", 6)).toContain(
      "-1 / -2 / -3",
    );
    expect(deterministicPlanMessage("classPlacement", 1)).toBe(
      "PDF全体から1件のテンプレートを生成します。",
    );
    expect(deterministicPlanMessage("other", 1)).toBe(
      "PDF全体から1件のテンプレートを生成します。",
    );
  });

  it("uses the stable STEP page validation copy", () => {
    expect(warningMessage("STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX")).toBe(
      "STEPのPDFは、ページ数が6の倍数である必要があります。",
    );
  });

  it("distinguishes an invalid AI draft from incomplete teacher input", () => {
    expect(warningMessage("TEMPLATE_DRAFT_INVALID")).toBe(
      "AIが生成した下書きの形式を確認できませんでした。入力内容ではなく生成結果の問題です。失敗した項目だけ再試行してください。",
    );
    expect(warningMessage("FINAL_CHECK_INCOMPLETE")).toBe(
      "確認が必要な項目があります。",
    );
  });

  it("recovers a remembered direct progress URL when an older host has no list endpoint", async () => {
    rememberTemplateGenerationBatchId("batch-remembered");
    apiState.get.mockImplementation((path: string) => {
      if (path.endsWith("/resumable")) {
        return Promise.reject(
          new ApiError(404, { status: 404, title: "Not Found" }),
        );
      }
      return Promise.resolve(
        serverBatch({
          id: "batch-remembered",
          status: "generating",
          completedUnitCount: 21,
          expectedUnitCount: 36,
        }),
      );
    });

    const result = await templateGenerationApi.listResumableBatches();

    expect(result.browserRecoveryOnly).toBe(true);
    expect(result.items).toEqual([
      expect.objectContaining({
        id: "batch-remembered",
        status: "generating",
        completedUnitCount: 21,
        expectedUnitCount: 36,
      }),
    ]);
    expect(apiState.get).toHaveBeenCalledWith(
      "/template-generation-batches/batch-remembered",
      undefined,
      undefined,
    );
  });

  it("merges a browser-only batch after the authoritative server list", async () => {
    rememberTemplateGenerationBatchId("local-final-check");
    apiState.get.mockImplementation((path: string) => {
      if (path.endsWith("/resumable")) {
        return Promise.resolve({
          items: [
            {
              id: "server-running",
              status: "generating",
              testType: "step",
              subject: "理科",
              sourcePageCount: 72,
              expectedUnitCount: 36,
              completedUnitCount: 20,
              failedUnitCount: 0,
              rowVersion: 4,
            },
          ],
          limit: 20,
        });
      }
      return Promise.resolve(
        serverBatch({
          id: "local-final-check",
          status: "needsFinalCheck",
          completedUnitCount: 3,
          expectedUnitCount: 3,
        }),
      );
    });

    const result = await templateGenerationApi.listResumableBatches();

    expect(result.browserRecoveryOnly).toBe(false);
    expect(result.items.map((item) => item.id)).toEqual([
      "server-running",
      "local-final-check",
    ]);
  });

  it("prunes remembered IDs that are missing or not owned by the signed-in teacher", async () => {
    rememberTemplateGenerationBatchId("batch-no-access");
    apiState.get.mockImplementation((path: string) => {
      if (path.endsWith("/resumable")) {
        return Promise.reject(
          new ApiError(404, { status: 404, title: "Not Found" }),
        );
      }
      return Promise.reject(
        new ApiError(403, { status: 403, title: "Forbidden" }),
      );
    });

    const result = await templateGenerationApi.listResumableBatches();

    expect(result.items).toEqual([]);
    expect(recentTemplateGenerationBatchIds()).toEqual([]);
  });

  it("does not hide real resumable-list server failures", async () => {
    apiState.get.mockRejectedValue(
      new ApiError(503, { status: 503, title: "Unavailable" }),
    );

    await expect(
      templateGenerationApi.listResumableBatches(),
    ).rejects.toMatchObject({ status: 503 });
  });

  it("stores only a bounded set of opaque batch IDs", () => {
    for (let index = 0; index < 25; index += 1) {
      rememberTemplateGenerationBatchId(`batch-${index}`);
    }

    expect(recentTemplateGenerationBatchIds()).toHaveLength(20);
    expect(recentTemplateGenerationBatchIds()[0]).toBe("batch-24");
    expect(JSON.stringify(window.localStorage)).not.toContain("理科");
  });
});

function serverBatch(changes: Record<string, unknown> = {}) {
  return {
    id: "batch-1",
    status: "generating",
    testType: "step",
    subject: "理科",
    answerStyle: null,
    promptSystem: "standard",
    sourcePageCount: 6,
    expectedUnitCount: 3,
    completedUnitCount: 1,
    failedUnitCount: 0,
    rowVersion: 3,
    units: [
      {
        id: "unit-1",
        sequence: 1,
        status: "extracted",
        firstPage: 1,
        lastPage: 2,
        rowVersion: 2,
      },
    ],
    ...changes,
  };
}

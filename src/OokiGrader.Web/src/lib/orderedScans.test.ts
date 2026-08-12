import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "./api";
import {
  groupOrderedScans,
  moveScanItem,
  naturalSortScanItems,
  orderedScanApi,
} from "./orderedScans";

afterEach(() => vi.restoreAllMocks());

function scan(name: string) {
  return { file: new File([name], name, { type: "application/pdf" }) };
}

describe("ordered scan planning", () => {
  it("uses a stable numeric file-name order instead of lexical scanner order", () => {
    const sameA = scan("scan-2-a.pdf");
    const sameB = scan("scan-2-A.pdf");
    const result = naturalSortScanItems([
      scan("scan-10.pdf"),
      sameA,
      scan("scan-1.pdf"),
      sameB,
    ]);

    expect(result.map((item) => item.file.name)).toEqual([
      "scan-1.pdf",
      "scan-2-a.pdf",
      "scan-2-A.pdf",
      "scan-10.pdf",
    ]);
    expect(result[1]).toBe(sameA);
    expect(result[2]).toBe(sameB);
  });

  it.each([
    ["HOP", 1, 5, 5],
    ["STEP", 2, 6, 3],
    ["Other", 4, 8, 2],
  ])("groups %s using the template page count", (_, pageCount, files, groups) => {
    const result = groupOrderedScans(
      Array.from({ length: files }, (_, index) => index),
      pageCount,
    );

    expect(result).toHaveLength(groups);
    expect(result.every((group) => group.complete)).toBe(true);
    expect(result.flatMap((group) => group.items).map((item) => item.inputOrdinal))
      .toEqual(Array.from({ length: files }, (_, index) => index + 1));
  });

  it("keeps an incomplete final Other submission visible", () => {
    const groups = groupOrderedScans([1, 2, 3, 4, 5, 6], 4);

    expect(groups).toMatchObject([
      { groupNumber: 1, complete: true },
      { groupNumber: 2, complete: false },
    ]);
    expect(groups[1]?.items).toHaveLength(2);
  });

  it("moves one page without mutating the frozen source order", () => {
    const source = ["a", "b", "c"];

    expect(moveScanItem(source, 2, 0)).toEqual(["c", "a", "b"]);
    expect(source).toEqual(["a", "b", "c"]);
  });

  it("uses the typed create and row-version-gated finalize routes", async () => {
    const detail = { id: "batch-1", rowVersion: 7 };
    const post = vi.spyOn(api, "post").mockResolvedValue(detail);
    const items = [
      { clientItemId: "client-1", fileName: "scan-1.pdf", inputOrdinal: 1 },
    ];

    await orderedScanApi.create("session/1", { items }, "create-key");
    await orderedScanApi.finalize("batch/1", 7);
    await orderedScanApi.cancel("batch/1", 8);

    expect(post).toHaveBeenNthCalledWith(
      1,
      "/test-sessions/session%2F1/ordered-scan-batches",
      { items },
      { idempotencyKey: "create-key" },
    );
    expect(post).toHaveBeenNthCalledWith(
      2,
      "/ordered-scan-batches/batch%2F1:finalize",
      { expectedRowVersion: 7 },
      expect.objectContaining({ idempotencyKey: expect.any(String) }),
    );
    expect(post).toHaveBeenNthCalledWith(
      3,
      "/ordered-scan-batches/batch%2F1:cancel",
      { expectedRowVersion: 8 },
      expect.objectContaining({ idempotencyKey: expect.any(String) }),
    );
  });
});

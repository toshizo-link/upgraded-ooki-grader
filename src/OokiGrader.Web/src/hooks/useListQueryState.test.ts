import { describe, expect, it } from "vitest";
import {
  canAppendCursorTrail,
  isIsoDate,
  listFilterFingerprint,
  normalizeListSearch,
} from "./useListQueryState";

describe("list query safety", () => {
  it("normalizes tolerant search without changing Japanese text", () => {
    expect(normalizeListSearch("  佐藤\t 花子  ")).toBe("佐藤 花子");
    expect(isIsoDate("2026-02-29")).toBe(false);
    expect(isIsoDate("2028-02-29")).toBe(true);
  });

  it("keeps paging cursors out of the selection fingerprint", () => {
    const first = new URLSearchParams("q=佐藤&sort=-name&page=2&cursor=opaque&trail=%5B%22%22%5D");
    const second = new URLSearchParams("sort=-name&q=佐藤&pageSize=100");
    expect(listFilterFingerprint(first)).toBe(listFilterFingerprint(second));
  });

  it("refuses a next cursor before an aggregate trail can exceed 32 KiB", () => {
    const segment = "x".repeat(8_000);
    expect(canAppendCursorTrail([segment, segment], segment, segment)).toBe(true);
    expect(canAppendCursorTrail([segment, segment, segment], segment, segment)).toBe(false);
    expect(canAppendCursorTrail([], undefined, "x".repeat(8_193))).toBe(false);
  });
});

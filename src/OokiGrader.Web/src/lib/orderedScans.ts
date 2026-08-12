import type {
  CreateOrderedScanBatchRequest,
  OrderedScanBatchDetail,
} from "../types";
import { api, newIdempotencyKey } from "./api";

const scannerFileNameCollator = new Intl.Collator("ja", {
  numeric: true,
  sensitivity: "base",
});

export interface OrderedScanDraftItem {
  id: string;
  file: File;
}

export interface OrderedScanGroup<T> {
  groupNumber: number;
  items: Array<{ item: T; pageNumber: number; inputOrdinal: number }>;
  complete: boolean;
}

export function naturalSortScanItems<T extends { file: Pick<File, "name"> }>(
  items: readonly T[],
): T[] {
  return items
    .map((item, originalIndex) => ({ item, originalIndex }))
    .sort(
      (left, right) =>
        scannerFileNameCollator.compare(
          left.item.file.name,
          right.item.file.name,
        ) || left.originalIndex - right.originalIndex,
    )
    .map(({ item }) => item);
}

export function moveScanItem<T>(
  items: readonly T[],
  fromIndex: number,
  toIndex: number,
): T[] {
  if (
    fromIndex < 0 ||
    fromIndex >= items.length ||
    toIndex < 0 ||
    toIndex >= items.length ||
    fromIndex === toIndex
  ) {
    return [...items];
  }

  const result = [...items];
  const [moved] = result.splice(fromIndex, 1);
  if (moved === undefined) return result;
  result.splice(toIndex, 0, moved);
  return result;
}

export function groupOrderedScans<T>(
  items: readonly T[],
  expectedPageCount: number,
): OrderedScanGroup<T>[] {
  if (!Number.isInteger(expectedPageCount) || expectedPageCount < 1) {
    return [];
  }

  const groups: OrderedScanGroup<T>[] = [];
  for (let start = 0; start < items.length; start += expectedPageCount) {
    const groupItems = items
      .slice(start, start + expectedPageCount)
      .map((item, offset) => ({
        item,
        pageNumber: offset + 1,
        inputOrdinal: start + offset + 1,
      }));
    groups.push({
      groupNumber: groups.length + 1,
      items: groupItems,
      complete: groupItems.length === expectedPageCount,
    });
  }
  return groups;
}

export function orderedScanBatchStorageKey(sessionId: string) {
  return `ooki:ordered-scan-batch:${sessionId}`;
}

export const orderedScanApi = {
  create(
    sessionId: string,
    body: CreateOrderedScanBatchRequest,
    idempotencyKey: string = newIdempotencyKey(),
  ) {
    return api.post<OrderedScanBatchDetail>(
      `/test-sessions/${encodeURIComponent(sessionId)}/ordered-scan-batches`,
      body,
      { idempotencyKey },
    );
  },

  get(batchId: string, signal?: AbortSignal) {
    return api.get<OrderedScanBatchDetail>(
      `/ordered-scan-batches/${encodeURIComponent(batchId)}`,
      undefined,
      signal,
    );
  },

  finalize(batchId: string, expectedRowVersion?: number) {
    return api.post<OrderedScanBatchDetail>(
      `/ordered-scan-batches/${encodeURIComponent(batchId)}:finalize`,
      { expectedRowVersion },
      { idempotencyKey: newIdempotencyKey() },
    );
  },

  cancel(batchId: string, expectedRowVersion: number) {
    return api.post<OrderedScanBatchDetail>(
      `/ordered-scan-batches/${encodeURIComponent(batchId)}:cancel`,
      { expectedRowVersion },
      { idempotencyKey: newIdempotencyKey() },
    );
  },
};

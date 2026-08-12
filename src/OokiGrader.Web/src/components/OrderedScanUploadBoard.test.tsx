import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { orderedScanApi, orderedScanBatchStorageKey } from "../lib/orderedScans";
import type { OrderedScanBatchDetail } from "../types";
import { OrderedScanUploadBoard } from "./OrderedScanUploadBoard";

afterEach(() => {
  cleanup();
  window.sessionStorage.clear();
});

describe("OrderedScanUploadBoard", () => {
  it("previews four-page Other submissions with explicit boundaries", () => {
    render(
      <OrderedScanUploadBoard
        sessionId="session-other"
        expectedPageCount={4}
        isOpen
        onBatchChanged={vi.fn()}
      />,
    );

    const input = document.querySelector('input[type="file"]');
    expect(input).not.toBeNull();
    const files = [6, 2, 4, 1, 5, 3].map(
      (number) =>
        new File([String(number)], `scan-${number}.pdf`, {
          type: "application/pdf",
        }),
    );
    fireEvent.change(input!, { target: { files } });

    expect(screen.getByText("1答案 4ページ")).toBeVisible();
    expect(
      screen.getByText("スキャン順が生徒のまとまりになります"),
    ).toBeVisible();
    expect(screen.getByText("見込み 2答案・完成 1答案")).toBeVisible();
    const first = screen.getByLabelText("答案 1");
    const second = screen.getByLabelText("答案 2");
    expect(within(first).getByText("4 / 4ページ")).toBeVisible();
    expect(within(first).getByText("scan-1.pdf")).toBeVisible();
    expect(within(first).getByText("scan-4.pdf")).toBeVisible();
    expect(within(second).getByText("2 / 4ページ")).toBeVisible();
    expect(within(second).getByText("scan-5.pdf")).toBeVisible();
    expect(within(second).getByText("scan-6.pdf")).toBeVisible();
    expect(screen.getByText("あと2ページ追加すると送信できます。")).toBeVisible();
    expect(
      screen.getByRole("button", { name: "この順番でページを送信" }),
    ).toBeDisabled();
  });

  it("lets the teacher correct order before the batch is frozen", () => {
    render(
      <OrderedScanUploadBoard
        sessionId="session-step"
        expectedPageCount={2}
        isOpen
        onBatchChanged={vi.fn()}
      />,
    );
    const files = [1, 2, 3, 4].map(
      (number) =>
        new File([String(number)], `scan-${number}.pdf`, {
          type: "application/pdf",
        }),
    );
    fireEvent.change(document.querySelector('input[type="file"]')!, {
      target: { files },
    });

    fireEvent.click(
      screen.getByRole("button", { name: "scan-3.pdfを1つ前へ移動" }),
    );

    const first = screen.getByLabelText("答案 1");
    expect(within(first).getByText("scan-1.pdf")).toBeVisible();
    expect(within(first).getByText("scan-3.pdf")).toBeVisible();
    expect(within(first).queryByText("scan-2.pdf")).not.toBeInTheDocument();
  });

  it("reuses the create key when a batch response is lost", async () => {
    const create = vi
      .spyOn(orderedScanApi, "create")
      .mockRejectedValue(new Error("response lost"));
    render(
      <OrderedScanUploadBoard
        sessionId="session-ambiguous-create"
        expectedPageCount={2}
        isOpen
        onBatchChanged={vi.fn()}
      />,
    );
    const files = [1, 2].map(
      (number) =>
        new File([String(number)], `scan-${number}.pdf`, {
          type: "application/pdf",
        }),
    );
    fireEvent.change(document.querySelector('input[type="file"]')!, {
      target: { files },
    });

    const submit = screen.getByRole("button", {
      name: "この順番でページを送信",
    });
    fireEvent.click(submit);
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(submit).toBeEnabled());
    fireEvent.click(submit);
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));

    expect(create.mock.calls[0]?.[2]).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    );
    expect(create.mock.calls[1]?.[2]).toBe(create.mock.calls[0]?.[2]);
  });

  it("does not guess a page count when the published template lacks one", () => {
    render(
      <OrderedScanUploadBoard
        sessionId="session-invalid"
        isOpen
        onBatchChanged={vi.fn()}
      />,
    );

    expect(screen.getByText("答案のページ数を確認できません")).toBeVisible();
    expect(document.querySelector('input[type="file"]')).toBeNull();
  });

  it("finalizes a restored fully uploaded batch without local File objects", async () => {
    const uploaded = makeBatch("draft");
    const processing = { ...uploaded, status: "processing", rowVersion: 9 };
    window.sessionStorage.setItem(
      orderedScanBatchStorageKey("session-restored"),
      uploaded.id,
    );
    vi.spyOn(orderedScanApi, "get").mockResolvedValue(uploaded);
    const finalize = vi
      .spyOn(orderedScanApi, "finalize")
      .mockResolvedValue(processing);

    render(
      <OrderedScanUploadBoard
        sessionId="session-restored"
        expectedPageCount={4}
        isOpen
        onBatchChanged={vi.fn()}
      />,
    );

    expect(
      await screen.findByText("送信済みバッチを復元しました"),
    ).toBeVisible();
    expect(screen.getByText(/元のファイルを選び直さず/)).toBeVisible();
    fireEvent.click(
      screen.getByRole("button", { name: "答案を組み立てて採点へ" }),
    );

    await waitFor(() => expect(finalize).toHaveBeenCalledWith("batch-1", 7));
  });

  it("cancels a review batch before clearing it for a new upload", async () => {
    const review = makeBatch("needsReview");
    const cancelled = { ...review, status: "cancelled", rowVersion: 8 };
    window.sessionStorage.setItem(
      orderedScanBatchStorageKey("session-restored"),
      review.id,
    );
    vi.spyOn(orderedScanApi, "get").mockResolvedValue(review);
    const cancel = vi
      .spyOn(orderedScanApi, "cancel")
      .mockResolvedValue(cancelled);
    vi.spyOn(window, "confirm").mockReturnValue(true);

    render(
      <OrderedScanUploadBoard
        sessionId="session-restored"
        expectedPageCount={4}
        isOpen
        onBatchChanged={vi.fn()}
      />,
    );

    fireEvent.click(
      await screen.findByRole("button", {
        name: "取り消して次のバッチを追加",
      }),
    );

    await waitFor(() => expect(cancel).toHaveBeenCalledWith("batch-1", 7));
    await waitFor(() =>
      expect(
        window.sessionStorage.getItem(
          orderedScanBatchStorageKey("session-restored"),
        ),
      ).toBeNull(),
    );
    expect(document.querySelector('input[type="file"]')).not.toBeNull();
  });
});

function makeBatch(status: string): OrderedScanBatchDetail {
  return {
    id: "batch-1",
    testSessionId: "session-restored",
    expectedPageCount: 4,
    status,
    assemblyPolicyVersion: "ordered-scan-v1",
    planHash: null,
    lastErrorCode: null,
    rowVersion: 7,
    expiresAt: "2026-08-11T00:00:00Z",
    itemCount: 4,
    items: Array.from({ length: 4 }, (_, index) => ({
      id: `item-${index + 1}`,
      uploadId: `upload-${index + 1}`,
      clientItemId: `client-${index + 1}`,
      fileName: `scan-${index + 1}.pdf`,
      inputOrdinal: index + 1,
      status: "uploaded",
      rowVersion: 1,
    })),
    groups: [],
    submissionIds: [],
    issues: [],
  };
}

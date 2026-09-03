import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { OrderedScanBatchDetail, TestSessionSummary } from "../types";
import { CrossSessionScanIntake } from "./CrossSessionScanIntake";

const mocks = vi.hoisted(() => ({
  post: vi.fn(),
  uploadFile: vi.fn(),
  create: vi.fn(),
  get: vi.fn(),
  finalize: vi.fn(),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return {
    ...actual,
    api: { ...actual.api, post: mocks.post },
    uploadFile: mocks.uploadFile,
  };
});

vi.mock("../lib/orderedScans", async () => {
  const actual = await vi.importActual<typeof import("../lib/orderedScans")>(
    "../lib/orderedScans",
  );
  return {
    ...actual,
    orderedScanApi: {
      create: mocks.create,
      get: mocks.get,
      finalize: mocks.finalize,
      cancel: vi.fn(),
    },
  };
});

beforeEach(() => {
  mocks.post.mockImplementation((_path: string, _body: Blob, options: { query: { clientItemId: string; inputOrdinal: number } }) =>
    Promise.resolve({
      classificationMode: "server_visual_alignment_v1",
      openSessions: [routingSession()],
      items: [{
        clientItemId: options.query.clientItemId,
        inputOrdinal: options.query.inputOrdinal,
        state: "routed",
        destinationSessionId: "session-1",
        expectedPageCount: 1,
        detectedTemplatePageNumber: 1,
      }],
    }),
  );
  mocks.uploadFile.mockImplementation(async (_file: File, options: { onProgress?: (sent: number, total: number) => void }) => {
    options.onProgress?.(100, 100);
    return { uploadId: "upload", state: "completed", orderedScanItemId: "item" };
  });
  mocks.create.mockResolvedValue(batch("draft", 1));
  mocks.get.mockResolvedValue(batch("draft", 2));
  mocks.finalize.mockResolvedValue(batch("completed", 3));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("CrossSessionScanIntake", () => {
  it("natural-sorts, server-routes, and uploads every PDF sequentially", async () => {
    const changed = vi.fn();
    const { container } = render(
      <CrossSessionScanIntake
        openSessions={[openSession()]}
        onChanged={changed}
      />,
    );
    const input = container.querySelector<HTMLInputElement>("input[type=file]")!;
    const scan10 = new File(["pdf-10"], "scan-10.pdf", { type: "application/pdf" });
    const scan2 = new File(["pdf-2"], "scan-2.pdf", { type: "application/pdf" });

    fireEvent.change(input, { target: { files: [scan10, scan2] } });

    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2));
    expect(mocks.post.mock.calls.map((call) => call[2].query.fileName)).toEqual([
      "scan-2.pdf",
      "scan-10.pdf",
    ]);
    expect(mocks.post.mock.calls.map((call) => call[0])).toEqual([
      "/ordered-scan-routing:classify-page",
      "/ordered-scan-routing:classify-page",
    ]);
    expect(mocks.post.mock.calls.every((call) => call[1] instanceof Blob)).toBe(true);
    expect(await screen.findAllByText("受付先決定")).toHaveLength(2);

    fireEvent.click(screen.getByRole("button", { name: "仕分け結果で受付を開始" }));

    await waitFor(() => expect(mocks.uploadFile).toHaveBeenCalledTimes(2));
    expect(mocks.uploadFile.mock.calls.map(([file]) => (file as File).name)).toEqual([
      "scan-2.pdf",
      "scan-10.pdf",
    ]);
    expect(mocks.create).toHaveBeenCalledWith(
      "session-1",
      {
        items: [
          expect.objectContaining({ fileName: "scan-2.pdf", inputOrdinal: 1 }),
          expect.objectContaining({ fileName: "scan-10.pdf", inputOrdinal: 2 }),
        ],
      },
      expect.any(String),
    );
    expect(await screen.findAllByText("受付完了")).toHaveLength(2);
    expect(changed).toHaveBeenCalledOnce();
  });

  it("keeps an ambiguous route visible for a teacher to choose", async () => {
    mocks.post.mockImplementationOnce((_path: string, _body: Blob, options: { query: { clientItemId: string } }) =>
      Promise.resolve({
        classificationMode: "server_visual_alignment_v1",
        openSessions: [routingSession(), { ...routingSession(), id: "session-2", title: "算数" }],
        items: [{
          clientItemId: options.query.clientItemId,
          inputOrdinal: 1,
          state: "needsReview",
          destinationSessionId: null,
          expectedPageCount: null,
          detectedTemplatePageNumber: null,
          issueCode: "ROUTING_VISUAL_AMBIGUOUS",
        }],
      }),
    );
    const { container } = render(
      <CrossSessionScanIntake openSessions={[openSession()]} onChanged={vi.fn()} />,
    );
    const input = container.querySelector<HTMLInputElement>("input[type=file]")!;
    fireEvent.change(input, {
      target: { files: [new File(["pdf"], "0001.pdf", { type: "application/pdf" })] },
    });

    expect(await screen.findByText("受付先を確認")).toBeVisible();
    const destination = screen.getByLabelText("0001.pdfの受付先");
    fireEvent.change(destination, { target: { value: "session-2" } });
    expect(screen.getByRole("button", { name: "仕分け結果で受付を開始" })).toBeEnabled();
  });
});

function openSession(): TestSessionSummary {
  return {
    id: "session-1",
    templateId: "template-1",
    templateVersionId: "version-1",
    templateTitle: "理科",
    testDate: "2026-08-27",
    priority: "expedite",
    state: "open",
    expectedSubmissionPageCount: 1,
  };
}

function routingSession() {
  return {
    id: "session-1",
    title: "理科",
    testDate: "2026-08-27",
    expectedPageCount: 1,
  };
}

function batch(status: string, rowVersion: number): OrderedScanBatchDetail {
  return {
    id: "batch-1",
    testSessionId: "session-1",
    expectedPageCount: 1,
    status,
    assemblyPolicyVersion: "ordered-scan-v1",
    rowVersion,
    expiresAt: "2026-08-28T00:00:00Z",
    itemCount: 2,
    items: [],
    groups: [],
    submissionIds: status === "completed" ? ["submission-1", "submission-2"] : [],
    issues: [],
  };
}

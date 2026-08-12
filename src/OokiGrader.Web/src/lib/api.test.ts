import { afterEach, describe, expect, it, vi } from "vitest";
import { api, resetCsrfTokenForTests, uploadFile } from "./api";

describe("typed API client", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    resetCsrfTokenForTests();
  });

  it("uses the same-origin v1 path and includes the session cookie", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ items: [], nextCursor: null }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await api.get("/students", { search: "大木" });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/students?search=%E5%A4%A7%E6%9C%A8",
      expect.objectContaining({
        credentials: "include",
        method: "GET",
      }),
    );
  });

  it("preserves RFC problem details and the correlation id", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          title: "The scan needs attention",
          detail: "Expected page 2 was not found.",
          code: "SCAN_PAGE_MISSING",
        }),
        {
          status: 422,
          headers: {
            "Content-Type": "application/problem+json",
            "X-Correlation-Id": "01JCORRELATION",
          },
        },
      ),
    );
    vi.stubGlobal("fetch", fetchMock);

    await expect(api.get("/submissions/01J")).rejects.toMatchObject({
      status: 422,
      message: "Expected page 2 was not found.",
      correlationId: "01JCORRELATION",
    });
  });

  it("can omit the idempotency header for a read-only POST preview", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ csrfToken: "csrf-1" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ resultCount: 0, studentCount: 0 }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await api.post(
      "/transcript-exports:preview",
      { selector: { submissionIds: ["submission-1"] } },
      { idempotency: false },
    );

    const options = fetchMock.mock.calls[1]?.[1] as RequestInit;
    const headers = new Headers(options.headers);
    expect(headers.get("X-CSRF-Token")).toBe("csrf-1");
    expect(headers.has("Idempotency-Key")).toBe(false);
  });

  it("sends immutable ordered-batch identity with every single-page upload", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ csrfToken: "csrf-1" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            uploadId: "upload-1",
            state: "uploading",
            offset: 0,
            maxChunkBytes: 8_388_608,
            expiresAt: "2026-08-11T00:00:00Z",
            chunkUrl: "/api/v1/uploads/upload-1/content",
          }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            uploadId: "upload-1",
            state: "uploading",
            offset: 0,
            maxChunkBytes: 8_388_608,
            expiresAt: "2026-08-11T00:00:00Z",
            chunkUrl: "/api/v1/uploads/upload-1/content",
          }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        ),
      )
      .mockResolvedValueOnce(
        new Response(undefined, {
          status: 204,
          headers: { "Upload-Offset": "3" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            uploadId: "upload-1",
            state: "completed",
            orderedScanItemId: "item-1",
          }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        ),
      );
    vi.stubGlobal("fetch", fetchMock);

    await uploadFile(
      new File(["pdf"], "scan-2.pdf"),
      {
        purpose: "completedTestPage",
        testSessionId: "session-1",
        orderedScanBatchId: "batch-1",
        inputOrdinal: 2,
        clientItemId: "client-2",
      },
    );

    const createOptions = fetchMock.mock.calls[1]?.[1] as RequestInit;
    expect(JSON.parse(String(createOptions.body))).toMatchObject({
      purpose: "completedTestPage",
      testSessionId: "session-1",
      orderedScanBatchId: "batch-1",
      inputOrdinal: 2,
      clientItemId: "client-2",
      declaredMimeType: "application/pdf",
    });
    expect(fetchMock.mock.calls[3]?.[1]).toMatchObject({
      method: "PATCH",
      credentials: "include",
    });
  });

  it("resumes from authoritative upload state after a replayed create response", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ csrfToken: "csrf-1" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            uploadId: "upload-1",
            state: "uploading",
            offset: 0,
            maxChunkBytes: 8_388_608,
            expiresAt: "2026-08-11T00:00:00Z",
            chunkUrl: "/api/v1/uploads/upload-1/content",
          }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            uploadId: "upload-1",
            state: "finalizing",
            offset: 3,
            maxChunkBytes: 8_388_608,
            expiresAt: "2026-08-11T00:00:00Z",
            chunkUrl: "/api/v1/uploads/upload-1/content",
          }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            uploadId: "upload-1",
            state: "completed",
            orderedScanItemId: "item-1",
          }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        ),
      );
    vi.stubGlobal("fetch", fetchMock);

    const result = await uploadFile(new File(["pdf"], "scan-1.pdf"), {
      purpose: "completedTestPage",
      testSessionId: "session-1",
      orderedScanBatchId: "batch-1",
      inputOrdinal: 1,
      clientItemId: "client-1",
      createIdempotencyKey: "create-key",
      finalizeIdempotencyKey: "finalize-key",
    });

    expect(result).toMatchObject({
      state: "completed",
      orderedScanItemId: "item-1",
    });
    expect(fetchMock).toHaveBeenCalledTimes(4);
    expect(fetchMock.mock.calls.some(([, init]) =>
      (init as RequestInit | undefined)?.method === "PATCH")).toBe(false);
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      "/api/v1/uploads/upload-1:finalize",
    );
  });
});

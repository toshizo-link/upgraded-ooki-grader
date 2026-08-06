import { afterEach, describe, expect, it, vi } from "vitest";
import { api, resetCsrfTokenForTests } from "./api";

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
});

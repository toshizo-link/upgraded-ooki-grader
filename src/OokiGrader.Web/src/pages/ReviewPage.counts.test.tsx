import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter } from "../router";
import { ReviewPage } from "./ReviewPage";

const apiState = vi.hoisted(() => ({
  counts: {
    needsNameReview: 4,
    needsGradeReview: 7,
    readyToFinalize: 3,
  },
  get: vi.fn(),
}));

vi.mock("../auth/SessionContext", () => ({
  useSession: () => ({ hasAnyRole: () => true }),
}));

vi.mock("../lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/api")>();
  return {
    ...actual,
    api: {
      ...actual.api,
      get: apiState.get,
    },
  };
});

beforeEach(() => {
  window.history.replaceState(null, "", "/review");
  apiState.counts = {
    needsNameReview: 4,
    needsGradeReview: 7,
    readyToFinalize: 3,
  };
  apiState.get.mockImplementation((path: string) => {
    if (path === "/review/counts") return Promise.resolve(apiState.counts);
    return Promise.resolve({
      items: [],
      nextCursor: null,
      totalApproximate: 0,
    });
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("ReviewPage queue counts", () => {
  it("shows every section count on first load without fetching inactive queues", async () => {
    render(
      <BrowserRouter>
        <ReviewPage />
      </BrowserRouter>,
    );

    expect(
      await screen.findByRole("tab", { name: /生徒名\s*4/ }),
    ).toBeVisible();
    expect(screen.getByRole("tab", { name: /採点\s*7/ })).toBeVisible();
    expect(screen.getByRole("tab", { name: /確定\s*3/ })).toBeVisible();

    await waitFor(() => {
      expect(apiState.get).toHaveBeenCalledWith(
        "/review/counts",
        undefined,
        expect.any(AbortSignal),
      );
      expect(apiState.get).toHaveBeenCalledWith(
        "/review/name",
        { pageSize: 100 },
        expect.any(AbortSignal),
      );
    });
    expect(apiState.get).not.toHaveBeenCalledWith(
      "/review/grading",
      expect.anything(),
      expect.anything(),
    );
    expect(apiState.get).not.toHaveBeenCalledWith(
      "/submissions",
      expect.anything(),
      expect.anything(),
    );

    fireEvent.click(screen.getByRole("tab", { name: /採点\s*7/ }));

    await waitFor(() =>
      expect(apiState.get).toHaveBeenCalledWith(
        "/review/grading",
        { pageSize: 100 },
        expect.any(AbortSignal),
      ),
    );
    expect(window.location.search).toBe("?tab=grading");
    expect(screen.getByRole("tab", { name: /生徒名\s*4/ })).toBeVisible();
    expect(screen.getByRole("tab", { name: /確定\s*3/ })).toBeVisible();
  });

  it("refreshes all badges when the status stream reports changed counts", async () => {
    render(
      <BrowserRouter>
        <ReviewPage />
      </BrowserRouter>,
    );

    expect(
      await screen.findByRole("tab", { name: /生徒名\s*4/ }),
    ).toBeVisible();
    apiState.counts = {
      needsNameReview: 2,
      needsGradeReview: 6,
      readyToFinalize: 5,
    };

    act(() => {
      window.dispatchEvent(
        new CustomEvent("ooki:status", {
          detail: { type: "review.counts", payload: apiState.counts },
        }),
      );
    });

    expect(
      await screen.findByRole("tab", { name: /生徒名\s*2/ }),
    ).toBeVisible();
    expect(screen.getByRole("tab", { name: /採点\s*6/ })).toBeVisible();
    expect(screen.getByRole("tab", { name: /確定\s*5/ })).toBeVisible();
    expect(
      apiState.get.mock.calls.filter(([path]) => path === "/review/counts"),
    ).toHaveLength(2);
  });
});

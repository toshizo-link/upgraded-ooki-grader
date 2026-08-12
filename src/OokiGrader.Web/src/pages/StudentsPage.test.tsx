import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter } from "../router";
import type { StudentSummary } from "../types";
import { StudentsPage } from "./StudentsPage";

const apiState = vi.hoisted(() => ({
  get: vi.fn(),
}));

vi.mock("../auth/SessionContext", () => ({
  useSession: () => ({ hasAnyRole: () => true }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return {
    ...actual,
    api: { ...actual.api, get: apiState.get },
  };
});

beforeEach(() => {
  apiState.get.mockResolvedValue({
    items: [makeStudent()],
    nextCursor: null,
    totalApproximate: 1,
    facets: {
      grades: [{ value: "小4", label: "小4", count: 1 }],
      classes: [{ value: "A組", label: "A組", count: 1 }],
      courses: [{ value: "本科", label: "本科", count: 1 }],
    },
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("StudentsPage list controls", () => {
  it("combines URL filters and an allowlisted descending sort", async () => {
    window.history.replaceState(
      null,
      "",
      "/students?q=%E4%BD%90%E8%97%A4&status=inactive&class=A%E7%B5%84&course=%E6%9C%AC%E7%A7%91&grade=%E5%B0%8F4&sort=-name&pageSize=25",
    );
    renderPage();

    await waitFor(() =>
      expect(apiState.get).toHaveBeenCalledWith(
        "/students",
        expect.objectContaining({
          search: "佐藤",
          status: "inactive",
          class: "A組",
          course: "本科",
          grade: "小4",
          sort: "-name",
          pageSize: 25,
          includeFacets: true,
        }),
        expect.any(AbortSignal),
      ),
    );
    expect(screen.getByLabelText("在籍状態")).toHaveValue("inactive");
    expect(screen.getByLabelText("並び順")).toHaveValue("name");
    expect(screen.getByLabelText("並び方向")).toHaveValue("desc");
  });

  it("recovers invalid URL enums, sort, and cursor history", async () => {
    window.history.replaceState(
      null,
      "",
      "/students?status=deleted&sort=sql&page=2&cursor=opaque&trail=broken&unknown=kept",
    );
    renderPage();

    await waitFor(() => expect(window.location.search).toBe(""));
    await waitFor(() =>
      expect(apiState.get).toHaveBeenLastCalledWith(
        "/students",
        expect.objectContaining({ status: "active", sort: "studentNumber" }),
        expect.any(AbortSignal),
      ),
    );

    fireEvent.change(screen.getByLabelText("並び方向"), {
      target: { value: "desc" },
    });
    await waitFor(() => expect(window.location.search).toContain("sort=-studentNumber"));
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <StudentsPage />
    </BrowserRouter>,
  );
}

function makeStudent(): StudentSummary {
  return {
    id: "student-1",
    studentNumber: "S001",
    displayName: "佐藤 花子",
    gradeLabel: "小4",
    classLabel: "A組",
    course: "本科",
    active: false,
  };
}

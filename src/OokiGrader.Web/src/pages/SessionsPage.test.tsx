import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter } from "../router";
import type { TemplateSummary, TestSessionSummary } from "../types";
import { loadActiveTemplates, SessionsPage } from "./SessionsPage";

const queryState = vi.hoisted(() => ({
  session: undefined as TestSessionSummary | undefined,
  templates: [] as TemplateSummary[],
}));

const apiState = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
}));

vi.mock("../auth/SessionContext", () => ({
  useSession: () => ({ hasAnyRole: () => true }),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: (key: string) => {
    const data = key.startsWith("sessions:")
      ? {
          items: queryState.session ? [queryState.session] : [],
          nextCursor: null,
          totalApproximate: queryState.session ? 1 : 0,
          facets: {
            templates: [{ value: "template-1", label: "4年理科 HOP", count: 1 }],
            classes: [{ value: "A組", label: "A組", count: 1 }],
            courses: [{ value: "本科", label: "本科", count: 1 }],
          },
        }
      : key === "published-templates-for-session"
        ? {
            items: queryState.templates,
            nextCursor: null,
            totalApproximate: queryState.templates.length,
          }
        : { items: [], nextCursor: null, totalApproximate: 0 };
    return {
      data,
      error: undefined,
      status: "success" as const,
      reload: vi.fn(),
    };
  },
}));

vi.mock("../lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/api")>();
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
  queryState.session = {
    id: "session-1",
    sessionName: "8月 HOP",
    templateId: "template-1",
    templateVersionId: "version-1",
    templateTitle: "4年理科 HOP",
    testDate: "2026-08-01",
    classLabel: "A組",
    course: "本科",
    priority: "expedite",
    state: "closed",
  };
  queryState.templates = [
    {
      id: "template-1",
      title: "4年理科 HOP",
      subject: "理科",
      gradeLabel: "4年",
      category: "HOP",
      course: "本科",
      lifecycleState: "active",
      activeVersionId: "version-1",
      activeVersionNumber: 1,
    },
  ];
  apiState.post.mockResolvedValue({
    id: "session-created",
    templateId: "template-1",
    templateVersionId: "version-1",
    testDate: "2026-08-12",
    priority: "expedite",
    state: "open",
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("SessionsPage list controls", () => {
  it("loads every active-template cursor page for reception selection", async () => {
    apiState.get
      .mockResolvedValueOnce({
        items: [queryState.templates[0]],
        nextCursor: "next-page",
        totalApproximate: 2,
      })
      .mockResolvedValueOnce({
        items: [
          {
            ...queryState.templates[0],
            id: "template-2",
            title: "6年理科 STEP",
          },
        ],
        nextCursor: null,
        totalApproximate: 2,
      });

    const result = await loadActiveTemplates(new AbortController().signal);

    expect(result.items.map((item) => item.id)).toEqual([
      "template-1",
      "template-2",
    ]);
    expect(apiState.get).toHaveBeenNthCalledWith(
      2,
      "/templates",
      expect.objectContaining({ cursor: "next-page", pageSize: 200 }),
      expect.any(AbortSignal),
    );
  });

  it("recovers all session filters from the URL and keeps them when sorting", async () => {
    window.history.replaceState(
      null,
      "",
      "/sessions?state=closed&from=2026-08-01&to=2026-08-31&templateId=template-1&class=A%E7%B5%84&course=%E6%9C%AC%E7%A7%91&sort=name",
    );
    renderPage();

    expect(screen.getByLabelText("実施状態")).toHaveValue("closed");
    expect(screen.getByLabelText("ひな形")).toHaveValue("template-1");
    expect(screen.getByLabelText("クラス")).toHaveValue("A組");
    expect(screen.getByLabelText("コース")).toHaveValue("本科");
    expect(screen.getByLabelText("開始日")).toHaveValue("2026-08-01");
    expect(screen.getByLabelText("終了日")).toHaveValue("2026-08-31");
    expect(screen.getByLabelText("並び順")).toHaveValue("name");

    fireEvent.change(screen.getByLabelText("並び方向"), {
      target: { value: "desc" },
    });
    await waitFor(() => {
      expect(window.location.search).toContain("sort=-name");
      expect(window.location.search).toContain("class=A%E7%B5%84");
    });
  });

  it("starts reception with canonical template metadata and no duplicate name or course fields", async () => {
    window.history.replaceState(null, "", "/sessions");
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "答案受付を開始" }));
    const dialog = screen.getByRole("dialog", { name: "答案受付を開始" });
    fireEvent.change(within(dialog).getByLabelText(/^テストひな形/), {
      target: { value: "template-1" },
    });

    expect(within(dialog).getByText("4年理科 HOP")).toBeVisible();
    expect(within(dialog).getByText("理科")).toBeVisible();
    expect(within(dialog).getByText("4年")).toBeVisible();
    expect(within(dialog).getByText("本科")).toBeVisible();
    expect(within(dialog).queryByLabelText("実施名")).not.toBeInTheDocument();
    expect(within(dialog).queryByLabelText("コース")).not.toBeInTheDocument();

    fireEvent.change(within(dialog).getByLabelText(/^実施日/), {
      target: { value: "2026-08-12" },
    });
    await waitFor(() =>
      expect(within(dialog).getByRole("button", { name: "閉じる" })).toHaveFocus(),
    );
    const classInput = within(dialog).getByLabelText("クラス");
    classInput.focus();
    fireEvent.change(classInput, {
      target: { value: "4年A組" },
    });
    await waitFor(() => expect(classInput).toHaveFocus());
    fireEvent.click(
      within(dialog).getByRole("button", { name: "答案受付を開始" }),
    );

    await waitFor(() =>
      expect(apiState.post).toHaveBeenCalledWith(
        "/test-sessions",
        {
          templateVersionId: "version-1",
          testDate: "2026-08-12",
          classLabel: "4年A組",
          openImmediately: true,
        },
        expect.objectContaining({ idempotencyKey: expect.any(String) }),
      ),
    );
    expect(window.location.pathname).toBe("/sessions/session-created");
    expect(withinPayload(apiState.post.mock.calls[0]?.[1])).not.toHaveProperty(
      "sessionName",
    );
    expect(withinPayload(apiState.post.mock.calls[0]?.[1])).not.toHaveProperty(
      "course",
    );
    expect(withinPayload(apiState.post.mock.calls[0]?.[1])).not.toHaveProperty(
      "priority",
    );
    expect(dialog).not.toBeInTheDocument();
  });

  it("reuses the idempotency key when an ambiguous network failure is retried", async () => {
    apiState.post
      .mockRejectedValueOnce(new TypeError("Failed to fetch"))
      .mockResolvedValueOnce({
        id: "session-after-retry",
        templateId: "template-1",
        templateVersionId: "version-1",
        testDate: "2026-08-12",
        priority: "expedite",
        state: "open",
      });
    window.history.replaceState(null, "", "/sessions");
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "答案受付を開始" }));
    const dialog = screen.getByRole("dialog", { name: "答案受付を開始" });
    fireEvent.change(within(dialog).getByLabelText(/^テストひな形/), {
      target: { value: "template-1" },
    });
    fireEvent.change(within(dialog).getByLabelText(/^実施日/), {
      target: { value: "2026-08-12" },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "答案受付を開始" }),
    );

    expect(
      await within(dialog).findByText("答案受付を開始できませんでした。"),
    ).toBeVisible();
    const firstKey = (apiState.post.mock.calls[0]?.[2] as {
      idempotencyKey?: string;
    })?.idempotencyKey;
    fireEvent.click(
      within(dialog).getByRole("button", { name: "答案受付を開始" }),
    );

    await waitFor(() => expect(apiState.post).toHaveBeenCalledTimes(2));
    const secondKey = (apiState.post.mock.calls[1]?.[2] as {
      idempotencyKey?: string;
    })?.idempotencyKey;
    expect(firstKey).toBeTruthy();
    expect(secondKey).toBe(firstKey);
    expect(window.location.pathname).toBe("/sessions/session-after-retry");
  });

  it("keeps the dialog open and disables cancellation while reception is starting", async () => {
    let resolveRequest: ((value: TestSessionSummary) => void) | undefined;
    apiState.post.mockImplementationOnce(
      () =>
        new Promise<TestSessionSummary>((resolve) => {
          resolveRequest = resolve;
        }),
    );
    window.history.replaceState(null, "", "/sessions");
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "答案受付を開始" }));
    const dialog = screen.getByRole("dialog", { name: "答案受付を開始" });
    fireEvent.change(within(dialog).getByLabelText(/^テストひな形/), {
      target: { value: "template-1" },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "答案受付を開始" }),
    );

    const cancel = within(dialog).getByRole("button", { name: "キャンセル" });
    await waitFor(() => expect(cancel).toBeDisabled());
    fireEvent.click(within(dialog).getByRole("button", { name: "閉じる" }));
    expect(dialog).toBeVisible();

    resolveRequest?.({
      id: "session-in-flight",
      templateId: "template-1",
      templateVersionId: "version-1",
      testDate: "2026-08-11",
      priority: "expedite",
      state: "open",
    });
    await waitFor(() =>
      expect(window.location.pathname).toBe("/sessions/session-in-flight"),
    );
  });

  it("keeps keyboard focus inside the reception dialog", async () => {
    window.history.replaceState(null, "", "/sessions");
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "答案受付を開始" }));
    const dialog = screen.getByRole("dialog", { name: "答案受付を開始" });
    fireEvent.change(within(dialog).getByLabelText(/^テストひな形/), {
      target: { value: "template-1" },
    });
    const close = within(dialog).getByRole("button", { name: "閉じる" });
    const submit = within(dialog).getByRole("button", {
      name: "答案受付を開始",
    });
    submit.focus();
    fireEvent.keyDown(document, { key: "Tab" });
    expect(close).toHaveFocus();

    fireEvent.keyDown(document, { key: "Tab", shiftKey: true });
    expect(submit).toHaveFocus();
  });
});

function withinPayload(value: unknown) {
  return value as Record<string, unknown>;
}

function renderPage() {
  return render(
    <BrowserRouter>
      <SessionsPage />
    </BrowserRouter>,
  );
}

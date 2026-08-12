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
import { AdminPage } from "./AdminPage";

const queryState = vi.hoisted(() => ({
  connections: [] as Record<string, unknown>[],
  profiles: [] as Record<string, unknown>[],
  connectionsReload: vi.fn(),
  profilesReload: vi.fn(),
}));

const apiState = vi.hoisted(() => ({
  post: vi.fn(),
  put: vi.fn(),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: (key: string) => {
    if (key === "admin-ai-connections") {
      return {
        data: {
          items: queryState.connections,
          nextCursor: null,
          totalApproximate: queryState.connections.length,
        },
        error: undefined,
        status: "success" as const,
        reload: queryState.connectionsReload,
      };
    }
    if (key === "admin-ai-profiles") {
      return {
        data: {
          items: queryState.profiles,
          nextCursor: null,
          totalApproximate: queryState.profiles.length,
        },
        error: undefined,
        status: "success" as const,
        reload: queryState.profilesReload,
      };
    }
    return {
      data: undefined,
      error: undefined,
      status: "loading" as const,
      reload: vi.fn(),
    };
  },
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>(
    "../lib/api",
  );
  return {
    ...actual,
    api: {
      ...actual.api,
      post: apiState.post,
      put: apiState.put,
    },
  };
});

beforeEach(() => {
  window.history.replaceState(null, "", "/admin?tab=ai");
  queryState.connections = [];
  queryState.profiles = [];
  apiState.post.mockResolvedValue(undefined);
  apiState.put.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("AdminPage AI configuration", () => {
  it("tests, saves, and enables every AI feature in one action", async () => {
    let finishSave: (() => void) | undefined;
    apiState.post.mockReturnValue(
      new Promise<void>((resolve) => {
        finishSave = resolve;
      }),
    );
    renderPage();

    const geminiCard = screen
      .getByRole("heading", { name: "Gemini接続" })
      .closest(".card");
    expect(geminiCard).not.toBeNull();
    fireEvent.click(
      within(geminiCard as HTMLElement).getByRole("button", {
        name: "接続を追加",
      }),
    );

    expect(
      screen.getByText(
        "入力したキーをすぐに接続確認し、成功した場合だけ暗号化して保存します。",
      ),
    ).toBeVisible();
    expect(screen.getByLabelText("応答待ち時間（秒）")).not.toBeVisible();
    fireEvent.click(screen.getByText("詳細設定"));
    expect(screen.getByLabelText("応答待ち時間（秒）")).toBeVisible();

    fireEvent.change(screen.getByLabelText(/Gemini APIキー/), {
      target: { value: "AIza-test-gemini-key-1234567890" },
    });
    fireEvent.click(
      screen.getByRole("button", { name: "接続を確認して有効化" }),
    );

    expect(
      await screen.findByRole("button", { name: "確認中…" }),
    ).toBeDisabled();
    expect(apiState.post).toHaveBeenCalledWith(
      "/admin/ai-connections",
      expect.objectContaining({
        apiKey: "AIza-test-gemini-key-1234567890",
        provider: "geminiDirect",
        modelId: "gemini-3.5-flash-lite",
        timeoutSeconds: 75,
        concurrencyLimit: 2,
        testAndEnable: true,
      }),
      expect.objectContaining({ idempotencyKey: expect.any(String) }),
    );

    finishSave?.();
    expect(
      await screen.findByText(
        "Geminiの接続を確認して保存しました。全てのAI機能を利用できます。",
      ),
    ).toBeVisible();
    expect(queryState.connectionsReload).toHaveBeenCalledOnce();
    expect(queryState.profilesReload).toHaveBeenCalledOnce();
    expect(
      screen.queryByRole("dialog", { name: "AI接続を追加" }),
    ).not.toBeInTheDocument();
  });

  it("shows four read-only feature statuses without pilot controls", () => {
    queryState.connections = [configuredGeminiConnection()];
    queryState.profiles = [
      profile("templateExtraction", true),
      profile("nameTranscription", true),
      profile("initialGrading", false),
      profile("adjudication", false),
    ];
    renderPage();

    expect(
      within(screen.getByRole("row", { name: /ひな形の作成/ })).getByText(
        "利用できます",
      ),
    ).toBeVisible();
    expect(
      within(screen.getByRole("row", { name: /氏名の読み取り/ })).getByText(
        "利用できます",
      ),
    ).toBeVisible();
    expect(
      within(screen.getByRole("row", { name: /答案のAI採点/ })).getByText(
        "APIキーを再設定",
      ),
    ).toBeVisible();
    expect(
      within(
        screen.getByRole("row", { name: /採点結果の再確認/ }),
      ).getByText("APIキーを再設定"),
    ).toBeVisible();
    expect(screen.queryByText(/パイロット/)).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "評価記録を承認" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "有効化" }),
    ).not.toBeInTheDocument();
  });

  it("does not advertise an active profile whose connection is blocked", () => {
    queryState.connections = [
      {
        ...configuredGeminiConnection(),
        state: "blocked",
      },
    ];
    queryState.profiles = [profile("templateExtraction", true)];
    renderPage();

    expect(
      within(screen.getByRole("row", { name: /ひな形の作成/ })).getByText(
        "APIキーを再設定",
      ),
    ).toBeVisible();
  });

  it("keeps optional OpenRouter setup on the manual recheck flow", async () => {
    renderPage();

    const openRouterCard = screen
      .getByRole("heading", { name: "OpenRouter接続" })
      .closest(".card");
    expect(openRouterCard).not.toBeNull();
    fireEvent.click(
      within(openRouterCard as HTMLElement).getByRole("button", {
        name: "接続を追加",
      }),
    );

    expect(
      screen.getByText(
        "APIキーを暗号化して保存します。保存後、「再確認」で接続を確認してください。",
      ),
    ).toBeVisible();
    fireEvent.change(screen.getByLabelText(/OpenRouterモデルID/), {
      target: { value: "google/gemini-3.1-flash-lite" },
    });
    fireEvent.change(screen.getByLabelText(/OpenRouter APIキー/), {
      target: { value: "sk-or-test-openrouter-key-1234567890" },
    });
    fireEvent.click(screen.getByRole("button", { name: "暗号化して保存" }));

    await waitFor(() => {
      expect(apiState.post).toHaveBeenCalledWith(
        "/admin/ai-connections",
        expect.objectContaining({
          apiKey: "sk-or-test-openrouter-key-1234567890",
          provider: "openRouter",
          modelId: "google/gemini-3.1-flash-lite",
          testAndEnable: false,
        }),
        expect.objectContaining({ idempotencyKey: expect.any(String) }),
      );
    });
    expect(
      await screen.findByText(
        "OpenRouterのAPIキーを保存しました。「再確認」を押して接続を確認してください。",
      ),
    ).toBeVisible();
  });

  it("keeps a simple recheck action for a configured connection", async () => {
    queryState.connections = [configuredGeminiConnection()];
    apiState.post.mockResolvedValue({
      state: "passed",
      authentication: true,
      modelAvailable: true,
      imageInput: true,
      structuredOutput: true,
      usageMetadata: true,
    });
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "再確認" }));

    await waitFor(() => {
      expect(apiState.post).toHaveBeenCalledWith(
        "/admin/ai-connections/connection-gemini:test",
        {},
        expect.objectContaining({ idempotencyKey: expect.any(String) }),
      );
    });
    expect(
      await screen.findByText(
        "Geminiとの接続と必要な機能を確認しました。全てのAI機能を利用できます。",
      ),
    ).toBeVisible();
  });

  it("requires every backend capability before reporting recheck success", async () => {
    queryState.connections = [configuredGeminiConnection()];
    apiState.post.mockResolvedValue({
      state: "passed",
      authentication: true,
      modelAvailable: true,
      imageInput: true,
      structuredOutput: true,
      usageMetadata: false,
      safeErrorCode: "gemini_usage_metadata_missing",
    });
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "再確認" }));

    expect(
      await screen.findByText(/使用量情報をすべて確認できないため利用できません/),
    ).toBeVisible();
    expect(queryState.connectionsReload).toHaveBeenCalledOnce();
    expect(queryState.profilesReload).toHaveBeenCalledOnce();
  });

  it("does not claim that OpenRouter recheck enables Gemini AI profiles", async () => {
    queryState.connections = [configuredOpenRouterConnection()];
    apiState.post.mockResolvedValue({
      state: "passed",
      authentication: true,
      modelAvailable: true,
      imageInput: true,
      structuredOutput: true,
      usageMetadata: true,
    });
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "再確認" }));

    expect(
      await screen.findByText(
        "OpenRouterとの接続と必要な機能を確認しました。既定のAI機能はGemini設定を使用します。",
      ),
    ).toBeVisible();
    expect(
      screen.queryByText("全てのAI機能を利用できます。"),
    ).not.toBeInTheDocument();
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <AdminPage />
    </BrowserRouter>,
  );
}

function profile(taskType: string, active: boolean) {
  return {
    id: `profile-${taskType}`,
    taskType,
    connectionId: "connection-gemini",
    active,
    stale: false,
  };
}

function configuredOpenRouterConnection() {
  return {
    ...configuredGeminiConnection(),
    id: "connection-openrouter",
    provider: "openRouter",
    modelId: "google/gemini-3.1-flash-lite",
  };
}

function configuredGeminiConnection() {
  return {
    id: "connection-gemini",
    provider: "geminiDirect",
    configured: true,
    modelId: "gemini-3.5-flash-lite",
    state: "active",
    timeoutSeconds: 75,
    concurrencyLimit: 2,
    revision: 3,
    lastCapabilityProbe: {
      state: "passed",
      checkedAt: "2026-08-11T00:00:00Z",
      imageInput: true,
      structuredOutput: true,
    },
  };
}

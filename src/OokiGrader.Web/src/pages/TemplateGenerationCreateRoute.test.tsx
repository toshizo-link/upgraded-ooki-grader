import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter } from "../router";
import type { RuntimeCapabilities } from "../types";
import { TemplateGenerationCreateRoute } from "./TemplateGenerationCreateRoute";

const capabilityState = vi.hoisted(() => ({
  enabled: true,
}));

vi.mock("../hooks/useRuntimeCapabilities", () => ({
  useRuntimeCapabilities: () => ({
    data: makeCapabilities(capabilityState.enabled),
    error: undefined,
    status: "success" as const,
    reload: vi.fn(),
  }),
}));

vi.mock("./TemplateCreatePage", () => ({
  TemplateCreatePage: () => <div>create-template-surface</div>,
}));

beforeEach(() => {
  window.history.replaceState(null, "", "/templates/new");
  capabilityState.enabled = true;
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("TemplateGenerationCreateRoute", () => {
  it("renders the create flow when template generation is enabled", () => {
    renderRoute();

    expect(screen.getByText("create-template-surface")).toBeVisible();
  });

  it("blocks the create flow while keeping a route back to existing templates", () => {
    capabilityState.enabled = false;
    renderRoute();

    expect(
      screen.getByText("テンプレート生成は現在停止しています"),
    ).toBeVisible();
    expect(screen.queryByText("create-template-surface")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "ひな形一覧へ戻る" })).toHaveAttribute(
      "href",
      "/templates",
    );
  });
});

function renderRoute() {
  return render(
    <BrowserRouter>
      <TemplateGenerationCreateRoute />
    </BrowserRouter>,
  );
}

function makeCapabilities(enabled: boolean): RuntimeCapabilities {
  const feature = { enabled: true, ready: true };
  return {
    reports: { pdfExport: true },
    ai: {
      provider: "geminiDirect",
      modelId: "model-1",
      templateGeneration: { enabled, ready: enabled },
      nameTranscription: feature,
      semanticGrading: feature,
      geminiBatch: feature,
      openRouterEnabled: false,
    },
    safety: {
      automaticAssignment: false,
      automaticFinalization: false,
    },
  };
}

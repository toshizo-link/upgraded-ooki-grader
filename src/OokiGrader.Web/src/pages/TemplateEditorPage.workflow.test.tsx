import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../lib/api";
import { BrowserRouter, Route, Routes } from "../router";
import type {
  TemplateQuestion,
  TemplateSummary,
  TemplateValidation,
  TemplateVersionDetail,
} from "../types";
import { TemplateEditorPage } from "./TemplateEditorPage";

interface EditorFixture {
  template: TemplateSummary;
  version: TemplateVersionDetail;
  questions: TemplateQuestion[];
}

const state = vi.hoisted(() => ({
  editorData: undefined as EditorFixture | undefined,
  generationData: { state: "completed" },
  reloadEditor: vi.fn(),
  reloadGeneration: vi.fn(),
  get: vi.fn(),
  post: vi.fn(),
}));

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: (key: string) =>
    key.startsWith("template-editor:")
      ? {
          data: state.editorData,
          error: undefined,
          status: state.editorData ? "success" : "loading",
          reload: state.reloadEditor,
        }
      : {
          data: state.generationData,
          error: undefined,
          status: "success",
          reload: state.reloadGeneration,
        },
}));

vi.mock("../lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/api")>();
  return {
    ...actual,
    api: {
      ...actual.api,
      get: state.get,
      post: state.post,
    },
  };
});

beforeEach(() => {
  window.history.replaceState(
    null,
    "",
    "/templates/template-1/versions/version-1",
  );
  state.editorData = makeEditorFixture([
    makeQuestion({ id: "question-1", displayLabel: "問1", order: 1 }),
    makeQuestion({
      id: "question-2",
      displayLabel: "問2",
      order: 2,
      questionText: "",
    }),
  ]);
  state.get.mockResolvedValue(state.editorData.version);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("TemplateEditorPage confirmation and publish recovery", () => {
  it("uses the explicit all mode and reports every skipped structural blocker", async () => {
    const first = state.editorData!.questions[0]!;
    const second = state.editorData!.questions[1]!;
    state.post.mockImplementation(async (path: string) => {
      if (!path.endsWith("/questions:verifyProposals")) {
        throw new Error(`Unexpected POST ${path}`);
      }
      return {
        revision: 8,
        verifiedQuestionCount: 1,
        verifiedAnswerCount: 1,
        skippedQuestionCount: 1,
        issues: [
          {
            code: "question.text_required",
            message: "問2の問題文を入力してください。",
            questionId: "question-2",
            blocking: true,
          },
        ],
        questions: [
          {
            ...first,
            teacherVerified: true,
            proposalState: "accepted",
            warnings: [],
            acceptedAnswers: first.acceptedAnswers.map((answer) => ({
              ...answer,
              teacherVerified: true,
            })),
          },
          second,
        ],
      };
    });
    renderPage();

    fireEvent.click(
      screen.getByRole("button", { name: "すべての問題を確認" }),
    );
    const dialog = screen.getByRole("dialog", {
      name: "すべての問題を確認済みにしますか？",
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "すべての問題を確認" }),
    );

    await waitFor(() =>
      expect(state.post).toHaveBeenCalledWith(
        expect.stringContaining("questions:verifyProposals"),
        expect.objectContaining({ selectionMode: "all" }),
        expect.any(Object),
      ),
    );
    expect(
      await screen.findByText("1問を確認済み。1問は確認できませんでした"),
    ).toBeVisible();
    expect(screen.getByText("問2の問題文を入力してください。")).toBeVisible();
    expect(screen.getByRole("heading", { name: "問2" })).toBeVisible();
  });

  it("shows all start blockers and keeps template-global issues non-clickable", async () => {
    const verifiedQuestions = state.editorData!.questions.map((question) => ({
      ...question,
      questionText: question.questionText || "説明しなさい。",
      teacherVerified: true,
      proposalState: "accepted" as const,
      warnings: [],
      acceptedAnswers: question.acceptedAnswers.map((answer) => ({
        ...answer,
        teacherVerified: true,
      })),
    }));
    state.editorData = makeEditorFixture(verifiedQuestions);
    const validReport: TemplateValidation = {
      valid: true,
      pageCount: 1,
      questionCount: 2,
      totalPointsMilli: 2000,
      kanjiRequiredCount: 2,
      alwaysReviewCount: 0,
      issues: [],
    };
    state.post.mockImplementation(async (path: string) => {
      if (path.endsWith(":validate")) return validReport;
      if (path.endsWith(":publish")) {
        throw new ApiError(422, {
          code: "TEMPLATE_PUBLISH_BLOCKED",
          title: "受付開始前の確認が必要です",
          errors: [
            {
              field: "questions[0].rubricRules",
              code: "rubric.required",
              message: "問1の採点基準を確認してください。",
            },
            {
              code: "template.source_required",
              message: "問題用紙を確認してください。",
            },
          ],
        });
      }
      throw new Error(`Unexpected POST ${path}`);
    });
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "受付を開始" }));
    const dialog = await screen.findByRole("dialog", {
      name: "答案受付を開始",
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "受付を開始" }),
    );

    expect(
      await screen.findByRole("button", {
        name: "問1の採点基準を確認してください。",
      }),
    ).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "問題用紙を確認してください。" }),
    ).not.toBeInTheDocument();
    expect(screen.getByText("問題用紙を確認してください。")).toBeVisible();
  });

  it("publishes a draft and starts reception atomically with only date and class", async () => {
    const verifiedQuestions = state.editorData!.questions.map((question) => ({
      ...question,
      questionText: question.questionText || "説明しなさい。",
      teacherVerified: true,
      proposalState: "accepted" as const,
      warnings: [],
      acceptedAnswers: question.acceptedAnswers.map((answer) => ({
        ...answer,
        teacherVerified: true,
      })),
    }));
    state.editorData = makeEditorFixture(verifiedQuestions);
    const validReport: TemplateValidation = {
      valid: true,
      pageCount: 2,
      questionCount: 2,
      totalPointsMilli: 2000,
      kanjiRequiredCount: 2,
      alwaysReviewCount: 0,
      issues: [],
    };
    state.post.mockImplementation(async (path: string) => {
      if (path.endsWith(":validate")) return validReport;
      if (path.endsWith(":publish")) {
        return {
          ...state.editorData!.version,
          testSession: {
            id: "session-created",
            templateId: "template-1",
            templateVersionId: "version-1",
            testDate: "2026-08-12",
            classLabel: "6年A組",
            priority: "expedite",
            state: "open",
          },
        };
      }
      throw new Error(`Unexpected POST ${path}`);
    });
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "受付を開始" }));
    const dialog = await screen.findByRole("dialog", {
      name: "答案受付を開始",
    });
    expect(within(dialog).getByText("国語テスト")).toBeVisible();
    expect(within(dialog).getByText("国語")).toBeVisible();
    expect(within(dialog).getByText("6年")).toBeVisible();
    expect(within(dialog).queryByLabelText("実施名")).not.toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText(/^実施日/), {
      target: { value: "2026-08-12" },
    });
    fireEvent.change(within(dialog).getByLabelText("対象クラス"), {
      target: { value: "6年A組" },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "受付を開始" }),
    );

    await waitFor(() =>
      expect(state.post).toHaveBeenCalledWith(
        "/templates/template-1/versions/version-1:publish",
        {
          revision: 7,
          testDate: "2026-08-12",
          classLabel: "6年A組",
        },
        expect.objectContaining({
          etag: '"rev-7"',
          idempotencyKey: expect.any(String),
        }),
      ),
    );
    expect(await screen.findByText("作成された答案受付")).toBeVisible();
    expect(window.location.pathname).toBe("/sessions/session-created");
  });

  it("replays the exact draft publish request after an ambiguous network failure", async () => {
    const verifiedQuestions = state.editorData!.questions.map((question) => ({
      ...question,
      questionText: question.questionText || "説明しなさい。",
      teacherVerified: true,
      proposalState: "accepted" as const,
      warnings: [],
      acceptedAnswers: question.acceptedAnswers.map((answer) => ({
        ...answer,
        teacherVerified: true,
      })),
    }));
    state.editorData = makeEditorFixture(verifiedQuestions);
    const validReport: TemplateValidation = {
      valid: true,
      pageCount: 2,
      questionCount: 2,
      totalPointsMilli: 2000,
      kanjiRequiredCount: 2,
      alwaysReviewCount: 0,
      issues: [],
    };
    let publishAttempt = 0;
    state.post.mockImplementation(async (path: string) => {
      if (path.endsWith(":validate")) return validReport;
      if (path.endsWith(":publish")) {
        publishAttempt += 1;
        if (publishAttempt === 1) {
          throw new TypeError("Failed to fetch");
        }
        return {
          ...state.editorData!.version,
          state: "published",
          revision: 8,
          testSession: {
            id: "session-recovered",
            templateId: "template-1",
            templateVersionId: "version-1",
            testDate: "2026-08-12",
            priority: "expedite",
            state: "open",
          },
        };
      }
      throw new Error(`Unexpected POST ${path}`);
    });
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "受付を開始" }));
    let dialog = await screen.findByRole("dialog", {
      name: "答案受付を開始",
    });
    fireEvent.change(within(dialog).getByLabelText(/^実施日/), {
      target: { value: "2026-08-12" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "受付を開始" }));
    expect(await screen.findByText("Failed to fetch")).toBeVisible();

    state.get.mockResolvedValue({
      ...state.editorData!.version,
      state: "published",
      revision: 8,
    });
    fireEvent.click(screen.getByRole("button", { name: "受付を開始" }));
    dialog = await screen.findByRole("dialog", { name: "答案受付を開始" });
    fireEvent.click(within(dialog).getByRole("button", { name: "受付を開始" }));

    expect(await screen.findByText("作成された答案受付")).toBeVisible();
    const publishCalls = state.post.mock.calls.filter(([path]) =>
      String(path).endsWith(":publish"),
    );
    expect(publishCalls).toHaveLength(2);
    expect(publishCalls[1]?.[1]).toEqual(publishCalls[0]?.[1]);
    expect(publishCalls[1]?.[2]).toEqual(publishCalls[0]?.[2]);
    expect(state.get).toHaveBeenCalledTimes(1);
    expect(window.location.pathname).toBe("/sessions/session-recovered");
  });

  it("starts a new reception from an already-published template without republishing", async () => {
    state.editorData = {
      ...makeEditorFixture(state.editorData!.questions),
      template: {
        ...state.editorData!.template,
        lifecycleState: "active",
        activeVersionId: "version-1",
      },
      version: {
        ...state.editorData!.version,
        state: "published",
      },
    };
    state.post.mockImplementation(async (path: string, body: unknown) => {
      if (path === "/test-sessions") {
        return {
          id: "session-from-published",
          templateId: "template-1",
          templateVersionId: "version-1",
          testDate: (body as { testDate: string }).testDate,
          priority: "expedite",
          state: "open",
        };
      }
      throw new Error(`Unexpected POST ${path}`);
    });
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "受付を開始" }));
    const dialog = screen.getByRole("dialog", { name: "答案受付を開始" });
    fireEvent.change(within(dialog).getByLabelText(/^実施日/), {
      target: { value: "2026-08-13" },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "受付を開始" }),
    );

    await waitFor(() =>
      expect(state.post).toHaveBeenCalledWith(
        "/test-sessions",
        {
          templateVersionId: "version-1",
          testDate: "2026-08-13",
          classLabel: undefined,
          openImmediately: true,
        },
        expect.objectContaining({ idempotencyKey: expect.any(String) }),
      ),
    );
    expect(
      state.post.mock.calls.some(([path]) => String(path).endsWith(":publish")),
    ).toBe(false);
    expect(window.location.pathname).toBe("/sessions/session-from-published");
  });
});

function renderPage() {
  return render(
    <BrowserRouter>
      <Routes>
        <Route
          path="/templates/:templateId/versions/:versionId"
          element={<TemplateEditorPage />}
        />
        <Route
          path="/sessions/:sessionId"
          element={<div>作成された答案受付</div>}
        />
      </Routes>
    </BrowserRouter>,
  );
}

function makeEditorFixture(questions: TemplateQuestion[]): EditorFixture {
  return {
    template: {
      id: "template-1",
      title: "国語テスト",
      subject: "国語",
      gradeLabel: "6年",
      category: "クラス分け",
      course: "本科",
      lifecycleState: "draft",
    },
    version: {
      id: "version-1",
      templateId: "template-1",
      versionNumber: 1,
      state: "draft",
      defaultPointsMilli: 1000,
      revision: 7,
      sources: [
        {
          id: "source-1",
          sourceRole: "blankTest",
          displayName: "国語テスト.pdf",
          mimeType: "application/pdf",
        },
      ],
    },
    questions,
  };
}

function makeQuestion(
  changes: Partial<TemplateQuestion> = {},
): TemplateQuestion {
  return {
    id: "question-1",
    displayLabel: "問1",
    order: 1,
    questionText: "答えなさい。",
    questionType: "exact_short_text",
    gradingMode: "ai_rubric",
    maxPointsMilli: 1000,
    pointIncrementMilli: 1000,
    allowNonKanji: false,
    requiresCompleteAnswer: false,
    answerOrderInsensitive: false,
    acceptedAnswers: [
      {
        id: "answer-1",
        text: "東京",
        variantType: "canonical",
        provenance: "ai_proposed",
        teacherVerified: false,
      },
    ],
    canonicalAnswer: "東京",
    rubric: "模範解答と一致する内容を正解とする。",
    requiresReviewAlways: false,
    proposalState: "proposed",
    teacherVerified: false,
    warnings: [
      "先生による確認が必要です。",
      "未確認の解答候補があります。",
    ],
    revision: 1,
    ...changes,
  };
}

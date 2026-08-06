import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { BrowserRouter } from "../router";
import { api } from "../lib/api";
import type { UploadFinalizeResponse } from "../types";
import {
  inferSourceRole,
  inferTemplateMetadata,
  metadataForCreation,
  sourceRoleHelp,
  TemplateCreatePage,
} from "./TemplateCreatePage";

const { uploadFileMock } = vi.hoisted(() => ({
  uploadFileMock: vi.fn<
    (...args: unknown[]) => Promise<UploadFinalizeResponse>
  >(() => new Promise<UploadFinalizeResponse>(() => undefined)),
}));

vi.mock("../lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/api")>();
  return { ...actual, uploadFile: uploadFileMock };
});

vi.mock("../hooks/useApiQuery", () => ({
  useApiQuery: () => ({
    data: undefined,
    error: undefined,
    status: "loading",
    reload: vi.fn(),
  }),
}));

afterEach(cleanup);

describe("TemplateCreatePage", () => {
  it("accepts an integer default score without a step mismatch", () => {
    render(
      <BrowserRouter>
        <TemplateCreatePage />
      </BrowserRouter>,
    );

    const defaultPoints = screen.getByLabelText(
      "問題ごとの初期配点",
    ) as HTMLInputElement;

    expect(defaultPoints).toHaveAttribute("step", "0.5");

    fireEvent.change(defaultPoints, { target: { value: "10" } });

    expect(defaultPoints).toHaveValue(10);
    expect(defaultPoints.validity.stepMismatch).toBe(false);
    expect(defaultPoints).toBeValid();
  });

  it("infers Japanese exam metadata and a separate answer key", () => {
    const question = new File(
      ["question"],
      "中学1年_社会_地理_問題用紙.pdf",
      { type: "application/pdf" },
    );
    const answer = new File(
      ["answer"],
      "中学1年_社会_地理_模範解答.pdf",
      { type: "application/pdf" },
    );

    expect(inferTemplateMetadata([question, answer])).toEqual({
      title: "中学1年 社会 地理 問題用紙",
      subject: "社会",
    });
    expect(inferSourceRole(question, [question, answer])).toEqual({
      role: "blankTest",
      confidence: "default",
    });
    expect(inferSourceRole(answer, [question, answer])).toEqual({
      role: "separateAnswerKey",
      confidence: "strong",
    });
  });

  it("uses replaceable placeholders for generic scanner filenames", () => {
    const scan = new File(["scan"], "SCAN_001.pdf", {
      type: "application/pdf",
    });

    expect(inferTemplateMetadata([scan])).toEqual({
      title: "新しいテスト",
      subject: "自動判定中",
    });
  });

  it("never treats a plain or generically filled answer sheet as authoritative", () => {
    const blankAnswerSheet = new File(
      ["blank"],
      "中学1年_社会_解答用紙.pdf",
      { type: "application/pdf" },
    );
    const completedPaper = new File(
      ["answers"],
      "中学1年_社会_解答付き.pdf",
      { type: "application/pdf" },
    );

    expect(inferSourceRole(blankAnswerSheet, [blankAnswerSheet])).toEqual({
      role: "blankTest",
      confidence: "default",
    });
    expect(inferSourceRole(completedPaper, [completedPaper])).toEqual({
      role: "containsNonModelAnswers",
      confidence: "strong",
    });
  });

  it("classifies completed student answers as non-authoritative", () => {
    const completedStudentPaper = new File(
      ["answers"],
      "中学1年_社会_生徒答案_記入済み.pdf",
      { type: "application/pdf" },
    );
    const filledExam = new File(["answers"], "math_filled_exam.png", {
      type: "image/png",
    });

    expect(
      inferSourceRole(completedStudentPaper, [completedStudentPaper]),
    ).toEqual({
      role: "containsNonModelAnswers",
      confidence: "strong",
    });
    expect(inferSourceRole(filledExam, [filledExam])).toEqual({
      role: "containsNonModelAnswers",
      confidence: "strong",
    });
  });

  it("keeps explicitly labeled model answers authoritative", () => {
    const embeddedModelAnswer = new File(
      ["answers"],
      "中学1年_社会_模範解答_記入済み.pdf",
      { type: "application/pdf" },
    );
    const modelAnswerIncluded = new File(
      ["answers"],
      "中学1年_社会_模範解答入り.pdf",
      { type: "application/pdf" },
    );

    expect(inferSourceRole(embeddedModelAnswer, [embeddedModelAnswer])).toEqual(
      {
        role: "containsModelAnswers",
        confidence: "strong",
      },
    );
    expect(inferSourceRole(modelAnswerIncluded, [modelAnswerIncluded])).toEqual(
      {
        role: "containsModelAnswers",
        confidence: "strong",
      },
    );
  });

  it("keeps an ambiguously named filled answer non-authoritative", () => {
    const filledAnswer = new File(
      ["answers"],
      "中学1年_社会_生徒の答え_記入済み.pdf",
      { type: "application/pdf" },
    );

    expect(inferSourceRole(filledAnswer, [filledAnswer])).toEqual({
      role: "containsNonModelAnswers",
      confidence: "strong",
    });
  });

  it("prefers the clean question sheet when inferring metadata", () => {
    const completedStudentPaper = new File(
      ["answers"],
      "中学1年_社会_生徒答案_記入済み.pdf",
      { type: "application/pdf" },
    );
    const question = new File(
      ["question"],
      "中学1年_社会_地理_問題用紙.pdf",
      { type: "application/pdf" },
    );

    expect(inferTemplateMetadata([completedStudentPaper, question])).toEqual({
      title: "中学1年 社会 地理 問題用紙",
      subject: "社会",
    });
  });

  it("explains that non-model answers are ignored as answer-key authority", () => {
    expect(sourceRoleHelp("containsNonModelAnswers")).toBe(
      "記入された答えは正解として使わず、AIが印刷された問題を独自に解いて正答候補を作ります。",
    );
  });

  it("lets the teacher mark an uploaded paper as non-model answers", () => {
    const { container } = render(
      <BrowserRouter>
        <TemplateCreatePage />
      </BrowserRouter>,
    );
    const fileInput = container.querySelector(
      'input[type="file"]',
    ) as HTMLInputElement;
    const uploadedPaper = new File(["answers"], "答案.pdf", {
      type: "application/pdf",
    });

    fireEvent.change(fileInput, { target: { files: [uploadedPaper] } });

    const roleSelect = screen.getByRole("combobox", {
      name: /資料の種類/,
    });
    expect(
      screen.getByRole("option", {
        name: "記入済み答案（AIが正答を作成）",
      }),
    ).toBeInTheDocument();

    fireEvent.change(roleSelect, {
      target: { value: "containsNonModelAnswers" },
    });

    expect(roleSelect).toHaveValue("containsNonModelAnswers");
    expect(
      screen.getByText(
        "記入された答えは正解として使わず、AIが印刷された問題を独自に解いて正答候補を作ります。",
      ),
    ).toBeInTheDocument();
  });

  it("reviews source authority before reuse matching and sends it as identity", async () => {
    uploadFileMock.mockResolvedValueOnce({
      uploadId: "upload-1",
      state: "completed",
    });
    const getSpy = vi.spyOn(api, "get").mockImplementation((path) => {
      if (path === "/templates/source-match") {
        return new Promise<never>(() => undefined);
      }
      return Promise.reject(new Error(`Unexpected GET ${path}`));
    });
    const { container } = render(
      <BrowserRouter>
        <TemplateCreatePage />
      </BrowserRouter>,
    );
    const fileInput = container.querySelector(
      'input[type="file"]',
    ) as HTMLInputElement;

    fireEvent.change(fileInput, {
      target: {
        files: [
          new File(["answers"], "SCAN_001.pdf", {
            type: "application/pdf",
          }),
        ],
      },
    });

    const roleSelect = await screen.findByRole("combobox", {
      name: /資料の種類/,
    });
    fireEvent.change(roleSelect, {
      target: { value: "containsNonModelAnswers" },
    });
    const continueButton = await screen.findByRole("button", {
      name: "今すぐ続ける",
    });
    expect(getSpy).not.toHaveBeenCalledWith(
      "/templates/source-match",
      expect.anything(),
    );

    fireEvent.click(continueButton);

    await waitFor(() =>
      expect(getSpy).toHaveBeenCalledWith("/templates/source-match", {
        uploadIds: "upload-1",
        sourceRoles: "containsNonModelAnswers",
      }),
    );
  });

  it("keeps filename guesses as a fallback while AI generation runs", () => {
    const inferred = {
      title: "中1 社会 問題用紙",
      subject: "社会",
      category: "地理",
      gradeLabel: "中学1年",
      course: "",
      defaultPointsMilli: 1000,
    };

    expect(metadataForCreation(inferred, new Set(), true)).toEqual(inferred);
    expect(
      metadataForCreation(
        inferred,
        new Set(["title", "subject", "gradeLabel"]),
        true,
      ),
    ).toEqual(inferred);
    expect(metadataForCreation(inferred, new Set(), false)).toEqual(inferred);
  });
});

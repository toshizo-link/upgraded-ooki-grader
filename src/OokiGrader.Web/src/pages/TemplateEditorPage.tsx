import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
} from "react";
import { Link, useNavigate, useParams } from "../router";
import { Icon } from "../components/Icon";
import { TemplateSessionMetadata } from "../components/TemplateSessionMetadata";
import {
  Badge,
  Button,
  EmptyState,
  ErrorState,
  Field,
  IconButton,
  InlineAlert,
  LoadingState,
  Modal,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { ApiError, api, asPaged, newIdempotencyKey } from "../lib/api";
import {
  classNames,
  formatDateTime,
  formatPoints,
  toDateInput,
} from "../lib/format";
import type {
  AnswerVariant,
  PagedResponse,
  TemplateQuestion,
  TemplateSource,
  TemplateSummary,
  TestSessionSummary,
  TemplateValidation,
  TemplateVersionDetail,
} from "../types";

interface EditorData {
  template: TemplateSummary;
  version: TemplateVersionDetail;
  questions: TemplateQuestion[];
}

interface GenerationStatus {
  state: "queued" | "running" | "completed" | "failed" | string;
  completedQuestions?: number;
  estimatedQuestions?: number;
  warnings?: string[];
  detail?: string;
}

interface ProposalVerificationIssue {
  code?: string;
  message: string;
  questionId?: string;
  blocking?: boolean;
}

interface ProposalVerificationResponse {
  revision: number;
  verifiedQuestionCount: number;
  verifiedAnswerCount: number;
  skippedQuestionCount: number;
  issues: ProposalVerificationIssue[];
  questions: TemplateQuestion[];
}

interface PublishTestSessionResponse {
  id: string;
  name: string;
  sessionName: string;
  title: string;
  templateId: string;
  templateVersionId: string;
  templateTitle: string;
  templateVersionNumber: number;
  subject: string | null;
  gradeLabel: string | null;
  category: string | null;
  expectedSubmissionPageCount: number | null;
  course: string | null;
  templateCourse: string | null;
  testDate: string;
  classLabel: string | null;
  priority: "economy" | "expedite";
  state: string;
  creationSource: string;
  revision: number;
}

interface PublishAndStartReceptionResponse extends TemplateVersionDetail {
  testSession: PublishTestSessionResponse;
}

interface PendingPublishReceptionRequest {
  body: {
    revision: number;
    testDate: string;
    classLabel?: string;
  };
  etag: string;
}

type SaveState = "saved" | "dirty" | "saving" | "conflict" | "error";

interface LocalQuestionDraft {
  version: 1;
  savedAt: string;
  baseRevision?: number;
  question: TemplateQuestion;
}

const LOCAL_DRAFT_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
const ROUTINE_VERIFICATION_WARNINGS = new Set([
  "先生による確認が必要です。",
  "未確認の解答候補があります。",
]);
const ROUTINE_AI_NOTICES = new Set([
  "正答はAIによる提案です。先生が根拠資料と照合してください。",
  "模範解答の転記候補です。原資料との照合が必要です。",
]);
const BULK_CONFIRMABLE_QUESTION_TYPES = new Set([
  "multiple_choice",
  "boolean",
  "numeric",
  "exact_short_text",
]);

export type TemplateSourcePreviewKind = "pdf" | "image" | "unsupported";

export function templateSourcePreviewKind(
  source: Pick<TemplateSource, "displayName" | "mimeType">,
): TemplateSourcePreviewKind {
  const mimeType = source.mimeType?.toLowerCase();
  const fileName = source.displayName.toLowerCase();
  if (mimeType === "application/pdf" || fileName.endsWith(".pdf")) {
    return "pdf";
  }
  if (
    mimeType === "image/png" ||
    mimeType === "image/jpeg" ||
    mimeType === "image/webp" ||
    /\.(?:png|jpe?g|webp)$/u.test(fileName)
  ) {
    return "image";
  }
  return "unsupported";
}

export function templateSourceRoleLabel(
  sourceRole: TemplateSource["sourceRole"],
) {
  switch (sourceRole) {
    case "blankTest":
      return "問題用紙（未記入）";
    case "containsModelAnswers":
      return "模範解答入り";
    case "containsNonModelAnswers":
      return "記入済み答案（正解には不使用）";
    case "separateAnswerKey":
      return "別紙の模範解答";
  }
}

export function isKanjiRequired(
  question: Pick<TemplateQuestion, "allowNonKanji">,
) {
  return !question.allowNonKanji;
}

export function allowNonKanjiForKanjiRequired(kanjiRequired: boolean) {
  return !kanjiRequired;
}

export const DEFAULT_AI_RUBRIC =
  "模範解答と照合し、内容と根拠が一致する場合のみ正解とします。部分的な一致、曖昧な表現、別解の可能性がある場合は点数を確定せず、先生の確認に回します。";

export function defaultsForQuestionTypeChange(
  question: Pick<
    TemplateQuestion,
    "questionType" | "gradingMode" | "rubric" | "requiresReviewAlways"
  >,
  questionType: string,
) {
  const gradingMode = questionType === "unsupported" ? "manual" : "ai_rubric";
  const changes: Partial<TemplateQuestion> = {
    questionType,
    gradingMode,
    requiresReviewAlways: false,
  };
  if (gradingMode === "ai_rubric" && !question.rubric?.trim()) {
    changes.rubric = DEFAULT_AI_RUBRIC;
  }
  return changes;
}

export type GradingPreset =
  | "ai"
  | "exact"
  | "numeric"
  | "choice"
  | "manual"
  | "custom";

export function gradingPresetForQuestion(
  question: Pick<TemplateQuestion, "questionType" | "gradingMode">,
): GradingPreset {
  if (question.gradingMode === "ai_rubric") return "ai";
  if (
    question.questionType === "exact_short_text" &&
    question.gradingMode === "transcribe_then_rules"
  ) {
    return "exact";
  }
  if (
    question.questionType === "numeric" &&
    question.gradingMode === "transcribe_then_rules"
  ) {
    return "numeric";
  }
  if (
    question.questionType === "multiple_choice" &&
    question.gradingMode === "transcribe_then_rules"
  ) {
    return "choice";
  }
  if (
    question.questionType === "subjective" &&
    question.gradingMode === "manual"
  ) {
    return "manual";
  }
  return "custom";
}

export function changesForGradingPreset(
  question: Pick<
    TemplateQuestion,
    "questionType" | "gradingMode" | "rubric" | "requiresReviewAlways"
  >,
  preset: Exclude<GradingPreset, "custom">,
): Partial<TemplateQuestion> {
  if (preset === "ai") {
    return defaultsForQuestionTypeChange(
      question,
      question.questionType === "unsupported"
        ? "subjective"
        : question.questionType,
    );
  }
  const byPreset = {
    exact: {
      questionType: "exact_short_text",
      gradingMode: "transcribe_then_rules",
    },
    numeric: {
      questionType: "numeric",
      gradingMode: "transcribe_then_rules",
    },
    choice: {
      questionType: "multiple_choice",
      gradingMode: "transcribe_then_rules",
    },
    manual: {
      questionType: "subjective",
      gradingMode: "manual",
    },
  } as const;
  return { ...byPreset[preset], requiresReviewAlways: false };
}

export function newQuestionPayload(
  questionCount: number,
  defaultPointsMilli = 1000,
) {
  const order = questionCount + 1;
  return {
    displayLabel: `問${order}`,
    order,
    questionText: "",
    questionType: "exact_short_text",
    gradingMode: "ai_rubric",
    maxPointsMilli: defaultPointsMilli,
    pointIncrementMilli: defaultPointIncrementMilli(defaultPointsMilli),
    allowNonKanji: false,
    requiresCompleteAnswer: false,
    answerOrderInsensitive: false,
    acceptedAnswers: [],
    rubric: DEFAULT_AI_RUBRIC,
    requiresReviewAlways: false,
  };
}

export function defaultPointIncrementMilli(maxPointsMilli: number) {
  let left = Math.max(1, Math.round(maxPointsMilli));
  let right = 1000;
  while (right !== 0) {
    [left, right] = [right, left % right];
  }
  return left;
}

function localDraftKey(
  templateId: string,
  versionId: string,
  questionId: string,
) {
  return `ooki:template-draft:v1:${templateId}:${versionId}:${questionId}`;
}

function readLocalDraft(
  templateId: string,
  versionId: string,
  question: TemplateQuestion,
): LocalQuestionDraft | undefined {
  try {
    const key = localDraftKey(templateId, versionId, question.id);
    const raw = localStorage.getItem(key);
    if (!raw) return undefined;
    const value = JSON.parse(raw) as LocalQuestionDraft;
    const savedAt = Date.parse(value.savedAt);
    if (
      value.version !== 1 ||
      value.question?.id !== question.id ||
      !Number.isFinite(savedAt) ||
      Date.now() - savedAt > LOCAL_DRAFT_MAX_AGE_MS
    ) {
      localStorage.removeItem(key);
      return undefined;
    }
    return {
      ...value,
      question: {
        ...value.question,
        pointIncrementMilli: value.question.pointIncrementMilli ?? 1000,
        requiresCompleteAnswer:
          value.question.requiresCompleteAnswer ?? false,
        answerOrderInsensitive:
          value.question.answerOrderInsensitive ?? false,
      },
    };
  } catch {
    return undefined;
  }
}

function questionCanonical(question: TemplateQuestion) {
  return (
    question.canonicalAnswer ||
    question.acceptedAnswers.find((answer) => answer.variantType === "canonical")
      ?.text ||
    ""
  );
}

function isCanonicalVariant(answer: AnswerVariant) {
  return answer.variantType === "canonical";
}

function isAcceptedVariant(answer: AnswerVariant) {
  return (
    !answer.variantType ||
    answer.variantType === "accepted" ||
    answer.variantType === "equivalent"
  );
}

function isPhoneticExceptionVariant(answer: AnswerVariant) {
  return (
    answer.variantType === "explicitException" ||
    answer.variantType === "phonetic_exception"
  );
}

export function questionAcceptedVariants(question: TemplateQuestion) {
  return question.acceptedAnswers
    .filter(isAcceptedVariant)
    .map((answer) => answer.text)
    .join("\n");
}

export function questionPhoneticExceptions(question: TemplateQuestion) {
  return question.acceptedAnswers
    .filter(isPhoneticExceptionVariant)
    .map((answer) => answer.text)
    .join("\n");
}

function answerLines(value: string) {
  return value
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

function editedVariants(
  value: string,
  existing: AnswerVariant[],
  newVariantType: "accepted" | "explicitException",
) {
  const unused = [...existing];
  return answerLines(value).map((text): AnswerVariant => {
    const existingIndex = unused.findIndex((answer) => answer.text === text);
    if (existingIndex < 0) return { text, variantType: newVariantType };
    const [matched] = unused.splice(existingIndex, 1);
    return { ...matched!, text };
  });
}

export function answersForQuestionEdit(
  question: TemplateQuestion,
  canonical: string,
  acceptedVariantText: string,
  phoneticExceptionText: string,
): AnswerVariant[] {
  const canonicalAnswer = question.acceptedAnswers.find(isCanonicalVariant);
  const acceptedAnswers = question.acceptedAnswers.filter(isAcceptedVariant);
  const phoneticExceptions = question.acceptedAnswers.filter(
    isPhoneticExceptionVariant,
  );
  const otherTypedAnswers = question.acceptedAnswers.filter(
    (answer) =>
      !isCanonicalVariant(answer) &&
      !isAcceptedVariant(answer) &&
      !isPhoneticExceptionVariant(answer),
  );

  return [
    ...(canonical.trim()
      ? [
          canonicalAnswer?.text === canonical
            ? { ...canonicalAnswer, text: canonical }
            : { text: canonical, variantType: "canonical" as const },
        ]
      : []),
    ...editedVariants(acceptedVariantText, acceptedAnswers, "accepted"),
    ...editedVariants(
      phoneticExceptionText,
      phoneticExceptions,
      "explicitException",
    ),
    ...otherTypedAnswers,
  ];
}

export function needsProposalVerification(question: TemplateQuestion) {
  return (
    question.teacherVerified === false ||
    question.acceptedAnswers.some((answer) => answer.teacherVerified === false)
  );
}

function substantiveWarnings(question: TemplateQuestion) {
  return (question.warnings || []).filter(
    (warning) => !ROUTINE_VERIFICATION_WARNINGS.has(warning),
  );
}

function hasBlockingTeacherNote(question: TemplateQuestion) {
  if (!question.teacherNote?.trim()) return false;
  return question.teacherNote
    .split("\n")
    .map((line) => line.trim().replace(/^\[AI確認\]\s*/u, ""))
    .filter(Boolean)
    .some((notice) => !ROUTINE_AI_NOTICES.has(notice));
}

function customTeacherNote(question: TemplateQuestion) {
  return (question.teacherNote || "")
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => {
      const notice = line.replace(/^\[AI確認\]\s*/u, "");
      return line && !ROUTINE_AI_NOTICES.has(notice);
    })
    .join("\n");
}

function mergeCustomTeacherNote(question: TemplateQuestion, value: string) {
  const routineNotes = (question.teacherNote || "")
    .split("\n")
    .map((line) => line.trim())
    .filter((line) =>
      ROUTINE_AI_NOTICES.has(line.replace(/^\[AI確認\]\s*/u, "")),
    );
  return [...routineNotes, value.trim()].filter(Boolean).join("\n");
}

export function needsIndividualReview(question: TemplateQuestion) {
  if (!needsProposalVerification(question)) return false;
  if (
    substantiveWarnings(question).length > 0 ||
    hasBlockingTeacherNote(question)
  ) {
    return true;
  }
  if (question.requiresReviewAlways) return true;
  if (!question.displayLabel.trim() || !question.questionText.trim()) return true;
  if (question.maxPointsMilli <= 0) {
    return true;
  }
  if (!BULK_CONFIRMABLE_QUESTION_TYPES.has(question.questionType)) return true;
  return !questionCanonical(question).trim();
}

export function isTemplateEditorReadOnly(
  template: Pick<TemplateSummary, "lifecycleState">,
  version: Pick<TemplateVersionDetail, "state">,
) {
  return version.state !== "draft" || template.lifecycleState === "archived";
}

export function TemplateEditorPage() {
  const { templateId = "", versionId = "" } = useParams();
  const navigate = useNavigate();
  const editor = useApiQuery<EditorData>(
    `template-editor:${templateId}:${versionId}`,
    async (signal) => {
      const [template, version, questionValue] = await Promise.all([
        api.get<TemplateSummary>(
          `/templates/${encodeURIComponent(templateId)}`,
          undefined,
          signal,
        ),
        api.get<TemplateVersionDetail>(
          `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}`,
          undefined,
          signal,
        ),
        api.get<PagedResponse<TemplateQuestion> | TemplateQuestion[]>(
          `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/questions`,
          undefined,
          signal,
        ),
      ]);
      return {
        template,
        version,
        questions: asPaged(questionValue).items.sort(
          (a, b) => a.order - b.order,
        ),
      };
    },
    Boolean(templateId && versionId),
  );
  const generation = useApiQuery<GenerationStatus>(
    `template-generation:${templateId}:${versionId}`,
    (signal) =>
      api.get(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/generation`,
        undefined,
        signal,
      ),
    Boolean(templateId && versionId),
  );
  const [questions, setQuestions] = useState<TemplateQuestion[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [draft, setDraft] = useState<TemplateQuestion>();
  const [selectedSourceId, setSelectedSourceId] = useState("");
  const [sourcePreviewFailed, setSourcePreviewFailed] = useState(false);
  const [saveState, setSaveState] = useState<SaveState>("saved");
  const [validation, setValidation] = useState<TemplateValidation>();
  const [validating, setValidating] = useState(false);
  const [showAllQuestions, setShowAllQuestions] = useState(true);
  const [bulkVerifyOpen, setBulkVerifyOpen] = useState(false);
  const [bulkVerifying, setBulkVerifying] = useState(false);
  const [bulkVerification, setBulkVerification] =
    useState<ProposalVerificationResponse>();
  const [verifyingQuestionId, setVerifyingQuestionId] = useState("");
  const [receptionOpen, setReceptionOpen] = useState(false);
  const [startingReception, setStartingReception] = useState(false);
  const [receptionDetails, setReceptionDetails] = useState({
    testDate: toDateInput(),
    classLabel: "",
  });
  const [retryingGeneration, setRetryingGeneration] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const [recoveryDraft, setRecoveryDraft] = useState<LocalQuestionDraft>();
  const draftRef = useRef<TemplateQuestion | undefined>(undefined);
  const completedGenerationRef = useRef("");
  const receptionIdempotencyKeyRef = useRef("");
  const pendingPublishReceptionRef =
    useRef<PendingPublishReceptionRequest | undefined>(undefined);

  useEffect(() => {
    const data = editor.data;
    if (!data) return;
    setQuestions(data.questions);
    setSelectedId((current) => {
      if (
        current &&
        data.questions.some((question) => question.id === current)
      ) {
        return current;
      }
      if (!data.questions[0]) return "";
      const firstException = data.questions.find(needsIndividualReview);
      return firstException?.id || data.questions[0].id;
    });
    const sources = data.version.sources || [];
    setSelectedSourceId((current) => {
      if (sources.some((source) => source.id === current)) return current;
      return (
        sources.find((source) => source.sourceRole === "blankTest")?.id ||
        sources[0]?.id ||
        ""
      );
    });
  }, [editor.data]);

  useEffect(() => {
    setSourcePreviewFailed(false);
  }, [selectedSourceId]);

  useEffect(() => {
    const state = generation.data?.state;
    if (state === "queued" || state === "running") {
      completedGenerationRef.current = "";
      const timer = window.setTimeout(() => generation.reload(), 2500);
      return () => window.clearTimeout(timer);
    }
    if (
      state === "completed" &&
      completedGenerationRef.current !== state
    ) {
      completedGenerationRef.current = state;
      editor.reload();
    }
    return undefined;
  }, [generation.data?.state, generation.reload, editor.reload]);

  useEffect(() => {
    const question = questions.find((item) => item.id === selectedId);
    setDraft(question ? structuredClone(question) : undefined);
    draftRef.current = question ? structuredClone(question) : undefined;
    setSaveState("saved");
    setRecoveryDraft(
      question
        ? readLocalDraft(templateId, versionId, question)
        : undefined,
    );
  }, [questions, selectedId, templateId, versionId]);

  useEffect(() => {
    if (!draft || saveState !== "dirty") return;
    const localDraft: LocalQuestionDraft = {
      version: 1,
      savedAt: new Date().toISOString(),
      baseRevision: draft.revision,
      question: draft,
    };
    try {
      localStorage.setItem(
        localDraftKey(templateId, versionId, draft.id),
        JSON.stringify(localDraft),
      );
    } catch {
      // The editor still retains its in-memory draft when storage is unavailable.
    }
  }, [draft, saveState, templateId, versionId]);

  useEffect(() => {
    const warnBeforeUnload = (event: BeforeUnloadEvent) => {
      if (saveState === "saved") return;
      event.preventDefault();
    };
    window.addEventListener("beforeunload", warnBeforeUnload);
    return () => window.removeEventListener("beforeunload", warnBeforeUnload);
  }, [saveState]);

  useEffect(() => {
    draftRef.current = draft;
    if (!draft || saveState !== "dirty") return;
    const snapshot = JSON.stringify(draft);
    const timer = window.setTimeout(async () => {
      setSaveState("saving");
      try {
        const saved = await api.patch<TemplateQuestion>(
          `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/questions/${encodeURIComponent(draft.id)}`,
          questionPayload(draft),
          {
            etag: draft.revision ? `"rev-${draft.revision}"` : undefined,
          },
        );
        setQuestions((current) =>
          current.map((question) =>
            question.id === saved.id ? { ...question, ...saved } : question,
          ),
        );
        if (JSON.stringify(draftRef.current) === snapshot) {
          try {
            localStorage.removeItem(
              localDraftKey(templateId, versionId, saved.id),
            );
          } catch {
            // A stale local recovery copy is harmless and expires after seven days.
          }
          setDraft(saved);
          draftRef.current = saved;
          setSaveState("saved");
        } else {
          setSaveState("dirty");
        }
      } catch (reason) {
        setSaveState(
          reason instanceof ApiError && reason.status === 412
            ? "conflict"
            : "error",
        );
      }
    }, 900);
    return () => window.clearTimeout(timer);
  }, [draft, saveState, templateId, versionId]);

  function changeDraft(changes: Partial<TemplateQuestion>) {
    if (!draft) return;
    setDraft({ ...draft, ...changes });
    setBulkVerification(undefined);
    setSaveState("dirty");
    setValidation(undefined);
  }

  function recoverLocalDraft() {
    if (!recoveryDraft) return;
    const recovered = structuredClone(recoveryDraft.question);
    setDraft(recovered);
    draftRef.current = recovered;
    setRecoveryDraft(undefined);
    setSaveState("dirty");
  }

  function discardLocalDraft() {
    if (!recoveryDraft) return;
    try {
      localStorage.removeItem(
        localDraftKey(
          templateId,
          versionId,
          recoveryDraft.question.id,
        ),
      );
    } catch {
      // Nothing else is required when browser storage is unavailable.
    }
    setRecoveryDraft(undefined);
  }

  async function addQuestion(copyFrom?: TemplateQuestion) {
    setActionError(undefined);
    try {
      const created = await api.post<TemplateQuestion>(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/questions`,
        copyFrom
          ? {
              ...questionPayload(copyFrom),
              displayLabel: `${copyFrom.displayLabel}（コピー）`,
              order: questions.length + 1,
            }
          : newQuestionPayload(
              questions.length,
              editor.data?.version.defaultPointsMilli ?? 1000,
            ),
        { idempotencyKey: newIdempotencyKey() },
      );
      setQuestions((current) => [...current, created]);
      setSelectedId(created.id);
    } catch (reason) {
      setActionError(errorMessage(reason, "問題を追加できませんでした。"));
    }
  }

  async function deleteQuestion(question: TemplateQuestion) {
    if (
      !window.confirm(
        `${question.displayLabel}を削除しますか？設定した解答欄も削除されます。`,
      )
    ) {
      return;
    }
    try {
      await api.delete(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/questions/${encodeURIComponent(question.id)}`,
        {
          etag: question.revision
            ? `"rev-${question.revision}"`
            : undefined,
        },
      );
      const remaining = questions.filter((item) => item.id !== question.id);
      setQuestions(remaining);
      setSelectedId(remaining[0]?.id || "");
    } catch (reason) {
      setActionError(errorMessage(reason, "問題を削除できませんでした。"));
    }
  }

  async function moveQuestion(index: number, direction: -1 | 1) {
    const target = index + direction;
    if (target < 0 || target >= questions.length) return;
    const reordered = [...questions];
    const current = reordered[index];
    const swap = reordered[target];
    if (!current || !swap) return;
    reordered[index] = swap;
    reordered[target] = current;
    const normalized = reordered.map((item, order) => ({
      ...item,
      order: order + 1,
    }));
    setQuestions(normalized);
    try {
      const saved = await api.post<
        PagedResponse<TemplateQuestion> | TemplateQuestion[]
      >(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/questions:reorder`,
        { questionIds: normalized.map((item) => item.id) },
        { idempotencyKey: newIdempotencyKey() },
      );
      setQuestions(
        asPaged(saved).items.sort((a, b) => a.order - b.order),
      );
    } catch (reason) {
      setQuestions(questions);
      setActionError(errorMessage(reason, "並び順を保存できませんでした。"));
    }
  }

  async function verifyAllProposals() {
    setBulkVerifying(true);
    setActionError(undefined);
    try {
      const latest = await api.get<TemplateVersionDetail>(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}`,
      );
      if (latest.revision === undefined) {
        throw new Error("最新版の改訂番号を取得できませんでした。");
      }
      const result = await api.post<ProposalVerificationResponse>(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/questions:verifyProposals`,
        {
          selectionMode: "all",
          revision: latest.revision,
        },
        {
          idempotencyKey: newIdempotencyKey(),
          etag: `"rev-${latest.revision}"`,
        },
      );
      const updated = [...result.questions].sort((a, b) => a.order - b.order);
      setQuestions(updated);
      setBulkVerification(result);
      setBulkVerifyOpen(false);
      setValidation(undefined);
      const firstSkippedQuestionId = result.issues.find(
        (issue) => issue.blocking !== false && issue.questionId,
      )?.questionId;
      const nextQuestion =
        updated.find((question) => question.id === firstSkippedQuestionId) ||
        updated.find(needsIndividualReview) ||
        updated.find(needsProposalVerification) ||
        updated[0];
      setSelectedId(nextQuestion?.id || "");
      setShowAllQuestions(
        result.skippedQuestionCount === 0 || !updated.some(needsIndividualReview),
      );
      editor.reload();
    } catch (reason) {
      setActionError(
        errorMessage(reason, "すべての問題を確認できませんでした。"),
      );
      setBulkVerifyOpen(false);
    } finally {
      setBulkVerifying(false);
    }
  }

  async function verifyQuestion(question: TemplateQuestion) {
    setVerifyingQuestionId(question.id);
    setActionError(undefined);
    try {
      const saved = await api.patch<TemplateQuestion>(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/questions/${encodeURIComponent(question.id)}`,
        questionPayload(question, true),
        {
          etag: question.revision ? `"rev-${question.revision}"` : undefined,
        },
      );
      setQuestions((current) =>
        current.map((item) => (item.id === saved.id ? saved : item)),
      );
      const updated = questions.map((item) =>
        item.id === saved.id ? saved : item,
      );
      const nextQuestion =
        updated.find(needsIndividualReview) ||
        updated.find(needsProposalVerification) ||
        saved;
      setSelectedId(nextQuestion.id);
      if (!updated.some(needsIndividualReview)) setShowAllQuestions(true);
      if (nextQuestion.id !== saved.id) {
        setDraft(nextQuestion);
        draftRef.current = nextQuestion;
      }
      setSaveState("saved");
      setBulkVerification(undefined);
      setValidation(undefined);
    } catch (reason) {
      setActionError(errorMessage(reason, "この問題を確認済みにできませんでした。"));
    } finally {
      setVerifyingQuestionId("");
    }
  }

  function openReception() {
    if (!receptionIdempotencyKeyRef.current) {
      receptionIdempotencyKeyRef.current = newIdempotencyKey();
    }
    setReceptionOpen(true);
  }

  function closeReception() {
    receptionIdempotencyKeyRef.current = "";
    pendingPublishReceptionRef.current = undefined;
    setReceptionOpen(false);
  }

  async function prepareReception() {
    if (editor.data?.version.state === "published") {
      setActionError(undefined);
      openReception();
      return;
    }
    setValidating(true);
    setActionError(undefined);
    try {
      const report = await api.post<TemplateValidation>(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}:validate`,
        {},
      );
      setValidation(report);
      if (report.valid) openReception();
    } catch (reason) {
      setActionError(
        errorMessage(reason, "受付開始前の確認を完了できませんでした。"),
      );
    } finally {
      setValidating(false);
    }
  }

  async function retryGeneration() {
    setRetryingGeneration(true);
    setActionError(undefined);
    try {
      await api.post(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}:generateDraft`,
        {
          replaceableMetadataFields: [
            "title",
            "subject",
            "category",
            "gradeLabel",
            "course",
          ],
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      generation.reload();
      editor.reload();
    } catch (reason) {
      setActionError(
        errorMessage(reason, "AI下書きをもう一度開始できませんでした。"),
      );
    } finally {
      setRetryingGeneration(false);
    }
  }

  async function startReception() {
    setStartingReception(true);
    setActionError(undefined);
    try {
      const idempotencyKey =
        receptionIdempotencyKeyRef.current || newIdempotencyKey();
      receptionIdempotencyKeyRef.current = idempotencyKey;
      let session: TestSessionSummary;
      if (editor.data?.version.state === "published") {
        session = await api.post<TestSessionSummary>(
          "/test-sessions",
          {
            templateVersionId: versionId,
            testDate: receptionDetails.testDate,
            classLabel: receptionDetails.classLabel.trim() || undefined,
            openImmediately: true,
          },
          { idempotencyKey },
        );
      } else {
        let request = pendingPublishReceptionRef.current;
        if (!request) {
          const latest = await api.get<TemplateVersionDetail>(
            `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}`,
          );
          if (latest.revision === undefined) {
            throw new Error("最新版の改訂番号を取得できませんでした。");
          }
          request = {
            body: {
              revision: latest.revision,
              testDate: receptionDetails.testDate,
              classLabel: receptionDetails.classLabel.trim() || undefined,
            },
            etag: `"rev-${latest.revision}"`,
          };
          pendingPublishReceptionRef.current = request;
        }
        const result = await api.post<PublishAndStartReceptionResponse>(
          `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}:publish`,
          request.body,
          {
            idempotencyKey,
            etag: request.etag,
          },
        );
        if (!result.testSession?.id) {
          throw new Error(
            "テスト実施の作成結果を確認できませんでした。一覧を更新してください。",
          );
        }
        session = result.testSession;
      }
      receptionIdempotencyKeyRef.current = "";
      pendingPublishReceptionRef.current = undefined;
      setReceptionOpen(false);
      navigate(`/sessions/${encodeURIComponent(session.id)}`);
    } catch (reason) {
      if (reason instanceof ApiError) {
        receptionIdempotencyKeyRef.current = "";
        pendingPublishReceptionRef.current = undefined;
      }
      const rejectedValidation = validationFromPublishError(reason, questions);
      if (rejectedValidation) {
        setValidation(rejectedValidation);
      } else {
        setActionError(errorMessage(reason, "答案受付を開始できませんでした。"));
      }
      setReceptionOpen(false);
    } finally {
      setStartingReception(false);
    }
  }

  if (editor.status === "loading") {
    return (
      <div className="editor-loading">
        <LoadingState label="採点基準を読み込んでいます" />
      </div>
    );
  }
  if (editor.status === "error" || !editor.data) {
    return (
      <div className="page">
        <ErrorState error={editor.error} onRetry={editor.reload} />
      </div>
    );
  }

  const isArchived = editor.data.template.lifecycleState === "archived";
  const isDraftVersion = editor.data.version.state === "draft";
  const isPublishedVersion = editor.data.version.state === "published";
  const receptionLifecycleAvailable = !["archived", "retired"].includes(
    editor.data.template.lifecycleState,
  );
  const canStartReception =
    receptionLifecycleAvailable && (isDraftVersion || isPublishedVersion);
  const isReadOnly = isTemplateEditorReadOnly(
    editor.data.template,
    editor.data.version,
  );
  const sources = editor.data.version.sources || [];
  const activeSource = sources.find(
    (source) => source.id === selectedSourceId,
  );
  const sourcePreviewKind = activeSource
    ? templateSourcePreviewKind(activeSource)
    : "unsupported";
  const totalPoints = questions.reduce(
    (sum, question) => sum + question.maxPointsMilli,
    0,
  );
  const proposalQuestions = questions.filter(needsProposalVerification);
  const individualReviewQuestions = questions.filter(needsIndividualReview);
  const bulkSkippedIssues = (bulkVerification?.issues || []).filter(
    (issue) => issue.blocking !== false,
  );
  const visibleQuestions =
    showAllQuestions || isReadOnly
      ? questions
      : individualReviewQuestions;

  return (
    <div className="template-editor">
      <header className="editor-toolbar">
        <div className="editor-toolbar__identity">
          <Link to="/templates" aria-label="ひな形一覧に戻る">
            <Icon name="arrowLeft" />
          </Link>
          <div>
            <h1>{editor.data.template.title}</h1>
            <span>
              第{editor.data.version.versionNumber}版{" "}
              <StatusBadge status={editor.data.version.state} />
            </span>
          </div>
        </div>
        <div className="save-indicator" aria-live="polite">
          {saveState === "saving" ? (
            <>
              <span className="spinner" />
              保存中
            </>
          ) : saveState === "saved" ? (
            <>
              <Icon name="check" size={16} />
              保存済み
              {editor.data.version.updatedAt
                ? ` ${formatDateTime(editor.data.version.updatedAt)}`
                : ""}
            </>
          ) : saveState === "conflict" ? (
            <>
              <Icon name="alert" size={16} />
              競合を確認
            </>
          ) : saveState === "error" ? (
            <>
              <Icon name="alert" size={16} />
              保存に失敗
            </>
          ) : (
            "未保存の変更"
          )}
        </div>
        <div className="editor-toolbar__actions">
          <span className="editor-total">
            合計 <strong>{formatPoints(totalPoints)}</strong>点
          </span>
          {canStartReception ? (
            <Button
              onClick={() => void prepareReception()}
              disabled={
                validating || (isDraftVersion && saveState !== "saved")
              }
            >
              {validating ? "確認中…" : "受付を開始"}
            </Button>
          ) : null}
        </div>
      </header>

      {isArchived ? (
        <InlineAlert tone="info" title="このひな形はアーカイブされています">
          <p>
            保存済みの版と採点基準を確認できます。編集や答案受付を再開する場合は、ひな形一覧の「アーカイブ」表示から復元してください。
          </p>
        </InlineAlert>
      ) : null}

      {saveState === "conflict" ? (
        <InlineAlert
          tone="warning"
          title="別の先生がこの問題を変更しました"
          action={
            <Button variant="secondary" size="small" onClick={editor.reload}>
              最新版を読み込む
            </Button>
          }
        >
          <p>自動的に上書きせず、サーバー上の最新版を保持しています。</p>
        </InlineAlert>
      ) : null}
      {recoveryDraft && !isReadOnly ? (
        <InlineAlert
          tone="warning"
          title="この端末に未送信の下書きがあります"
          action={
            <div className="button-row">
              <Button size="small" onClick={recoverLocalDraft}>
                下書きを復元
              </Button>
              <Button
                variant="secondary"
                size="small"
                onClick={discardLocalDraft}
              >
                破棄
              </Button>
            </div>
          }
        >
          <p>
            {formatDateTime(recoveryDraft.savedAt)}
            に保存された内容です。復元後、通常の競合チェックを通して保存します。
          </p>
        </InlineAlert>
      ) : null}
      {saveState === "error" ? (
        <InlineAlert
          tone="danger"
          title="変更を保存できませんでした"
          action={
            !isReadOnly ? (
              <Button
                variant="secondary"
                size="small"
                onClick={() => setSaveState("dirty")}
              >
                再試行
              </Button>
            ) : undefined
          }
        >
          <p>入力内容はこの画面に残っています。</p>
        </InlineAlert>
      ) : null}
      {actionError ? (
        <InlineAlert tone="danger">
          <p>{actionError}</p>
        </InlineAlert>
      ) : null}
      {generation.data &&
      ["queued", "running"].includes(generation.data.state) ? (
        <InlineAlert tone="info" title="採点基準の下書きを作成しています">
          <p>
            この画面を閉じても処理は続きます。
            {generation.data.completedQuestions !== undefined
              ? ` ${generation.data.completedQuestions}問を確認済みです。`
              : ""}
          </p>
        </InlineAlert>
      ) : null}
      {generation.data?.state === "failed" ? (
        <InlineAlert
          tone="danger"
          title="AI下書き作成を完了できませんでした"
          action={
            !isReadOnly && questions.length === 0 ? (
              <Button
                size="small"
                variant="secondary"
                onClick={() => void retryGeneration()}
                disabled={retryingGeneration}
              >
                {retryingGeneration ? "再試行中…" : "AIでもう一度作成"}
              </Button>
            ) : undefined
          }
        >
          <p>
            {generation.data.detail ||
              (isReadOnly
                ? "資料は保存されています。編集を再開する場合は、ひな形を復元してください。"
                : "資料は保存されています。もう一度試しても失敗する場合は、右上の＋から問題を追加できます。")}
          </p>
        </InlineAlert>
      ) : null}
      {!isReadOnly && bulkVerification ? (
        <InlineAlert
          tone={bulkVerification.skippedQuestionCount > 0 ? "warning" : "success"}
          title={
            bulkVerification.skippedQuestionCount > 0
              ? `${bulkVerification.verifiedQuestionCount}問を確認済み。${bulkVerification.skippedQuestionCount}問は確認できませんでした`
              : `${bulkVerification.verifiedQuestionCount}問をすべて確認済みにしました`
          }
        >
          <p>
            {bulkVerification.skippedQuestionCount > 0
              ? "入力が不足している問題は確認済みにしていません。最初の問題を表示しました。内容を直してから、もう一度「すべての問題を確認」を押してください。"
              : "受付開始前の問題確認が完了しました。「受付を開始」から実施日を入力できます。"}
          </p>
          {bulkSkippedIssues.length > 0 ? (
            <ul className="proposal-verification-issues">
              {bulkSkippedIssues.map((issue, index) => (
                <li key={`${issue.code || "issue"}-${issue.questionId || index}`}>
                  {issue.questionId ? (
                    <button
                      type="button"
                      onClick={() => setSelectedId(issue.questionId!)}
                    >
                      {issue.message}
                    </button>
                  ) : (
                    issue.message
                  )}
                </li>
              ))}
            </ul>
          ) : null}
        </InlineAlert>
      ) : !isReadOnly &&
        questions.length > 0 &&
        !["queued", "running"].includes(generation.data?.state || "") &&
        proposalQuestions.length > 0 ? (
        <InlineAlert
          tone="info"
          title={`${proposalQuestions.length}問の内容を確認してください`}
          action={
            <Button
              size="small"
              onClick={() => setBulkVerifyOpen(true)}
              disabled={bulkVerifying || saveState !== "saved"}
            >
              すべての問題を確認
            </Button>
          }
        >
          <p>
            元の資料と問題文・正解・配点を見比べます。入力がそろっている問題は一度に確認済みにでき、不足がある問題だけ残ります。
          </p>
        </InlineAlert>
      ) : null}
      {validation && !validation.valid ? (
        <div className="editor-validation" role="alert">
          <div>
            <Icon name="alert" />
            <div>
              <strong>受付開始前に{validation.issues.length}件を確認してください</strong>
              <ul>
                {validation.issues.map((issue) => (
                  <li key={`${issue.code}-${issue.questionId || ""}`}>
                    {issue.questionId ? (
                      <button
                        type="button"
                        onClick={() => setSelectedId(issue.questionId!)}
                      >
                        {issue.message}
                      </button>
                    ) : (
                      <span>{issue.message}</span>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          </div>
          <IconButton
            label="検証結果を閉じる"
            icon="close"
            onClick={() => setValidation(undefined)}
          />
        </div>
      ) : null}

      <div className="editor-workspace">
        <aside className="question-sidebar" aria-label="問題一覧">
          <div className="question-sidebar__header">
            <div>
              <strong>問題一覧</strong>
              <span>{visibleQuestions.length}問</span>
            </div>
            {!isReadOnly ? (
              <IconButton
                label="問題を追加"
                icon="plus"
                onClick={() => void addQuestion()}
              />
            ) : null}
          </div>
          {!isReadOnly && individualReviewQuestions.length > 0 ? (
            <div
              className="question-sidebar__filter"
              role="group"
              aria-label="表示対象"
            >
              <button
                type="button"
                className={showAllQuestions ? "is-active" : undefined}
                onClick={() => {
                  setShowAllQuestions(true);
                  if (!selectedId) setSelectedId(questions[0]?.id || "");
                }}
              >
                すべて {questions.length}
              </button>
              <button
                type="button"
                className={!showAllQuestions ? "is-active" : undefined}
                onClick={() => {
                  setShowAllQuestions(false);
                  setSelectedId(individualReviewQuestions[0]?.id || "");
                }}
              >
                要修正 {individualReviewQuestions.length}
              </button>
            </div>
          ) : null}
          {visibleQuestions.length ? (
            <ol className="question-list">
              {visibleQuestions.map((question) => {
                const index = questions.findIndex(
                  (candidate) => candidate.id === question.id,
                );
                return (
                  <li
                    className={classNames(
                      selectedId === question.id && "is-selected",
                      needsIndividualReview(question) && "has-warning",
                    )}
                    key={question.id}
                  >
                  <button
                    type="button"
                    onClick={() => setSelectedId(question.id)}
                  >
                    <span className="question-list__order">{index + 1}</span>
                    <span className="question-list__copy">
                      <strong>{question.displayLabel}</strong>
                      <small>
                        {question.questionText || "問題文が未入力です"}
                      </small>
                    </span>
                    {question.answerProvenance === "provided_model_answer" ? (
                      <span className="source-dot" title="模範解答から読取">
                        模
                      </span>
                    ) : question.proposalState === "proposed" ? (
                      <span className="proposal-dot" title="AI提案">
                        案
                      </span>
                    ) : null}
                    {needsIndividualReview(question) ? (
                      <Icon name="alert" size={16} />
                    ) : null}
                  </button>
                  {!isReadOnly && selectedId === question.id ? (
                    <div className="question-list__actions">
                      <IconButton
                        label="上へ移動"
                        icon="arrowLeft"
                        disabled={index === 0}
                        onClick={() => void moveQuestion(index, -1)}
                      />
                      <IconButton
                        label="下へ移動"
                        icon="arrowRight"
                        disabled={index === questions.length - 1}
                        onClick={() => void moveQuestion(index, 1)}
                      />
                      <IconButton
                        label="複製"
                        icon="copy"
                        onClick={() => void addQuestion(question)}
                      />
                      <IconButton
                        label="削除"
                        icon="trash"
                        onClick={() => void deleteQuestion(question)}
                      />
                    </div>
                  ) : null}
                  </li>
                );
              })}
            </ol>
          ) : (
            <EmptyState
              title={
                questions.length
                  ? "個別確認はありません"
                  : "問題がありません"
              }
              description={
                questions.length
                  ? "「すべて」に戻すと、AIが作成した問題を確認できます。"
                  : isReadOnly
                    ? "この版には問題が登録されていません。"
                    : "AIの作成完了を待つか、右上の＋から追加してください。"
              }
              icon={questions.length ? "check" : "templates"}
            />
          )}
        </aside>

        <section className="page-canvas-panel" aria-label="問題用紙">
          <div className="canvas-toolbar">
            <div className="source-preview-heading">
              <strong>元の資料</strong>
              <small>AIの下書きと見比べて確認</small>
            </div>
            <div className="source-preview-actions">
              {sources.length > 1 ? (
                <label className="source-preview-select">
                  <span>表示する資料</span>
                  <select
                    value={selectedSourceId}
                    onChange={(event) =>
                      setSelectedSourceId(event.target.value)
                    }
                  >
                    {sources.map((source) => (
                      <option key={source.id} value={source.id}>
                        {source.displayName}・
                        {templateSourceRoleLabel(source.sourceRole)}
                      </option>
                    ))}
                  </select>
                </label>
              ) : activeSource ? (
                <span className="source-preview-role">
                  {templateSourceRoleLabel(activeSource.sourceRole)}
                </span>
              ) : null}
              {activeSource?.contentUrl ? (
                <a
                  className="source-preview-open"
                  href={activeSource.contentUrl}
                  target="_blank"
                  rel="noreferrer"
                >
                  <Icon name="eye" size={14} />
                  別タブで開く
                </a>
              ) : null}
            </div>
          </div>
          <div
            className={classNames(
              "canvas-stage",
              "source-preview-stage",
              sourcePreviewKind === "pdf" && "source-preview-stage--pdf",
            )}
          >
            <div
              className={classNames(
                "paper-canvas",
                sourcePreviewKind === "pdf" && "paper-canvas--pdf",
              )}
            >
              {activeSource?.contentUrl &&
              sourcePreviewKind === "image" &&
              !sourcePreviewFailed ? (
                <img
                  src={activeSource.contentUrl}
                  alt={`元の資料：${activeSource.displayName}`}
                  onError={() => setSourcePreviewFailed(true)}
                />
              ) : activeSource?.contentUrl && sourcePreviewKind === "pdf" ? (
                <iframe
                  key={activeSource.id}
                  className="source-preview-frame"
                  src={activeSource.contentUrl}
                  title={`元の資料：${activeSource.displayName}`}
                  referrerPolicy="no-referrer"
                />
              ) : (
                <div className="paper-canvas__missing">
                  <Icon name="file" size={34} />
                  <strong>
                    {sourcePreviewFailed
                      ? "元の資料を読み込めませんでした"
                      : activeSource
                        ? "この形式は画面内で表示できません"
                        : "元の資料がありません"}
                  </strong>
                  <span>
                    {activeSource?.contentUrl
                      ? "上の「別タブで開く」から原本を確認してください。"
                      : "ひな形に問題用紙を追加し直してください。"}
                  </span>
                </div>
              )}
            </div>
          </div>
        </section>

        <aside className="question-properties" aria-label="選択中の問題">
          {draft ? (
            <QuestionProperties
              question={draft}
              readOnly={isReadOnly}
              onChange={changeDraft}
              onAccept={() => void verifyQuestion(draft)}
              accepting={verifyingQuestionId === draft.id}
              acceptDisabled={saveState !== "saved"}
            />
          ) : (
            <EmptyState
              icon="edit"
              title="問題を選択してください"
              description="左の一覧から編集する問題を選びます。"
            />
          )}
        </aside>
      </div>

      <Modal
        open={bulkVerifyOpen && !isReadOnly}
        onClose={() => !bulkVerifying && setBulkVerifyOpen(false)}
        title="すべての問題を確認済みにしますか？"
        description="元の資料とAIの下書きを見比べたあとに実行してください。"
        size="medium"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setBulkVerifyOpen(false)}
              disabled={bulkVerifying}
            >
              戻る
            </Button>
            <Button
              onClick={() => void verifyAllProposals()}
              disabled={
                isReadOnly ||
                bulkVerifying ||
                proposalQuestions.length === 0
              }
            >
              {bulkVerifying ? "確認しています…" : "すべての問題を確認"}
            </Button>
          </>
        }
      >
        <dl className="proposal-verification-summary">
          <div>
            <dt>確認する問題</dt>
            <dd>{proposalQuestions.length}問</dd>
          </div>
          <div>
            <dt>入力不足がある問題</dt>
            <dd>確認せず残す</dd>
          </div>
        </dl>
        <InlineAlert tone="info">
          <p>
            問題文、正解、配点、採点方法がそろっている問題を確認済みにします。入力不足や構造上の問題がある項目は確認済みにせず、理由と件数を表示します。
          </p>
        </InlineAlert>
      </Modal>

      <Modal
        open={receptionOpen && canStartReception}
        onClose={() => !startingReception && closeReception()}
        title="答案受付を開始"
        description={
          isDraftVersion
            ? "確認済みのひな形を確定し、すぐに答案を受け付けられる状態にします。"
            : "この確定済みひな形を使って、新しい答案受付を開始します。"
        }
        size="medium"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={closeReception}
              disabled={startingReception}
            >
              戻る
            </Button>
            <Button
              onClick={() => void startReception()}
              disabled={startingReception || !receptionDetails.testDate}
            >
              {startingReception ? "開始しています…" : "受付を開始"}
            </Button>
          </>
        }
      >
        <div className="session-form">
          <TemplateSessionMetadata template={editor.data.template} />
          <div className="form-grid form-grid--2">
            <Field
              label="実施日"
              htmlFor="template-reception-date"
              required
              hint="答案用紙に記載された日付を選びます。"
            >
              <input
                id="template-reception-date"
                type="date"
                value={receptionDetails.testDate}
                onChange={(event) => {
                  receptionIdempotencyKeyRef.current = "";
                  pendingPublishReceptionRef.current = undefined;
                  setReceptionDetails({
                    ...receptionDetails,
                    testDate: event.target.value,
                  });
                }}
                required
              />
            </Field>
            <Field
              label="対象クラス"
              htmlFor="template-reception-class"
              hint="クラスを分けて管理するときだけ入力します。"
            >
              <input
                id="template-reception-class"
                value={receptionDetails.classLabel}
                onChange={(event) => {
                  receptionIdempotencyKeyRef.current = "";
                  pendingPublishReceptionRef.current = undefined;
                  setReceptionDetails({
                    ...receptionDetails,
                    classLabel: event.target.value,
                  });
                }}
                placeholder="例：6年A組"
              />
            </Field>
          </div>
          <InlineAlert tone="info">
            <p>
              試験名・教科・学年・カテゴリ・コースはひな形から引き継がれます。試験情報や処理方法を入力し直す必要はありません。
            </p>
          </InlineAlert>
        </div>
        <InlineAlert tone={isDraftVersion ? "warning" : "info"}>
          <p>
            {isDraftVersion
              ? `開始すると第${editor.data.version.versionNumber}版は確定され、編集できなくなります。答案受付は自動で受付中になります。`
              : "確定済みのひな形は変更せず、新しいテスト実施を受付中で作成します。"}
          </p>
        </InlineAlert>
      </Modal>
    </div>
  );
}

export function QuestionProperties({
  question,
  readOnly,
  onChange,
  onAccept,
  accepting,
  acceptDisabled,
}: {
  question: TemplateQuestion;
  readOnly: boolean;
  onChange: (changes: Partial<TemplateQuestion>) => void;
  onAccept: () => void;
  accepting: boolean;
  acceptDisabled: boolean;
}) {
  const [variants, setVariants] = useState(() =>
    questionAcceptedVariants(question),
  );
  const [phoneticExceptions, setPhoneticExceptions] = useState(() =>
    questionPhoneticExceptions(question),
  );

  useEffect(() => {
    setVariants(questionAcceptedVariants(question));
    setPhoneticExceptions(questionPhoneticExceptions(question));
  }, [question.id, question.acceptedAnswers]);

  function updateAnswers(
    canonical: string,
    variantText = variants,
    phoneticExceptionText = phoneticExceptions,
  ) {
    onChange({
      canonicalAnswer: canonical,
      acceptedAnswers: answersForQuestionEdit(
        question,
        canonical,
        variantText,
        phoneticExceptionText,
      ),
    });
  }

  const canonical = questionCanonical(question);
  const hasKanji = /[\u3400-\u9fff\uf900-\ufaff]/u.test(canonical);

  return (
    <div className="properties-form">
      <div className="properties-form__heading">
        <div>
          <span>選択中の問題</span>
          <h2>{question.displayLabel}</h2>
        </div>
        <div className="properties-form__badges">
          {question.answerProvenance === "provided_model_answer" ? (
            <Badge tone="success">模範解答から読取</Badge>
          ) : null}
          {question.proposalState === "proposed" ? (
            <Badge tone="accent">AI提案</Badge>
          ) : null}
        </div>
      </div>
      {substantiveWarnings(question).map((warning) => (
        <InlineAlert tone="warning" key={warning}>
          <p>{warning}</p>
        </InlineAlert>
      ))}
      {!readOnly && needsProposalVerification(question) ? (
        <div className="proposal-question-action">
          <div>
            <strong>この問題を確認</strong>
            <small>
              内容を確認したら、この問題と解答候補をまとめて確認済みにします。
            </small>
          </div>
          <Button
            size="small"
            onClick={onAccept}
            disabled={accepting || acceptDisabled}
          >
            {accepting ? "確認中…" : "確認済みにする"}
          </Button>
        </div>
      ) : null}
      <div className="form-grid form-grid--label-points">
        <Field label="番号・ラベル" htmlFor="question-label">
          <input
            id="question-label"
            value={question.displayLabel}
            disabled={readOnly}
            onChange={(event) => onChange({ displayLabel: event.target.value })}
          />
        </Field>
        <Field label="配点" htmlFor="question-points">
          <div className="input-suffix">
            <input
              id="question-points"
              type="number"
              min="0.1"
              step="0.5"
              value={question.maxPointsMilli / 1000}
              disabled={readOnly}
              onChange={(event) =>
                onChange({
                  maxPointsMilli: Math.round(
                    Number(event.target.value) * 1000,
                  ),
                })
              }
            />
            <span>点</span>
          </div>
        </Field>
      </div>
      <Field label="問題文" htmlFor="question-text" required>
        <textarea
          id="question-text"
          rows={4}
          value={question.questionText}
          disabled={readOnly}
          onChange={(event) => onChange({ questionText: event.target.value })}
        />
      </Field>
      <Field
        label="採点方法"
        htmlFor="question-grading-preset"
        hint="通常は「AIで判定」のままで使えます。必要な問題だけ変更してください。"
      >
        <select
          id="question-grading-preset"
          value={gradingPresetForQuestion(question)}
          disabled={readOnly}
          onChange={(event) => {
            const preset = event.target.value as GradingPreset;
            if (preset !== "custom") {
              onChange(changesForGradingPreset(question, preset));
            }
          }}
        >
          <option value="ai">AIで判定（おすすめ）</option>
          <option value="exact">完全一致・登録した別表記で判定</option>
          <option value="numeric">数値として判定</option>
          <option value="choice">選択肢として判定</option>
          <option value="manual">先生が採点</option>
          {gradingPresetForQuestion(question) === "custom" ? (
            <option value="custom">現在の詳細設定を使用</option>
          ) : null}
        </select>
      </Field>
      <Field
        label={question.questionType === "subjective" ? "模範解答" : "正解"}
        htmlFor="canonical-answer"
        required
      >
        <textarea
          id="canonical-answer"
          rows={2}
          value={canonical}
          disabled={readOnly}
          onChange={(event) => updateAnswers(event.target.value)}
        />
      </Field>
      {question.questionType === "subjective" ||
      question.gradingMode === "ai_rubric" ? (
        <Field
          label="採点基準"
          htmlFor="question-rubric"
          hint="部分点を認める場合は、点数と条件を明記します。"
        >
          <textarea
            id="question-rubric"
            rows={4}
            value={question.rubric || ""}
            disabled={readOnly}
            onChange={(event) => onChange({ rubric: event.target.value })}
          />
        </Field>
      ) : null}

      <details className="question-advanced-settings" key={question.id}>
        <summary>
          <span>
            <strong>詳細設定</strong>
            <small>通常は変更不要です</small>
          </span>
          <Icon name="chevronDown" size={16} />
        </summary>
        <div className="question-advanced-settings__content">
          <div className="form-grid form-grid--2">
            <Field label="解答形式（詳細）" htmlFor="question-type">
              <select
                id="question-type"
                value={question.questionType}
                disabled={readOnly}
                onChange={(event) =>
                  onChange(
                    defaultsForQuestionTypeChange(
                      question,
                      event.target.value,
                    ),
                  )
                }
              >
                <option value="multiple_choice">選択式</option>
                <option value="numeric">数値</option>
                <option value="exact_short_text">短答（完全一致）</option>
                <option value="semantic_short_text">
                  短答（AIが意味を確認）
                </option>
                <option value="subjective">記述（AIが採点）</option>
              </select>
            </Field>
            <Field label="判定方式（詳細）" htmlFor="grading-mode">
              <select
                id="grading-mode"
                value={question.gradingMode}
                disabled={readOnly}
                onChange={(event) => {
                  if (event.target.value === "ai_rubric") {
                    onChange(
                      changesForGradingPreset(question, "ai"),
                    );
                  } else {
                    onChange({ gradingMode: event.target.value });
                  }
                }}
              >
                <option value="deterministic">規則で判定</option>
                <option value="transcribe_then_rules">読取後に規則で判定</option>
                <option value="ai_rubric">AIが採点基準で判定</option>
                <option value="manual">先生が確認</option>
              </select>
            </Field>
          </div>
          <Field label="部分点の単位" htmlFor="question-point-increment">
            <div className="input-suffix">
              <input
                id="question-point-increment"
                type="number"
                min="0.1"
                step="0.1"
                value={question.pointIncrementMilli / 1000}
                disabled={readOnly}
                onChange={(event) =>
                  onChange({
                    pointIncrementMilli: Math.round(
                      Number(event.target.value) * 1000,
                    ),
                  })
                }
              />
              <span>点</span>
            </div>
            <small>通常は1点です。配点を割り切れる値にします。</small>
          </Field>
          <Field
            label="正解として認める別表記"
            htmlFor="answer-variants"
            hint="必要な場合だけ、1行に1つ入力します。"
          >
            <textarea
              id="answer-variants"
              rows={3}
              value={variants}
              disabled={readOnly}
              onChange={(event) => {
                setVariants(event.target.value);
                updateAnswers(canonical, event.target.value);
              }}
            />
          </Field>
          <label className="setting-check">
            <input
              type="checkbox"
              checked={question.requiresCompleteAnswer}
              disabled={readOnly}
              onChange={(event) =>
                onChange({ requiresCompleteAnswer: event.target.checked })
              }
            />
            <span>
              <strong>完答</strong>
              <small>
                すべての正解要素がそろった場合だけ満点にし、不足時は部分点を付けません。
              </small>
            </span>
          </label>
          <label className="setting-check">
            <input
              type="checkbox"
              checked={question.answerOrderInsensitive}
              disabled={readOnly}
              onChange={(event) =>
                onChange({ answerOrderInsensitive: event.target.checked })
              }
            />
            <span>
              <strong>順不同</strong>
              <small>
                「、」「,」「，」「/」「／」「;」「；」「・」または改行で区切った要素を、重複数も含む完全な組として順序に関係なく判定します。
              </small>
            </span>
          </label>
          <label className="setting-check">
            <input
              type="checkbox"
              checked={isKanjiRequired(question)}
              disabled={readOnly}
              onChange={(event) =>
                onChange({
                  allowNonKanji: allowNonKanjiForKanjiRequired(
                    event.target.checked,
                  ),
                })
              }
            />
            <span>
              <strong>漢字必須</strong>
              <small>
                {!question.allowNonKanji
                  ? hasKanji
                    ? "正解に漢字がある場合、ひらがな・カタカナだけの同じ読みは不正解です。下の例外欄に登録した読みだけ正解にできます。"
                    : "正解に漢字がないため、この設定による違いはありません。"
                  : "登録した読みや採点基準に合えば、漢字以外の表記も正解にできます。"}
              </small>
            </span>
          </label>
          {!question.allowNonKanji ? (
            <Field
              label="漢字必須の例外（読み）"
              htmlFor="phonetic-exceptions"
              hint="ひらがな・カタカナでも正解にする読みを、1行に1つ入力します。"
            >
              <textarea
                id="phonetic-exceptions"
                rows={3}
                value={phoneticExceptions}
                disabled={readOnly}
                onChange={(event) => {
                  setPhoneticExceptions(event.target.value);
                  updateAnswers(canonical, variants, event.target.value);
                }}
              />
            </Field>
          ) : null}
          <label className="setting-check">
            <input
              type="checkbox"
              checked={question.requiresReviewAlways}
              disabled={readOnly}
              onChange={(event) =>
                onChange({ requiresReviewAlways: event.target.checked })
              }
            />
            <span>
              <strong>採点後に必ず先生が確認する</strong>
              <small>
                通常はオフです。AIの確信度にかかわらず毎回答を確認したい場合だけオンにします。
              </small>
            </span>
          </label>
          {question.questionType !== "subjective" &&
          question.gradingMode !== "ai_rubric" ? (
            <Field
              label="部分点・採点基準"
              htmlFor="question-rubric"
              hint="必要な場合だけ入力します。"
            >
              <textarea
                id="question-rubric"
                rows={4}
                value={question.rubric || ""}
                disabled={readOnly}
                onChange={(event) => onChange({ rubric: event.target.value })}
              />
            </Field>
          ) : null}
          <Field
            label="先生向けメモ"
            htmlFor="teacher-note"
            hint="必要な場合だけ入力します。結果PDFには表示されません。"
          >
            <textarea
              id="teacher-note"
              rows={2}
              value={customTeacherNote(question)}
              disabled={readOnly}
              onChange={(event) =>
                onChange({
                  teacherNote: mergeCustomTeacherNote(
                    question,
                    event.target.value,
                  ),
                })
              }
            />
          </Field>
        </div>
      </details>
    </div>
  );
}

export function questionPayload(question: TemplateQuestion, verify = false) {
  return {
    displayLabel: question.displayLabel,
    order: question.order,
    questionText: question.questionText,
    questionType: question.questionType,
    gradingMode: question.gradingMode,
    maxPointsMilli: question.maxPointsMilli,
    pointIncrementMilli: question.pointIncrementMilli,
    allowNonKanji: question.allowNonKanji,
    requiresCompleteAnswer: question.requiresCompleteAnswer,
    answerOrderInsensitive: question.answerOrderInsensitive,
    acceptedAnswers: verify
      ? question.acceptedAnswers.map((answer) => ({
          ...answer,
          teacherVerified: true,
        }))
      : question.acceptedAnswers,
    canonicalAnswer: question.canonicalAnswer,
    rubric: question.rubric,
    teacherNote: question.teacherNote,
    requiresReviewAlways: question.requiresReviewAlways,
    teacherVerified: verify ? true : question.teacherVerified,
  };
}

export function validationFromPublishError(
  reason: unknown,
  questions: TemplateQuestion[],
): TemplateValidation | undefined {
  if (
    !(reason instanceof ApiError) ||
    reason.problem.code !== "TEMPLATE_PUBLISH_BLOCKED" ||
    !reason.problem.errors?.length
  ) {
    return undefined;
  }
  const ordered = [...questions].sort((a, b) => a.order - b.order);
  const issues = reason.problem.errors.map((error) => {
    const extended = error as typeof error & {
      questionId?: string;
      blocking?: boolean;
    };
    const questionIndex = error.field?.match(/^questions\[(\d+)\]/u)?.[1];
    return {
      code: error.code || "TEMPLATE_PUBLISH_BLOCKED",
      message: error.message,
      questionId:
        extended.questionId ||
        (questionIndex === undefined
          ? undefined
          : ordered[Number(questionIndex)]?.id),
      blocking: extended.blocking ?? true,
    };
  });
  return {
    valid: false,
    pageCount: 0,
    questionCount: questions.length,
    totalPointsMilli: questions.reduce(
      (sum, question) => sum + question.maxPointsMilli,
      0,
    ),
    kanjiRequiredCount: questions.filter(isKanjiRequired).length,
    alwaysReviewCount: questions.filter(
      (question) => question.requiresReviewAlways,
    ).length,
    issues,
  };
}

function errorMessage(reason: unknown, fallback: string) {
  if (reason instanceof ApiError) {
    return reason.problem.errors?.[0]?.message || reason.message;
  }
  return reason instanceof Error ? reason.message : fallback;
}

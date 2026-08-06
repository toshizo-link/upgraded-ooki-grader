import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
} from "react";
import { Link, useNavigate, useParams } from "../router";
import { Icon } from "../components/Icon";
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
import { classNames, formatDateTime, formatPoints } from "../lib/format";
import type {
  PagedResponse,
  TemplateQuestion,
  TemplateSource,
  TemplateSummary,
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
        pointIncrementMilli: value.question.pointIncrementMilli ?? 1,
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

function questionVariants(question: TemplateQuestion) {
  return question.acceptedAnswers
    .filter((answer) => answer.variantType !== "canonical")
    .map((answer) => answer.text)
    .join("\n");
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
  const [publishOpen, setPublishOpen] = useState(false);
  const [publishing, setPublishing] = useState(false);
  const [retryingGeneration, setRetryingGeneration] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const [recoveryDraft, setRecoveryDraft] = useState<LocalQuestionDraft>();
  const draftRef = useRef<TemplateQuestion | undefined>(undefined);
  const completedGenerationRef = useRef("");

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
          : {
              displayLabel: `問${questions.length + 1}`,
              order: questions.length + 1,
              questionText: "",
              questionType: "exact_short_text",
              gradingMode: "transcribe_then_rules",
              maxPointsMilli:
                editor.data?.version.defaultPointsMilli ?? 1000,
              pointIncrementMilli: 1,
              allowNonKanji: false,
              acceptedAnswers: [],
              requiresReviewAlways: false,
            },
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

  async function verifyNonBlockingProposals() {
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
          selectionMode: "allNonBlocking",
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
      const nextQuestion =
        updated.find(needsIndividualReview) ||
        updated.find(needsProposalVerification) ||
        updated[0];
      setSelectedId(nextQuestion?.id || "");
      if (!updated.some(needsIndividualReview)) setShowAllQuestions(true);
      editor.reload();
    } catch (reason) {
      setActionError(
        errorMessage(reason, "安全な提案を一括確認できませんでした。"),
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
      setValidation(undefined);
    } catch (reason) {
      setActionError(errorMessage(reason, "この問題を確認済みにできませんでした。"));
    } finally {
      setVerifyingQuestionId("");
    }
  }

  async function validateForPublish() {
    setValidating(true);
    setActionError(undefined);
    try {
      const report = await api.post<TemplateValidation>(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}:validate`,
        {},
      );
      setValidation(report);
      if (report.valid) setPublishOpen(true);
    } catch (reason) {
      setActionError(
        errorMessage(reason, "公開前の確認を完了できませんでした。"),
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

  async function publish() {
    setPublishing(true);
    setActionError(undefined);
    try {
      const latest = await api.get<TemplateVersionDetail>(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}`,
      );
      await api.post(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}:publish`,
        { revision: latest.revision },
        {
          idempotencyKey: newIdempotencyKey(),
          etag: `"rev-${latest.revision}"`,
        },
      );
      setPublishOpen(false);
      navigate("/templates", {
        state: { message: "テストひな形を公開しました。" },
      });
    } catch (reason) {
      setActionError(errorMessage(reason, "公開できませんでした。"));
      setPublishOpen(false);
    } finally {
      setPublishing(false);
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

  const isReadOnly = editor.data.version.state === "published";
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
  const nonBlockingProposalCount = proposalQuestions.filter(
    (question) => !needsIndividualReview(question),
  ).length;
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
          {!isReadOnly ? (
            <Button
              onClick={() => void validateForPublish()}
              disabled={validating || saveState !== "saved"}
            >
              {validating ? "確認中…" : "公開する"}
            </Button>
          ) : null}
        </div>
      </header>

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
      {recoveryDraft ? (
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
            <Button
              variant="secondary"
              size="small"
              onClick={() => setSaveState("dirty")}
            >
              再試行
            </Button>
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
            questions.length === 0 ? (
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
              "資料は保存されています。もう一度試しても失敗する場合は、右上の＋から問題を追加できます。"}
          </p>
        </InlineAlert>
      ) : null}
      {!isReadOnly &&
      questions.length > 0 &&
      !["queued", "running"].includes(generation.data?.state || "") &&
      proposalQuestions.length > 0 ? (
        <InlineAlert
          tone={individualReviewQuestions.length ? "warning" : "info"}
          title={
            bulkVerification
              ? `${bulkVerification.verifiedQuestionCount}問を確認済み。${individualReviewQuestions.length}問は個別確認が必要です`
              : individualReviewQuestions.length
              ? `${individualReviewQuestions.length}問は個別確認が必要です`
              : `${nonBlockingProposalCount}問のAI下書きができました`
          }
          action={
            nonBlockingProposalCount > 0 ? (
              <Button
                size="small"
                onClick={() => setBulkVerifyOpen(true)}
                disabled={bulkVerifying || saveState !== "saved"}
              >
                まとめて確認
              </Button>
            ) : undefined
          }
        >
          <p>
            {bulkVerification
              ? "残りは左の「要修正」から順に確認してください。"
              : "元の資料と問題文・正解・配点を見比べてください。"}
            {!bulkVerification && nonBlockingProposalCount > 0
              ? ` 警告のない${nonBlockingProposalCount}問はまとめて確認済みにできます。`
              : !bulkVerification
                ? " 左の「要修正」から順に確認できます。"
                : ""}
          </p>
        </InlineAlert>
      ) : null}
      {validation && !validation.valid ? (
        <div className="editor-validation" role="alert">
          <div>
            <Icon name="alert" />
            <div>
              <strong>公開前に{validation.issues.length}件を確認してください</strong>
              <ul>
                {validation.issues.map((issue) => (
                  <li key={`${issue.code}-${issue.questionId || ""}`}>
                    <button
                      type="button"
                      onClick={() =>
                        issue.questionId && setSelectedId(issue.questionId)
                      }
                    >
                      {issue.message}
                    </button>
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
        open={bulkVerifyOpen}
        onClose={() => !bulkVerifying && setBulkVerifyOpen(false)}
        title={`警告のない${nonBlockingProposalCount}問をまとめて確認しますか？`}
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
              onClick={() => void verifyNonBlockingProposals()}
              disabled={bulkVerifying || nonBlockingProposalCount === 0}
            >
              {bulkVerifying
                ? "確認しています…"
                : `${nonBlockingProposalCount}問を確認済みにする`}
            </Button>
          </>
        }
      >
        <dl className="proposal-verification-summary">
          <div>
            <dt>一括確認の候補</dt>
            <dd>{nonBlockingProposalCount}問</dd>
          </div>
          <div>
            <dt>個別確認</dt>
            <dd>{individualReviewQuestions.length}問</dd>
          </div>
        </dl>
        <InlineAlert tone="info">
          <p>
            問題文、正解、配点、採点方法を確認済みにします。要修正の問題は対象外です。
          </p>
        </InlineAlert>
      </Modal>

      <Modal
        open={publishOpen}
        onClose={() => !publishing && setPublishOpen(false)}
        title={`第${editor.data.version.versionNumber}版を公開しますか？`}
        description="公開した版は変更できません。修正時は新しい下書き版を作成します。"
        size="medium"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setPublishOpen(false)}
              disabled={publishing}
            >
              戻る
            </Button>
            <Button
              onClick={() => void publish()}
              disabled={publishing}
            >
              {publishing ? "公開しています…" : "この版を公開"}
            </Button>
          </>
        }
      >
        {validation ? (
          <dl className="publish-summary">
            <div>
              <dt>ページ</dt>
              <dd>{validation.pageCount}</dd>
            </div>
            <div>
              <dt>問題</dt>
              <dd>{validation.questionCount}問</dd>
            </div>
            <div>
              <dt>合計点</dt>
              <dd>{formatPoints(validation.totalPointsMilli)}点</dd>
            </div>
            <div>
              <dt>漢字必須</dt>
              <dd>{validation.kanjiRequiredCount}問</dd>
            </div>
            <div>
              <dt>常に確認</dt>
              <dd>{validation.alwaysReviewCount}問</dd>
            </div>
          </dl>
        ) : null}
        <InlineAlert tone="warning">
          <p>
            この版を使って開始した採点は、後から別の版を公開しても自動では変わりません。
          </p>
        </InlineAlert>
      </Modal>
    </div>
  );
}

function QuestionProperties({
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
  const [variants, setVariants] = useState(() => questionVariants(question));

  useEffect(() => {
    setVariants(questionVariants(question));
  }, [question.id, question.acceptedAnswers]);

  function updateAnswers(canonical: string, variantText = variants) {
    const accepted = variantText
      .split("\n")
      .map((value) => value.trim())
      .filter(Boolean);
    onChange({
      canonicalAnswer: canonical,
      acceptedAnswers: [
        ...(canonical
          ? [{ text: canonical, variantType: "canonical" as const }]
          : []),
        ...accepted.map((text) => ({
          text,
          variantType: "accepted" as const,
        })),
      ],
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
            <strong>採点の詳細設定</strong>
            <small>問題の種類・別表記・部分点など</small>
          </span>
          <Icon name="chevronDown" size={16} />
        </summary>
        <div className="question-advanced-settings__content">
          <div className="form-grid form-grid--2">
            <Field label="問題の種類" htmlFor="question-type">
              <select
                id="question-type"
                value={question.questionType}
                disabled={readOnly}
                onChange={(event) =>
                  onChange({ questionType: event.target.value })
                }
              >
                <option value="multiple_choice">選択式</option>
                <option value="numeric">数値</option>
                <option value="exact_short_text">短答（完全一致）</option>
                <option value="semantic_short_text">短答（意味で判定）</option>
                <option value="subjective">記述・先生が確認</option>
              </select>
            </Field>
            <Field label="採点方法" htmlFor="grading-mode">
              <select
                id="grading-mode"
                value={question.gradingMode}
                disabled={readOnly}
                onChange={(event) =>
                  onChange({ gradingMode: event.target.value })
                }
              >
                <option value="deterministic">規則で判定</option>
                <option value="transcribe_then_rules">読取後に規則で判定</option>
                <option value="ai_rubric">採点基準で判定</option>
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
            <small>配点を割り切れる値にします。</small>
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
              checked={question.allowNonKanji}
              disabled={readOnly}
              onChange={(event) =>
                onChange({ allowNonKanji: event.target.checked })
              }
            />
            <span>
              <strong>漢字以外の解答も正解にする</strong>
              <small>
                {question.allowNonKanji
                  ? "登録した読みや採点基準に合えば正解にできます。"
                  : hasKanji
                    ? "ひらがな・カタカナだけの同じ読みは不正解になります。"
                    : "正解に漢字がないため、この設定による違いはありません。"}
              </small>
            </span>
          </label>
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
              <small>AIの確信度にかかわらず確認待ちにします。</small>
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

function questionPayload(question: TemplateQuestion, verify = false) {
  return {
    displayLabel: question.displayLabel,
    order: question.order,
    questionText: question.questionText,
    questionType: question.questionType,
    gradingMode: question.gradingMode,
    maxPointsMilli: question.maxPointsMilli,
    pointIncrementMilli: question.pointIncrementMilli,
    allowNonKanji: question.allowNonKanji,
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

function errorMessage(reason: unknown, fallback: string) {
  if (reason instanceof ApiError) {
    return reason.problem.errors?.[0]?.message || reason.message;
  }
  return reason instanceof Error ? reason.message : fallback;
}

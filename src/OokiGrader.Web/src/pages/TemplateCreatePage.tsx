import {
  useRef,
  useState,
  type ChangeEvent,
  type DragEvent,
} from "react";
import { Link, useNavigate } from "../router";
import { Icon } from "../components/Icon";
import {
  Button,
  Card,
  Field,
  InlineAlert,
  PageHeader,
} from "../components/ui";
import {
  ApiError,
  api,
  newIdempotencyKey,
  uploadFile,
} from "../lib/api";
import { useApiQuery } from "../hooks/useApiQuery";
import type {
  RuntimeCapabilities,
  TemplateSummary,
  TemplateVersionDetail,
} from "../types";

type SourceRole =
  | "blankTest"
  | "containsModelAnswers"
  | "containsNonModelAnswers"
  | "separateAnswerKey";

type WorkflowState =
  | "idle"
  | "uploading"
  | "matching"
  | "reviewing"
  | "creating"
  | "attaching"
  | "generating"
  | "failed";

export interface TemplateMetadata {
  title: string;
  subject: string;
  category: string;
  gradeLabel: string;
  course: string;
  defaultPointsMilli: number;
}

export type TemplateMetadataTextField =
  | "title"
  | "subject"
  | "category"
  | "gradeLabel"
  | "course";

interface PendingSource {
  id: string;
  file: File;
  sourceRole: SourceRole;
  roleInference: "strong" | "default" | "user";
  progress: number;
  state: "ready" | "uploading" | "uploaded" | "attached" | "failed";
  uploadId?: string;
  error?: string;
}

interface ExactSourceMatch {
  templateId: string;
  templateTitle: string;
  versionId: string;
  versionNumber: number;
  contentHash: string;
  publishedAt?: string;
  sources: Array<{ uploadId: string; sourceRole: SourceRole }>;
}

const SOURCE_REVIEW_SECONDS = 5;
const ANSWER_KEY_PATTERN =
  /(模範[\s_-]*解答|解答[\s_-]*(?:例|一覧)|正答|答え|answer[\s_-]*key|model[\s_-]*answer|solutions?)/iu;
const EMBEDDED_ANSWER_PATTERN =
  /((?:模範[\s_-]*解答|正答|解答[\s_-]*(?:例|見本))[\s_-]*(?:付き|入り|記入済み?)|記入例|model[\s_-]*answer[\s_-]*(?:included|filled)|answer[\s_-]*key[\s_-]*included)/iu;
const EXPLICIT_NON_MODEL_ANSWER_PATTERN =
  /(生徒[\s_-]*(?:答案|解答|回答)|(?:答案|解答|回答)[\s_-]*採点前|受験済み|非模範[\s_-]*(?:答案|解答|回答)|模範[\s_-]*解答[\s_-]*ではない|student[\s_-]*(?:answers?|responses?)|non[\s_-]*model[\s_-]*answers?|(?:completed|filled)[\s_-]*(?:test|exam))/iu;
const FILLED_ANSWER_PATTERN =
  /((?:答案|解答|回答)[\s_-]*(?:付き|記入済み?|回答済み?)|記入済み)/iu;
const PLAIN_ANSWER_SHEET_PATTERN =
  /(?:解答|答案)[\s_-]*用紙/iu;

export function TemplateCreatePage() {
  const navigate = useNavigate();
  const capabilities = useApiQuery<RuntimeCapabilities>(
    "runtime-capabilities",
    (signal) => api.get("/capabilities", undefined, signal),
  );
  const [metadata, setMetadataState] = useState<TemplateMetadata>({
    title: "",
    subject: "",
    category: "",
    gradeLabel: "",
    course: "",
    defaultPointsMilli: 1000,
  });
  const metadataRef = useRef(metadata);
  const teacherMetadataFieldsRef = useRef(
    new Set<TemplateMetadataTextField>(),
  );
  const [sources, setSourcesState] = useState<PendingSource[]>([]);
  const sourcesRef = useRef<PendingSource[]>([]);
  const draftIdsRef = useRef({ templateId: "", versionId: "" });
  const continueReviewRef = useRef<(() => void) | null>(null);
  const reuseDecisionRef = useRef<((createNew: boolean) => void) | null>(null);
  const manualRequestedRef = useRef(false);
  const [manualPreferred, setManualPreferred] = useState(false);
  const [workflow, setWorkflow] = useState<WorkflowState>("idle");
  const [reviewSeconds, setReviewSeconds] = useState(SOURCE_REVIEW_SECONDS);
  const [dragging, setDragging] = useState(false);
  const [exactMatch, setExactMatch] = useState<ExactSourceMatch>();
  const [error, setError] = useState<string>();
  const templateGeneration = capabilities.data?.ai.templateGeneration;
  const aiTemplateReady = templateGeneration?.ready === true;
  const working = !["idle", "failed"].includes(workflow);

  function setMetadata(
    changes: Partial<TemplateMetadata>,
    teacherEntered = false,
  ) {
    if (teacherEntered) {
      for (const field of [
        "title",
        "subject",
        "category",
        "gradeLabel",
        "course",
      ] satisfies TemplateMetadataTextField[]) {
        if (field in changes) {
          if (String(changes[field] ?? "").trim()) {
            teacherMetadataFieldsRef.current.add(field);
          } else {
            teacherMetadataFieldsRef.current.delete(field);
          }
        }
      }
    }
    const next = { ...metadataRef.current, ...changes };
    metadataRef.current = next;
    setMetadataState(next);
  }

  function replaceSources(next: PendingSource[]) {
    sourcesRef.current = next;
    setSourcesState(next);
  }

  function updateSource(id: string, changes: Partial<PendingSource>) {
    replaceSources(
      sourcesRef.current.map((item) =>
        item.id === id ? { ...item, ...changes } : item,
      ),
    );
  }

  function addFiles(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files || []);
    event.target.value = "";
    beginWithFiles(files);
  }

  function dropFiles(event: DragEvent<HTMLLabelElement>) {
    event.preventDefault();
    setDragging(false);
    if (working) return;
    beginWithFiles(Array.from(event.dataTransfer.files || []));
  }

  function beginWithFiles(files: File[]) {
    if (!files.length || working) return;
    setError(undefined);
    setExactMatch(undefined);
    draftIdsRef.current = { templateId: "", versionId: "" };
    manualRequestedRef.current = manualPreferred;

    const inferredMetadata = inferTemplateMetadata(files);
    const nextMetadata = {
      ...metadataRef.current,
      title: metadataRef.current.title.trim() || inferredMetadata.title,
      subject: metadataRef.current.subject || inferredMetadata.subject,
    };
    setMetadata(nextMetadata);

    const pending = files.map((file) => {
      const inference = inferSourceRole(file, files);
      return {
        id: crypto.randomUUID(),
        file,
        sourceRole: inference.role,
        roleInference: inference.confidence,
        progress: 0,
        state: "ready" as const,
      };
    });
    replaceSources(pending);
    void runAutomaticFlow();
  }

  async function runAutomaticFlow() {
    setError(undefined);
    try {
      setWorkflow("uploading");
      for (const source of sourcesRef.current) {
        if (source.uploadId) continue;
        updateSource(source.id, {
          state: "uploading",
          progress: 0,
          error: undefined,
        });
        try {
          const finalized = await uploadFile(source.file, {
            purpose: "templateSource",
            onProgress: (uploaded, total) =>
              updateSource(source.id, {
                progress: total ? Math.round((uploaded / total) * 100) : 0,
              }),
          });
          updateSource(source.id, {
            uploadId: finalized.uploadId,
            state: "uploaded",
            progress: 100,
          });
        } catch (reason) {
          updateSource(source.id, {
            state: "failed",
            error: errorMessage(reason, "アップロードできませんでした。"),
          });
          throw reason;
        }
      }

      setWorkflow("reviewing");
      await waitForSourceReview();

      const createNew = await checkForExactSourceMatch();
      if (!createNew) return;

      const safeMetadata = {
        ...metadataRef.current,
        title: metadataRef.current.title.trim() || "新しいテスト",
        subject: metadataRef.current.subject || "自動判定中",
      };
      setMetadata(safeMetadata);
      const canGenerate =
        !manualRequestedRef.current && (await resolveAiTemplateReadiness());
      const creationMetadata = metadataForCreation(
        safeMetadata,
        teacherMetadataFieldsRef.current,
        canGenerate,
      );
      setWorkflow("creating");

      let { templateId, versionId } = draftIdsRef.current;
      if (!templateId) {
        const template = await api.post<TemplateSummary>(
          "/templates",
          creationMetadata,
          { idempotencyKey: newIdempotencyKey() },
        );
        templateId = template.id;
        draftIdsRef.current.templateId = template.id;
      }
      if (!versionId) {
        const version = await api.post<TemplateVersionDetail>(
          `/templates/${encodeURIComponent(templateId)}/versions`,
          {
            sourceVersionId: null,
            defaultPointsMilli: safeMetadata.defaultPointsMilli,
          },
          { idempotencyKey: newIdempotencyKey() },
        );
        versionId = version.id;
        draftIdsRef.current.versionId = version.id;
      }

      setWorkflow("attaching");
      for (const source of sourcesRef.current) {
        if (source.state === "attached") continue;
        if (!source.uploadId) {
          throw new Error(`${source.file.name}のアップロード情報がありません。`);
        }
        await api.post(
          `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}/sources`,
          {
            uploadId: source.uploadId,
            sourceRole: source.sourceRole,
            displayName: source.file.name,
          },
          { idempotencyKey: newIdempotencyKey() },
        );
        updateSource(source.id, { state: "attached" });
      }

      if (canGenerate) {
        setWorkflow("generating");
        await api.post(
          `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}:generateDraft`,
          {
            priority: "economy",
            replaceableMetadataFields: (
              [
                "title",
                "subject",
                "category",
                "gradeLabel",
                "course",
              ] satisfies TemplateMetadataTextField[]
            ).filter(
              (field) => !teacherMetadataFieldsRef.current.has(field),
            ),
          },
          { idempotencyKey: newIdempotencyKey() },
        );
      }

      navigate(
        `/templates/${encodeURIComponent(templateId)}/versions/${encodeURIComponent(versionId)}`,
        {
          state: {
            message: canGenerate
              ? "問題・正答・配点・採点基準を自動作成しています。完了すると原本と下書きを並べて確認できます。"
              : manualRequestedRef.current
                ? "問題用紙を保存しました。手動編集画面を開きました。"
                : "問題用紙を保存しました。AIを利用できないため、手動編集画面を開きました。",
          },
        },
      );
    } catch (reason) {
      setWorkflow("failed");
      setError(errorMessage(reason, "ひな形の自動作成を開始できませんでした。"));
    }
  }

  async function checkForExactSourceMatch() {
    const uploadedSources = sourcesRef.current.filter(
      (source): source is PendingSource & { uploadId: string } =>
        Boolean(source.uploadId),
    );
    if (!uploadedSources.length) return true;
    try {
      setWorkflow("matching");
      const result = await api.get<{ exactMatch: ExactSourceMatch | null }>(
        "/templates/source-match",
        {
          uploadIds: uploadedSources.map((source) => source.uploadId).join(","),
          sourceRoles: uploadedSources
            .map((source) => source.sourceRole)
            .join(","),
        },
      );
      if (!result.exactMatch) return true;
      setExactMatch(result.exactMatch);
      return await new Promise<boolean>((resolve) => {
        reuseDecisionRef.current = resolve;
      });
    } catch {
      // Reuse detection is an optimization. Creation remains available if it
      // cannot be checked, and the publish path still protects immutability.
      return true;
    } finally {
      reuseDecisionRef.current = null;
    }
  }

  function openExactMatch() {
    if (!exactMatch) return;
    reuseDecisionRef.current?.(false);
    navigate(
      `/templates/${encodeURIComponent(exactMatch.templateId)}/versions/${encodeURIComponent(exactMatch.versionId)}`,
      { state: { message: "同じ資料の公開済みひな形を再利用します。" } },
    );
  }

  function createDespiteExactMatch() {
    setExactMatch(undefined);
    reuseDecisionRef.current?.(true);
  }

  async function resolveAiTemplateReadiness() {
    if (capabilities.data) {
      return capabilities.data.ai.templateGeneration.ready === true;
    }
    try {
      const current = await api.get<RuntimeCapabilities>("/capabilities");
      return current.ai.templateGeneration.ready === true;
    } catch {
      return false;
    }
  }

  function waitForSourceReview() {
    setReviewSeconds(SOURCE_REVIEW_SECONDS);
    return new Promise<void>((resolve) => {
      let remaining = SOURCE_REVIEW_SECONDS;
      let finished = false;
      const finish = () => {
        if (finished) return;
        finished = true;
        window.clearInterval(timer);
        continueReviewRef.current = null;
        resolve();
      };
      const timer = window.setInterval(() => {
        remaining -= 1;
        setReviewSeconds(Math.max(remaining, 0));
        if (remaining <= 0) finish();
      }, 1000);
      continueReviewRef.current = finish;
    });
  }

  function continueNow() {
    continueReviewRef.current?.();
  }

  function requestManualEditing() {
    manualRequestedRef.current = true;
    setManualPreferred(true);
  }

  function toggleManualPreference() {
    const next = !manualPreferred;
    setManualPreferred(next);
    manualRequestedRef.current = next;
  }

  const workflowCopy = workflowStatus(workflow, reviewSeconds);

  return (
    <div className="page">
      <PageHeader
        eyebrow="自動作成"
        title="テストひな形を作成"
        description="問題用紙を追加すると、AIが問題・正答・配点の下書きを自動で作成します。"
        backAction={
          <Link className="back-link" to="/templates">
            <Icon name="arrowLeft" size={17} />
            ひな形一覧へ
          </Link>
        }
      />

      {error ? (
        <InlineAlert
          tone="danger"
          title="自動作成を完了できませんでした"
          action={
            <Button
              size="small"
              variant="secondary"
              onClick={() => void runAutomaticFlow()}
            >
              続きから再試行
            </Button>
          }
        >
          <p>{error}</p>
        </InlineAlert>
      ) : null}

      <Card className="wizard-card template-auto-create">
        <div className="wizard-card__heading">
          <span className="wizard-number">
            <Icon name="spark" size={19} />
          </span>
          <div>
            <h2>問題用紙と解答資料を追加</h2>
            <p>
              PDF・画像をまとめて追加できます。資料の種類も自動で判定します。
            </p>
          </div>
        </div>

        <label
          className={`file-drop-zone${dragging ? " is-dragging" : ""}`}
          onDragEnter={(event) => {
            event.preventDefault();
            if (!working) setDragging(true);
          }}
          onDragOver={(event) => event.preventDefault()}
          onDragLeave={() => setDragging(false)}
          onDrop={dropFiles}
        >
          <input
            type="file"
            accept=".pdf,.jpg,.jpeg,.png,.tif,.tiff,application/pdf,image/jpeg,image/png,image/tiff"
            multiple
            onChange={addFiles}
            disabled={working}
          />
          <span className="file-drop-zone__icon">
            <Icon name={working ? "clock" : "upload"} size={27} />
          </span>
          <strong>
            {working
              ? "ファイルを処理しています"
              : "ファイルを選択、またはここにドロップ"}
          </strong>
          <small>
            {working
              ? "このまま待つと自動で編集・確認画面へ進みます"
              : "問題用紙・記入済み答案・模範解答を一度に追加できます"}
          </small>
        </label>

        {sources.length ? (
          <div className="template-auto-create__progress" aria-live="polite">
            <div>
              <span className="spinner" />
              <div>
                <strong>{workflowCopy.title}</strong>
                <small>{workflowCopy.detail}</small>
              </div>
            </div>
            {workflow === "reviewing" ? (
              <Button size="small" onClick={continueNow}>
                今すぐ続ける
              </Button>
            ) : null}
          </div>
        ) : null}

        {sources.length ? (
          <div className="source-list" aria-label="追加した資料">
            {sources.map((source) => (
              <div className="source-row source-row--automatic" key={source.id}>
                <span className="file-icon">
                  <Icon name="file" />
                </span>
                <div className="source-row__file">
                  <strong>{source.file.name}</strong>
                  <small>{(source.file.size / 1_000_000).toFixed(1)} MB</small>
                  {source.state === "uploading" ? (
                    <div
                      className="upload-progress"
                      role="progressbar"
                      aria-label={`${source.file.name}のアップロード`}
                      aria-valuenow={source.progress}
                      aria-valuemin={0}
                      aria-valuemax={100}
                    >
                      <span style={{ width: `${source.progress}%` }} />
                    </div>
                  ) : null}
                  {source.error ? (
                    <small className="field__error">{source.error}</small>
                  ) : null}
                </div>
                <label className="source-role-select">
                  <span>
                    資料の種類
                    <em>
                      {source.roleInference === "user"
                        ? "指定済み"
                        : source.roleInference === "strong"
                          ? "自動判定"
                          : "仮判定"}
                    </em>
                  </span>
                  <select
                    value={source.sourceRole}
                    disabled={
                      source.state === "attached" ||
                      [
                        "matching",
                        "creating",
                        "attaching",
                        "generating",
                      ].includes(workflow)
                    }
                    onChange={(event) =>
                      updateSource(source.id, {
                        sourceRole: event.target.value as SourceRole,
                        roleInference: "user",
                      })
                    }
                  >
                    <option value="blankTest">問題のみ（未記入）</option>
                    <option value="containsModelAnswers">
                      模範解答入り
                    </option>
                    <option value="containsNonModelAnswers">
                      記入済み答案（AIが正答を作成）
                    </option>
                    <option value="separateAnswerKey">別紙の模範解答</option>
                  </select>
                  <small>{sourceRoleHelp(source.sourceRole)}</small>
                </label>
                <span
                  className={`source-state${
                    ["uploaded", "attached"].includes(source.state)
                      ? " source-state--done"
                      : ""
                  }`}
                >
                  {["uploaded", "attached"].includes(source.state) ? (
                    <Icon name="check" size={16} />
                  ) : null}
                  {sourceStateLabel(source.state)}
                </span>
              </div>
            ))}
          </div>
        ) : null}

        {workflow === "reviewing" ? (
          <InlineAlert tone="info" title="資料の種類を確認できます">
            <p>
              上の自動判定が違う場合だけ変更してください。変更がなければ
              {reviewSeconds}秒後に自動作成を続けます。
            </p>
            <p>
              生徒などの記入済み答案は「AIが正答を作成」を選ぶと、記入内容を正解として使いません。
            </p>
          </InlineAlert>
        ) : exactMatch ? (
          <InlineAlert
            tone="success"
            title="同じ資料の公開済みひな形が見つかりました"
            action={
              <div className="button-row">
                <Button size="small" onClick={openExactMatch}>
                  既存のひな形を開く
                </Button>
                <Button
                  size="small"
                  variant="secondary"
                  onClick={createDespiteExactMatch}
                >
                  新しい下書きを作成
                </Button>
              </div>
            }
          >
            <p>
              「{exactMatch.templateTitle}」第{exactMatch.versionNumber}
              版と完全に一致します。通常は既存版をそのまま利用できます。
            </p>
          </InlineAlert>
        ) : (
          <InlineAlert
            tone={
              capabilities.status === "error" ||
              templateGeneration?.enabled === false
                ? "warning"
                : "info"
            }
            title="AIが下書きを作成します"
          >
            <p>
              {templateGeneration?.enabled === false
                ? "AI作成は管理者設定で無効です。ファイル保存後、手動編集画面を開きます。"
                : aiTemplateReady
                  ? "Geminiが問題、解答欄、配点、正解候補を抽出します。原本とAIの下書きを並べて確認できます。"
                  : capabilities.status === "loading"
                    ? "Geminiの準備状況を確認中です。利用できない場合も、同じ操作で手動編集へ進めます。"
                    : "Geminiを利用できないため、ファイル保存後に手動編集画面を開きます。"}
            </p>
          </InlineAlert>
        )}

        <details className="template-create-options">
          <summary>テスト名・教科などを指定する（省略可）</summary>
          <div className="template-create-options__fields">
            <Field
              label="テスト名"
              htmlFor="template-title"
              hint="未入力の場合は問題用紙のファイル名から作成します。"
            >
              <input
                id="template-title"
                value={metadata.title}
                disabled={["creating", "attaching", "generating"].includes(
                  workflow,
                )}
                onChange={(event) =>
                  setMetadata({ title: event.target.value }, true)
                }
                placeholder="自動判定"
              />
            </Field>
            <div className="form-grid form-grid--2">
              <Field
                label="教科"
                htmlFor="template-subject"
                hint="未選択の場合はファイル名から判定します。"
              >
                <select
                  id="template-subject"
                  value={metadata.subject}
                  disabled={["creating", "attaching", "generating"].includes(
                    workflow,
                  )}
                  onChange={(event) =>
                    setMetadata({ subject: event.target.value }, true)
                  }
                >
                  <option value="">自動判定</option>
                  <option value="自動判定中" disabled>
                    自動判定中
                  </option>
                  <option value="国語">国語</option>
                  <option value="数学">数学</option>
                  <option value="英語">英語</option>
                  <option value="理科">理科</option>
                  <option value="社会">社会</option>
                  <option value="その他">その他</option>
                </select>
              </Field>
              <Field label="カテゴリ" htmlFor="template-category">
                <input
                  id="template-category"
                  value={metadata.category}
                  disabled={["creating", "attaching", "generating"].includes(
                    workflow,
                  )}
                  onChange={(event) =>
                    setMetadata({ category: event.target.value }, true)
                  }
                  placeholder="例：単元テスト"
                />
              </Field>
              <Field label="学年" htmlFor="template-grade">
                <input
                  id="template-grade"
                  value={metadata.gradeLabel}
                  disabled={["creating", "attaching", "generating"].includes(
                    workflow,
                  )}
                  onChange={(event) =>
                    setMetadata({ gradeLabel: event.target.value }, true)
                  }
                  placeholder="例：中学2年"
                />
              </Field>
              <Field label="コース" htmlFor="template-course">
                <input
                  id="template-course"
                  value={metadata.course}
                  disabled={["creating", "attaching", "generating"].includes(
                    workflow,
                  )}
                  onChange={(event) =>
                    setMetadata({ course: event.target.value }, true)
                  }
                />
              </Field>
            </div>
            <Field
              label="問題ごとの初期配点"
              htmlFor="default-points"
              hint="自動抽出できない問題に使用します。"
            >
              <div className="input-suffix">
                <input
                  id="default-points"
                  type="number"
                  min="0.5"
                  step="0.5"
                  value={metadata.defaultPointsMilli / 1000}
                  disabled={["creating", "attaching", "generating"].includes(
                    workflow,
                  )}
                  onChange={(event) =>
                    setMetadata({
                      defaultPointsMilli: Math.round(
                        Number(event.target.value) * 1000,
                      ),
                    })
                  }
                />
                <span>点</span>
              </div>
            </Field>
          </div>
        </details>

        <details className="template-auto-create__fallback">
          <summary>AIを使わずに作成する</summary>
          <div>
            <p>
              {manualPreferred
                ? "ファイルだけを保存し、問題は手動で入力します。"
                : "AI接続を使わずに空の下書きを作る場合だけ選択してください。"}
            </p>
            <Button
              variant="secondary"
              size="small"
              onClick={working ? requestManualEditing : toggleManualPreference}
              disabled={
                ["matching", "creating", "attaching", "generating"].includes(
                  workflow,
                ) || (working && manualPreferred)
              }
            >
              {manualPreferred
                ? working
                  ? "手動編集を使用"
                  : "AI自動作成に戻す"
                : "手動入力に切り替える"}
            </Button>
          </div>
        </details>
      </Card>
    </div>
  );
}

export function metadataForCreation(
  metadata: TemplateMetadata,
  _teacherEnteredFields: ReadonlySet<TemplateMetadataTextField>,
  _aiWillGenerate: boolean,
): TemplateMetadata {
  return metadata;
}

export function inferTemplateMetadata(files: File[]) {
  const preferred =
    files.find((file) => !isLikelyAnswerMaterial(file.name)) || files[0];
  const rawTitle = preferred?.name.replace(/\.[^.]+$/u, "") || "新しいテスト";
  const normalizedTitle = rawTitle
    .replace(/[_-]+/gu, " ")
    .replace(/\s+/gu, " ")
    .trim();
  const title = isGenericScannerTitle(normalizedTitle)
    ? "新しいテスト"
    : normalizedTitle || "新しいテスト";
  const combined = files.map((file) => file.name).join(" ");
  const subject = inferSubject(combined);
  return { title, subject };
}

export function inferSourceRole(file: File, _allFiles: File[]) {
  if (EXPLICIT_NON_MODEL_ANSWER_PATTERN.test(file.name)) {
    return {
      role: "containsNonModelAnswers" as const,
      confidence: "strong" as const,
    };
  }
  if (EMBEDDED_ANSWER_PATTERN.test(file.name)) {
    return {
      role: "containsModelAnswers" as const,
      confidence: "strong" as const,
    };
  }
  if (FILLED_ANSWER_PATTERN.test(file.name)) {
    return {
      role: "containsNonModelAnswers" as const,
      confidence: "strong" as const,
    };
  }
  if (isLikelyAnswerKey(file.name)) {
    return {
      role: "separateAnswerKey" as const,
      confidence: "strong" as const,
    };
  }
  return { role: "blankTest" as const, confidence: "default" as const };
}

function isLikelyAnswerMaterial(fileName: string) {
  return (
    EMBEDDED_ANSWER_PATTERN.test(fileName) ||
    EXPLICIT_NON_MODEL_ANSWER_PATTERN.test(fileName) ||
    FILLED_ANSWER_PATTERN.test(fileName) ||
    isLikelyAnswerKey(fileName)
  );
}

export function sourceRoleHelp(role: SourceRole) {
  switch (role) {
    case "blankTest":
      return "AIが問題を解いて正解候補を作ります。";
    case "containsModelAnswers":
      return "用紙にある答えを模範解答として使います。";
    case "containsNonModelAnswers":
      return "記入された答えは正解として使わず、AIが印刷された問題を独自に解いて正答候補を作ります。";
    case "separateAnswerKey":
      return "別紙にある答えを模範解答として使います。";
  }
}

function isLikelyAnswerKey(fileName: string) {
  if (ANSWER_KEY_PATTERN.test(fileName)) return true;
  if (PLAIN_ANSWER_SHEET_PATTERN.test(fileName)) return false;
  const stem = fileName.replace(/\.[^.]+$/u, "").trim();
  return /(?:^|[\s_-])解答$/u.test(stem);
}

function inferSubject(value: string) {
  if (/(国語|漢字|現代文|古文|作文|読解)/u.test(value)) return "国語";
  if (/(数学|算数|計算|方程式|図形)/u.test(value)) return "数学";
  if (/(英語|英文|英単語|english)/iu.test(value)) return "英語";
  if (/(理科|物理|化学|生物|地学|science)/iu.test(value)) return "理科";
  if (/(社会|地理|歴史|公民|地図|geography|history)/iu.test(value)) {
    return "社会";
  }
  return "自動判定中";
}

function isGenericScannerTitle(value: string) {
  return (
    /^(?:scan(?:ned)?|scanner|image|img|document|adobe scan|camscanner|無題|名称未設定)\s*\d*$/iu.test(
      value,
    ) || /^[\d\s./]+$/u.test(value)
  );
}

function sourceStateLabel(state: PendingSource["state"]) {
  switch (state) {
    case "ready":
      return "待機中";
    case "uploading":
      return "アップロード中";
    case "uploaded":
      return "アップロード済み";
    case "attached":
      return "保存済み";
    case "failed":
      return "失敗";
  }
}

function workflowStatus(state: WorkflowState, seconds: number) {
  switch (state) {
    case "uploading":
      return {
        title: "資料をアップロードしています",
        detail: "完了後、資料の種類を確認して自動作成します。",
      };
    case "reviewing":
      return {
        title: "資料を自動判定しました",
        detail: `${seconds}秒後に下書き作成を続けます。違う場合だけ修正してください。`,
      };
    case "matching":
      return {
        title: "同じ資料のひな形を探しています",
        detail: "登録済みの場合は重複した下書きを作らず再利用できます。",
      };
    case "creating":
      return {
        title: "テストの基本情報を作成しています",
        detail: "テスト名と教科はファイル名から入力しました。",
      };
    case "attaching":
      return {
        title: "資料をひな形に登録しています",
        detail: "登録後、選択した方法で編集・確認画面へ進みます。",
      };
    case "generating":
      return {
        title: "AI下書き作成を開始しています",
        detail: "編集画面では原本とAIの下書きを並べて確認できます。",
      };
    case "failed":
      return {
        title: "処理を中断しました",
        detail: "エラーを確認し、続きから再試行してください。",
      };
    default:
      return { title: "", detail: "" };
  }
}

function errorMessage(reason: unknown, fallback: string) {
  if (reason instanceof ApiError) {
    return reason.problem.errors?.[0]?.message || reason.message;
  }
  return reason instanceof Error ? reason.message : fallback;
}

import { useState, type ChangeEvent, type DragEvent } from "react";
import { Icon } from "../components/Icon";
import { Button, Card, Field, InlineAlert, PageHeader } from "../components/ui";
import { ApiError, newIdempotencyKey, uploadFile } from "../lib/api";
import {
  answerStyleLabel,
  deterministicPlanMessage,
  pageRangeLabel,
  templateGenerationApi,
  testTypeLabel,
  warningMessage,
} from "../lib/templateGeneration";
import { Link, useNavigate } from "../router";
import type {
  TemplateGenerationAnswerStyle,
  TemplateGenerationBatch,
  TemplateGenerationSubject,
  TemplateGenerationTestType,
  UploadFinalizeResponse,
} from "../types";

type TestTypeSelection = TemplateGenerationTestType | "";
type SubjectSelection = TemplateGenerationSubject | "";
type AnswerStyleSelection = TemplateGenerationAnswerStyle | "";
type CreatePhase =
  | "idle"
  | "uploading"
  | "planning"
  | "ready"
  | "starting"
  | "cancelling";

interface UploadedPdf {
  file: File;
  uploadId: string;
  sourceRowVersion?: number;
}

interface PendingBatchCancellation {
  batch: TemplateGenerationBatch;
  idempotencyKey: string;
}

const TEST_TYPES: Array<{
  value: TemplateGenerationTestType;
  label: string;
  description: string;
}> = [
  {
    value: "hop",
    label: "HOP",
    description: "1ページを1件の独立したテストとして作成します。",
  },
  {
    value: "step",
    label: "STEP",
    description: "2ページを1件とし、6ページごとに3種類を作成します。",
  },
  {
    value: "classPlacement",
    label: "クラス分けテスト",
    description: "PDF全体を分割せず、1件のテストとして作成します。",
  },
  {
    value: "other",
    label: "その他",
    description: "PDF全体を1件として、通常または穴埋めで作成します。",
  },
];

const SUBJECTS: TemplateGenerationSubject[] = ["算数", "国語", "理科", "社会"];

export function TemplateCreatePage() {
  const navigate = useNavigate();
  const [testType, setTestType] = useState<TestTypeSelection>("");
  const [subject, setSubject] = useState<SubjectSelection>("");
  const [answerStyle, setAnswerStyle] = useState<AnswerStyleSelection>("");
  const [uploaded, setUploaded] = useState<UploadedPdf>();
  const [batch, setBatch] = useState<TemplateGenerationBatch>();
  const [phase, setPhase] = useState<CreatePhase>("idle");
  const [uploadProgress, setUploadProgress] = useState(0);
  const [dragging, setDragging] = useState(false);
  const [needsReplan, setNeedsReplan] = useState(false);
  const [pendingCancellation, setPendingCancellation] =
    useState<PendingBatchCancellation>();
  const [error, setError] = useState<string>();

  const settingsValid =
    Boolean(testType && subject) &&
    (testType !== "other" || Boolean(answerStyle));
  const busy =
    ["uploading", "planning", "starting", "cancelling"].includes(phase) ||
    Boolean(pendingCancellation);

  function invalidatePlan() {
    if (!uploaded) return;
    const invalidatedBatch = batch;
    setBatch(undefined);
    setNeedsReplan(true);
    setError(undefined);
    if (invalidatedBatch) {
      queueBatchCancellation(invalidatedBatch);
    } else {
      setPhase("idle");
    }
  }

  function queueBatchCancellation(invalidatedBatch: TemplateGenerationBatch) {
    const pending = {
      batch: invalidatedBatch,
      idempotencyKey: newIdempotencyKey(),
    };
    setPendingCancellation(pending);
    setPhase("cancelling");
    void cancelInvalidatedBatch(pending);
  }

  async function cancelInvalidatedBatch(
    pending = pendingCancellation,
  ) {
    if (!pending) return;
    setPhase("cancelling");
    setError(undefined);
    try {
      await templateGenerationApi.cancelBatch(
        pending.batch.batchId,
        pending.batch.rowVersion,
        pending.idempotencyKey,
      );
      setPendingCancellation((current) =>
        current?.batch.batchId === pending.batch.batchId ? undefined : current,
      );
      setPhase("idle");
    } catch (reason) {
      setPhase("idle");
      setError(
        `変更前の作成予定を取り消せませんでした。${createErrorMessage(reason)}`,
      );
    }
  }

  function changeTestType(value: TestTypeSelection) {
    if (value === testType) return;
    setTestType(value);
    if (value !== "other") setAnswerStyle("");
    invalidatePlan();
  }

  function changeSubject(value: SubjectSelection) {
    if (value === subject) return;
    setSubject(value);
    invalidatePlan();
  }

  function changeAnswerStyle(value: AnswerStyleSelection) {
    if (value === answerStyle) return;
    setAnswerStyle(value);
    invalidatePlan();
  }

  function chooseFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (file) void beginUpload(file);
  }

  function dropFile(event: DragEvent<HTMLLabelElement>) {
    event.preventDefault();
    setDragging(false);
    if (busy) return;
    const file = event.dataTransfer.files?.[0];
    if (file) void beginUpload(file);
  }

  async function beginUpload(file: File) {
    if (!settingsValid || busy) return;
    if (!isPdf(file)) {
      setError("PDFファイルを1件選択してください。");
      return;
    }

    setError(undefined);
    setBatch(undefined);
    setUploaded(undefined);
    setNeedsReplan(false);
    setUploadProgress(0);
    setPhase("uploading");

    try {
      const finalized = await uploadFile(file, {
        purpose: "templateSource",
        onProgress: (uploadedBytes, totalBytes) =>
          setUploadProgress(
            totalBytes ? Math.round((uploadedBytes / totalBytes) * 100) : 0,
          ),
      });
      const source = uploadedPdf(file, finalized);
      setUploaded(source);
      setUploadProgress(100);
      await planSource(source);
    } catch (reason) {
      setPhase("idle");
      setError(createErrorMessage(reason));
    }
  }

  async function planSource(source = uploaded) {
    if (!source || !settingsValid || !testType || !subject) return;
    setError(undefined);
    setPhase("planning");

    try {
      const planned = await templateGenerationApi.createBatch({
        sourceId: source.uploadId,
        testType,
        subject,
        answerStyle: testType === "other" ? answerStyle || null : null,
        ...(source.sourceRowVersion !== undefined
          ? { expectedSourceRowVersion: source.sourceRowVersion }
          : {}),
      });
      setBatch(planned);
      setNeedsReplan(false);
      setPhase("ready");
    } catch (reason) {
      setPhase("idle");
      setNeedsReplan(true);
      setError(createErrorMessage(reason));
    }
  }

  async function startGeneration() {
    if (!batch || phase === "starting") return;
    setError(undefined);
    setPhase("starting");
    try {
      await templateGenerationApi.startGeneration(
        batch.batchId,
        batch.rowVersion,
      );
      navigate(
        `/templates/generation/${encodeURIComponent(batch.batchId)}`,
      );
    } catch (reason) {
      setPhase("ready");
      setError(createErrorMessage(reason));
    }
  }

  function removeSource() {
    const invalidatedBatch = batch;
    setUploaded(undefined);
    setBatch(undefined);
    setNeedsReplan(false);
    setUploadProgress(0);
    setError(undefined);
    if (invalidatedBatch) {
      queueBatchCancellation(invalidatedBatch);
    } else {
      setPhase("idle");
    }
  }

  return (
    <div className="page template-generation-page">
      <PageHeader
        eyebrow="テンプレート作成"
        title="テストからテンプレートを作成"
        description="試験タイプと教科を先に指定し、PDFを決まったルールで分割して作成します。"
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
          title="作成を続けられません"
          action={
            pendingCancellation ? (
              <Button
                size="small"
                onClick={() => void cancelInvalidatedBatch()}
                disabled={phase === "cancelling"}
              >
                変更前の計画を取り消す
              </Button>
            ) : undefined
          }
        >
          <p>{error}</p>
        </InlineAlert>
      ) : null}

      <ol className="template-generation-steps" aria-label="作成手順">
        <li className="is-active">1. テスト設定</li>
        <li className={settingsValid ? "is-active" : ""}>2. PDF</li>
        <li className={batch ? "is-active" : ""}>3. 作成予定</li>
        <li>4. 生成</li>
      </ol>

      <Card className="wizard-card template-generation-card">
        <div className="wizard-card__heading">
          <span className="wizard-number">1</span>
          <div>
            <h2>テスト設定</h2>
            <p>ここで選んだ内容を使います。PDFから自動判定はしません。</p>
          </div>
        </div>

        <div className="template-settings-grid">
          <Field
            label="試験タイプ"
            htmlFor="template-test-type"
            required
            hint="HOPとSTEPの分割方法は固定されています。"
          >
            <select
              id="template-test-type"
              aria-label="試験タイプ"
              value={testType}
              disabled={busy}
              onChange={(event) =>
                changeTestType(event.target.value as TestTypeSelection)
              }
            >
              <option value="">選択してください</option>
              {TEST_TYPES.map((option) => (
                <option value={option.value} key={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </Field>

          <Field label="教科" htmlFor="template-subject" required>
            <select
              id="template-subject"
              aria-label="教科"
              value={subject}
              disabled={busy}
              onChange={(event) =>
                changeSubject(event.target.value as SubjectSelection)
              }
            >
              <option value="">選択してください</option>
              {SUBJECTS.map((value) => (
                <option value={value} key={value}>
                  {value}
                </option>
              ))}
            </select>
          </Field>

          {testType === "other" ? (
            <Field
              label="問題形式"
              htmlFor="template-answer-style"
              required
              hint="「その他」を選んだ場合だけ指定します。"
            >
              <select
                id="template-answer-style"
                aria-label="問題形式"
                value={answerStyle}
                disabled={busy}
                onChange={(event) =>
                  changeAnswerStyle(
                    event.target.value as AnswerStyleSelection,
                  )
                }
              >
                <option value="">選択してください</option>
                <option value="normal">通常</option>
                <option value="fillBlank">穴埋め</option>
              </select>
            </Field>
          ) : null}
        </div>

        {testType ? (
          <div className="template-setting-note">
            <strong>{testTypeLabel(testType)}</strong>
            <span>
              {TEST_TYPES.find((option) => option.value === testType)?.description}
            </span>
            {testType === "other" && answerStyle ? (
              <small>問題形式：{answerStyleLabel(answerStyle)}</small>
            ) : null}
          </div>
        ) : null}
      </Card>

      {settingsValid ? (
        <Card className="wizard-card template-generation-card">
          <div className="wizard-card__heading">
            <span className="wizard-number">2</span>
            <div>
              <h2>PDFをアップロード</h2>
              <p>1つのPDF内だけでページを分割します。</p>
            </div>
          </div>

          {!uploaded ? (
            <label
              className={`file-drop-zone${dragging ? " is-dragging" : ""}`}
              onDragEnter={(event) => {
                event.preventDefault();
                if (!busy) setDragging(true);
              }}
              onDragOver={(event) => event.preventDefault()}
              onDragLeave={() => setDragging(false)}
              onDrop={dropFile}
            >
              <input
                type="file"
                accept=".pdf,application/pdf"
                onChange={chooseFile}
                disabled={busy}
              />
              <span className="file-drop-zone__icon">
                <Icon name={phase === "uploading" ? "clock" : "upload"} size={27} />
              </span>
              <strong>
                {phase === "uploading"
                  ? "PDFをアップロードしています"
                  : "PDFを選択、またはここにドロップ"}
              </strong>
              <small>PDF 1件のみ。STEPは6の倍数ページが必要です。</small>
              {phase === "uploading" ? (
                <div
                  className="upload-progress template-upload-progress"
                  role="progressbar"
                  aria-label="PDFのアップロード"
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={uploadProgress}
                >
                  <span style={{ width: `${uploadProgress}%` }} />
                </div>
              ) : null}
            </label>
          ) : (
            <div className="template-source-summary">
              <span className="file-icon">
                <Icon name="file" />
              </span>
              <div>
                <strong>{uploaded.file.name}</strong>
                <small>{(uploaded.file.size / 1_000_000).toFixed(1)} MB</small>
              </div>
              {batch ? (
                <span>
                  {batch.sourcePageCount}ページ
                  <Icon name="check" size={16} />
                </span>
              ) : (
                <span>
                  {phase === "planning"
                    ? "ページを確認中"
                    : phase === "cancelling"
                      ? "変更前の計画を取消中"
                      : "計画待ち"}
                </span>
              )}
              <Button
                type="button"
                size="small"
                variant="quiet"
                disabled={busy}
                onClick={removeSource}
              >
                PDFを変更
              </Button>
            </div>
          )}

          {phase === "planning" ? (
            <div className="template-generation-working" role="status">
              <span className="spinner" aria-hidden="true" />
              <div>
                <strong>PDFを確認し、分割計画を作成しています</strong>
                <small>この段階ではAI生成を開始しません。</small>
              </div>
            </div>
          ) : null}

          {phase === "cancelling" ? (
            <div className="template-generation-working" role="status">
              <span className="spinner" aria-hidden="true" />
              <div>
                <strong>変更前の作成予定を取り消しています</strong>
                <small>完了後、新しい設定で再計画できます。</small>
              </div>
            </div>
          ) : null}

          {uploaded && needsReplan && phase !== "planning" ? (
            <InlineAlert
              tone="warning"
              title="設定に合わせて作成予定を更新してください"
              action={
                <Button
                  type="button"
                  size="small"
                  onClick={() => void planSource()}
                  disabled={!settingsValid || busy || Boolean(pendingCancellation)}
                >
                  この設定で再計画
                </Button>
              }
            >
              <p>変更前の分割計画は使用しません。</p>
            </InlineAlert>
          ) : null}
        </Card>
      ) : (
        <InlineAlert tone="info" title="先にテスト設定を選択してください">
          <p>
            必須項目が決まるとPDFのアップロード欄が表示されます。学年は生成後の最終確認で必要な場合だけ選びます。
          </p>
        </InlineAlert>
      )}

      {batch ? (
        <Card className="wizard-card template-generation-card">
          <div className="wizard-card__heading">
            <span className="wizard-number">3</span>
            <div>
              <h2>作成予定を確認</h2>
              <p>ページ範囲とSTEPの枝番は固定され、編集できません。</p>
            </div>
          </div>

          <InlineAlert tone="success" title="分割計画を確認しました">
            <p>
              {deterministicPlanMessage(
                batch.testType,
                batch.expectedUnitCount,
              )}
            </p>
          </InlineAlert>

          <dl className="template-plan-summary">
            <div>
              <dt>試験タイプ</dt>
              <dd>{testTypeLabel(batch.testType)}</dd>
            </div>
            <div>
              <dt>教科</dt>
              <dd>{batch.subject}</dd>
            </div>
            {batch.testType === "other" ? (
              <div>
                <dt>問題形式</dt>
                <dd>{answerStyleLabel(batch.answerStyle)}</dd>
              </div>
            ) : null}
            <div>
              <dt>元PDF</dt>
              <dd>{batch.sourcePageCount}ページ</dd>
            </div>
            <div>
              <dt>作成予定</dt>
              <dd>{batch.expectedUnitCount}件</dd>
            </div>
          </dl>

          <div className="template-plan-units" aria-label="固定されたページ範囲">
            {batch.units.map((unit) => (
              <div key={`${unit.sequence}-${unit.firstPage}`}>
                <span>{unit.sequence}</span>
                <strong>{pageRangeLabel(unit)}</strong>
                {unit.stepSetIndex ? (
                  <small>セット {unit.stepSetIndex}</small>
                ) : null}
                {unit.deterministicSuffix ? (
                  <em>{unit.deterministicSuffix}</em>
                ) : null}
              </div>
            ))}
          </div>

          <div className="template-generation-actions">
            <p>
              開始後、各テンプレートの向きを確認しながら問題・正答・配点を生成します。
            </p>
            <Button
              type="button"
              size="large"
              leadingIcon="spark"
              disabled={phase === "starting"}
              onClick={() => void startGeneration()}
            >
              {phase === "starting" ? "生成を開始しています" : "テンプレート生成を開始"}
            </Button>
          </div>
        </Card>
      ) : null}
    </div>
  );
}

function uploadedPdf(
  file: File,
  response: UploadFinalizeResponse,
): UploadedPdf {
  return {
    file,
    uploadId: response.uploadId,
    sourceRowVersion: response.rowVersion ?? response.revision,
  };
}

function isPdf(file: File) {
  return file.type === "application/pdf" || /\.pdf$/iu.test(file.name);
}

function createErrorMessage(reason: unknown) {
  if (reason instanceof ApiError) {
    const code =
      reason.problem.code || reason.problem.errors?.find((item) => item.code)?.code;
    if (code) return warningMessage(code);
    return (
      reason.problem.errors?.[0]?.message ||
      reason.message ||
      "テンプレートの作成を開始できませんでした。"
    );
  }
  return reason instanceof Error
    ? reason.message
    : "テンプレートの作成を開始できませんでした。";
}

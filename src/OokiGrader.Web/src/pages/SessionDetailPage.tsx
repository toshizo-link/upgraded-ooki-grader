import {
  useCallback,
  useRef,
  useState,
  type DragEvent,
  type ChangeEvent,
} from "react";
import { Link, useParams } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon } from "../components/Icon";
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  InlineAlert,
  LoadingState,
  Modal,
  PageHeader,
  SearchInput,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import {
  ApiError,
  api,
  asPaged,
  newIdempotencyKey,
  uploadFile,
} from "../lib/api";
import {
  classNames,
  formatDate,
  formatDateTime,
  formatPercentageBasisPoints,
  formatPoints,
} from "../lib/format";
import { submissionWorkflowHref } from "../lib/submissionNavigation";
import type {
  PagedResponse,
  SubmissionSummary,
  TestSessionSummary,
  UploadFinalizeResponse,
} from "../types";

interface LocalUpload {
  id: string;
  file: File;
  progress: number;
  state: "ready" | "uploading" | "completed" | "failed" | "duplicate";
  message?: string;
  submissionId?: string;
  duplicateUploadId?: string;
  existingSubmissionId?: string;
}

interface SessionSummary {
  submissionCount: number;
  stateCounts?: Record<string, number>;
  finalizedCount: number;
  attentionCount: number;
  estimatedCostUsd?: number;
}

interface OperatorStatusSummary {
  totalCount: number;
  uploadingCount: number;
  processingCount: number;
  attentionCount: number;
  readyCount: number;
}

interface SubmissionStatusResponse extends PagedResponse<SubmissionSummary> {
  summary?: OperatorStatusSummary;
}

const maximumUploadBytes = 250_000_000;
const supportedUploadTypes = new Set([
  "application/pdf",
  "image/jpeg",
  "image/png",
  "image/tiff",
]);
const supportedUploadExtensions = new Set([
  ".pdf",
  ".jpg",
  ".jpeg",
  ".png",
  ".tif",
  ".tiff",
]);

function canUpload(file: File) {
  const extension = file.name
    .slice(file.name.lastIndexOf("."))
    .toLocaleLowerCase("en-US");
  return (
    file.size <= maximumUploadBytes &&
    (supportedUploadTypes.has(file.type) ||
      supportedUploadExtensions.has(extension))
  );
}

export function SessionDetailPage() {
  const { sessionId = "" } = useParams();
  const { hasAnyRole } = useSession();
  const canManageSession = hasAnyRole("administrator", "teacher");
  const scanOperatorOnly =
    hasAnyRole("scanOperator") && !canManageSession;
  const [search, setSearch] = useState("");
  const [stateFilter, setStateFilter] = useState("all");
  const [uploads, setUploads] = useState<LocalUpload[]>([]);
  const [dragging, setDragging] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [resolvingUploadId, setResolvingUploadId] = useState<string>();
  const [sessionStateWorking, setSessionStateWorking] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const [closeOpen, setCloseOpen] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const session = useApiQuery<TestSessionSummary>(
    `session:${sessionId}`,
    (signal) =>
      api.get(
        `/test-sessions/${encodeURIComponent(sessionId)}`,
        undefined,
        signal,
      ),
    Boolean(sessionId),
  );
  const summary = useApiQuery<SessionSummary>(
    `session-summary:${sessionId}`,
    (signal) =>
      api.get(
        `/test-sessions/${encodeURIComponent(sessionId)}/summary`,
        undefined,
        signal,
      ),
    Boolean(sessionId) && !scanOperatorOnly,
  );
  const submissions = useApiQuery<SubmissionStatusResponse>(
    `session-submissions:${scanOperatorOnly}:${sessionId}:${stateFilter}:${search}`,
    async (signal) => {
      const response = await api.get<SubmissionStatusResponse>(
        scanOperatorOnly
          ? `/test-sessions/${encodeURIComponent(sessionId)}/upload-status`
          : "/submissions",
        scanOperatorOnly
          ? {
              state: stateFilter === "all" ? undefined : stateFilter,
              search: search || undefined,
              pageSize: 200,
            }
          : {
              testSessionId: sessionId,
              state: stateFilter === "all" ? undefined : stateFilter,
              search: search || undefined,
              pageSize: 200,
            },
        signal,
      );
      return asPaged(response) as SubmissionStatusResponse;
    },
    Boolean(sessionId),
  );

  const updateUpload = useCallback(
    (id: string, changes: Partial<LocalUpload>) => {
      setUploads((current) =>
        current.map((item) =>
          item.id === id ? { ...item, ...changes } : item,
        ),
      );
    },
    [],
  );

  function addFiles(files: File[]) {
    const accepted = files.filter(canUpload);
    const rejected = files.filter((file) => !canUpload(file));
    if (rejected.length) {
      setActionError(
        `${rejected.map((file) => file.name).join("、")} は追加できません。PDF・JPEG・PNG・TIFF（1ファイル250 MB以下）を選択してください。`,
      );
    } else {
      setActionError(undefined);
    }
    setUploads((current) => [
      ...current,
      ...accepted.map((file) => ({
        id: crypto.randomUUID(),
        file,
        progress: 0,
        state: "ready" as const,
      })),
    ]);
  }

  function handleFileInput(event: ChangeEvent<HTMLInputElement>) {
    addFiles(Array.from(event.target.files || []));
    event.target.value = "";
  }

  function handleDrop(event: DragEvent) {
    event.preventDefault();
    setDragging(false);
    addFiles(Array.from(event.dataTransfer.files));
  }

  async function startUploads() {
    const pending = uploads.filter(
      (item) => item.state === "ready" || item.state === "failed",
    );
    if (!pending.length) return;
    setUploading(true);
    setActionError(undefined);
    let cursor = 0;

    async function worker() {
      while (cursor < pending.length) {
        const item = pending[cursor++];
        if (!item) return;
        updateUpload(item.id, {
          state: "uploading",
          progress: 0,
          message: undefined,
        });
        try {
          const result = await uploadFile(item.file, {
            purpose: "completedTest",
            testSessionId: sessionId,
            onProgress: (uploaded, total) =>
              updateUpload(item.id, {
                progress: total ? Math.round((uploaded / total) * 100) : 0,
              }),
          });
          updateUpload(item.id, {
            state: "completed",
            progress: 100,
            submissionId: result.submissionId,
          });
        } catch (reason) {
          const duplicate =
            reason instanceof ApiError &&
            (reason.problem.code === "DUPLICATE_UPLOAD" ||
              reason.problem.code === "EXACT_DUPLICATE");
          updateUpload(item.id, {
            state: duplicate ? "duplicate" : "failed",
            message:
              reason instanceof Error
                ? reason.message
                : "アップロードできませんでした。",
            duplicateUploadId:
              reason instanceof ApiError
                ? reason.problem.uploadId
                : undefined,
            existingSubmissionId:
              reason instanceof ApiError
                ? reason.problem.existingSubmissionId
                : undefined,
          });
        }
      }
    }

    await Promise.all(
      Array.from({ length: Math.min(3, pending.length) }, () => worker()),
    );
    setUploading(false);
    submissions.reload();
    if (!scanOperatorOnly) summary.reload();
  }

  async function resolveDuplicate(
    item: LocalUpload,
    action: "useExisting" | "createAttempt" | "cancel",
  ) {
    if (!item.duplicateUploadId) return;
    setResolvingUploadId(item.id);
    setActionError(undefined);
    try {
      const result = await api.post<UploadFinalizeResponse | undefined>(
        `/uploads/${encodeURIComponent(item.duplicateUploadId)}:resolveDuplicate`,
        { action },
        { idempotencyKey: newIdempotencyKey() },
      );
      if (action === "cancel") {
        setUploads((current) =>
          current.filter((upload) => upload.id !== item.id),
        );
      } else {
        updateUpload(item.id, {
          state: "completed",
          progress: 100,
          submissionId:
            result?.submissionId || item.existingSubmissionId,
          message:
            action === "useExisting"
              ? "既存の答案として処理済みにしました。"
              : "別の受験回として追加しました。生徒名の確認が必要です。",
        });
      }
      submissions.reload();
      if (!scanOperatorOnly) summary.reload();
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "重複答案の扱いを保存できませんでした。",
      );
    } finally {
      setResolvingUploadId(undefined);
    }
  }

  async function toggleSessionState() {
    if (!session.data || sessionStateWorking) return;
    setSessionStateWorking(true);
    setActionError(undefined);
    const action = session.data.state === "open" ? "close" : "open";
    try {
      await api.post(
        `/test-sessions/${encodeURIComponent(sessionId)}:${action}`,
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      setCloseOpen(false);
      session.reload();
      if (!scanOperatorOnly) summary.reload();
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "受付状態を変更できませんでした。",
      );
    } finally {
      setSessionStateWorking(false);
    }
  }

  if (session.status === "loading") {
    return (
      <div className="page">
        <LoadingState label="テスト実施を読み込んでいます" />
      </div>
    );
  }
  if (session.status === "error" || !session.data) {
    return (
      <div className="page">
        <ErrorState error={session.error} onRetry={session.reload} />
      </div>
    );
  }

  const data = session.data;
  const isOpen = data.state === "open";
  const localDone = uploads.filter((item) => item.state === "completed").length;
  const operatorSummary = submissions.data?.summary;

  return (
    <div className="page session-detail-page">
      <PageHeader
        eyebrow={`${formatDate(data.testDate)} 実施`}
        title={
          data.sessionName ||
          data.name ||
          data.templateTitle ||
          "テスト実施"
        }
        description={
          [
            data.templateTitle,
            data.templateVersionNumber
              ? `第${data.templateVersionNumber}版`
              : undefined,
            data.classLabel,
            data.course,
          ]
            .filter(Boolean)
            .join("・") || "詳細未設定"
        }
        backAction={
          <Link className="back-link" to="/sessions">
            <Icon name="arrowLeft" size={17} />
            テスト実施一覧へ
          </Link>
        }
        actions={
          <>
            <StatusBadge status={data.state} />
            {canManageSession ? (
              <Button
                variant="secondary"
                disabled={sessionStateWorking}
                onClick={() => {
                  if (isOpen) {
                    setActionError(undefined);
                    setCloseOpen(true);
                  } else {
                    void toggleSessionState();
                  }
                }}
              >
                {sessionStateWorking
                  ? "変更しています…"
                  : isOpen
                    ? "受付を終了"
                    : "受付を再開"}
              </Button>
            ) : null}
          </>
        }
      />
      {actionError && !closeOpen ? (
        <InlineAlert tone="danger">
          <p>{actionError}</p>
        </InlineAlert>
      ) : null}

      {!scanOperatorOnly && summary.status === "error" ? (
        <ErrorState error={summary.error} onRetry={summary.reload} compact />
      ) : (
        <section className="session-summary-grid" aria-label="答案の状況">
          {scanOperatorOnly ? (
            <>
              <SummaryMetric
                label="受信した答案"
                value={operatorSummary?.totalCount}
                suffix="答案"
                icon="upload"
                loading={submissions.status === "loading"}
              />
              <SummaryMetric
                label="送信・画像処理中"
                value={
                  (operatorSummary?.uploadingCount || 0) +
                  (operatorSummary?.processingCount || 0)
                }
                suffix="件"
                icon="clock"
                loading={submissions.status === "loading"}
              />
              <SummaryMetric
                label="AI処理または画像の確認"
                value={operatorSummary?.attentionCount}
                suffix="件"
                icon="alert"
                loading={submissions.status === "loading"}
                tone="warning"
              />
              <SummaryMetric
                label="受信処理済み"
                value={operatorSummary?.readyCount}
                suffix="答案"
                icon="check"
                loading={submissions.status === "loading"}
                tone="success"
              />
            </>
          ) : (
            <>
              <SummaryMetric
                label="アップロード"
                value={summary.data?.submissionCount}
                suffix="答案"
                icon="upload"
                loading={summary.status === "loading"}
              />
              <SummaryMetric
                label="確認が必要"
                value={summary.data?.attentionCount}
                suffix="件"
                icon="alert"
                loading={summary.status === "loading"}
                tone="warning"
              />
              <SummaryMetric
                label="確定済み"
                value={summary.data?.finalizedCount}
                suffix="答案"
                icon="check"
                loading={summary.status === "loading"}
                tone="success"
              />
            </>
          )}
        </section>
      )}

      <Card className="upload-board">
        <div className="card__header">
          <div>
            <h2>答案をアップロード</h2>
            <p>
              複数ファイルをまとめて選択できます。画面を閉じても、送信済みの処理は続きます。
            </p>
          </div>
          {uploads.length ? (
            <Badge tone="neutral">
              {localDone} / {uploads.length}件完了
            </Badge>
          ) : null}
        </div>
        {!isOpen ? (
          <InlineAlert tone="warning" title="答案の受付は終了しています">
            <p>
              {canManageSession
                ? "アップロードするには、右上の「受付を再開」を選択してください。"
                : "受付の再開が必要な場合は、先生に連絡してください。"}
            </p>
          </InlineAlert>
        ) : (
          <>
            <div
              className={classNames(
                "file-drop-zone",
                "file-drop-zone--session",
                dragging && "is-dragging",
              )}
              onDragOver={(event) => {
                event.preventDefault();
                setDragging(true);
              }}
              onDragLeave={() => setDragging(false)}
              onDrop={handleDrop}
            >
              <input
                ref={fileInputRef}
                type="file"
                accept=".pdf,.jpg,.jpeg,.png,.tif,.tiff,application/pdf,image/jpeg,image/png,image/tiff"
                multiple
                onChange={handleFileInput}
                disabled={uploading}
              />
              <span className="file-drop-zone__icon">
                <Icon name="upload" size={28} />
              </span>
              <strong>答案をここにドロップ</strong>
              <span>または</span>
              <Button
                type="button"
                variant="secondary"
                onClick={() => fileInputRef.current?.click()}
                disabled={uploading}
              >
                ファイルを選択
              </Button>
              <small>PDF・JPEG・PNG・TIFF / 1ファイル250 MBまで</small>
            </div>
            {uploads.length ? (
              <div className="local-upload-list">
                {uploads.map((item) => (
                  <div
                    className={classNames(
                      "local-upload-row",
                      `local-upload-row--${item.state}`,
                    )}
                    key={item.id}
                  >
                    <span className="file-icon">
                      <Icon name="file" />
                    </span>
                    <div className="local-upload-row__copy">
                      <strong>{item.file.name}</strong>
                      <span>
                        {(item.file.size / 1_000_000).toFixed(1)} MB
                        {item.state === "uploading"
                          ? `・${item.progress}%`
                          : ""}
                      </span>
                      {item.state === "uploading" ? (
                        <div
                          className="upload-progress"
                          role="progressbar"
                          aria-valuenow={item.progress}
                          aria-valuemin={0}
                          aria-valuemax={100}
                        >
                          <span style={{ width: `${item.progress}%` }} />
                        </div>
                      ) : null}
                      {item.message ? <small>{item.message}</small> : null}
                      {item.state === "duplicate" &&
                      item.duplicateUploadId ? (
                        <div className="duplicate-upload-actions">
                          <Button
                            size="small"
                            variant="secondary"
                            disabled={resolvingUploadId === item.id}
                            onClick={() =>
                              void resolveDuplicate(item, "useExisting")
                            }
                          >
                            既存の答案を使用
                          </Button>
                          {canManageSession ? (
                            <Button
                              size="small"
                              variant="quiet"
                              disabled={resolvingUploadId === item.id}
                              onClick={() =>
                                void resolveDuplicate(item, "createAttempt")
                              }
                            >
                              別の受験回として追加
                            </Button>
                          ) : null}
                          <Button
                            size="small"
                            variant="quiet"
                            disabled={resolvingUploadId === item.id}
                            onClick={() =>
                              void resolveDuplicate(item, "cancel")
                            }
                          >
                            取消
                          </Button>
                        </div>
                      ) : null}
                    </div>
                    <UploadState state={item.state} />
                    {!uploading &&
                    item.state !== "completed" &&
                    item.state !== "duplicate" ? (
                      <button
                        type="button"
                        aria-label={`${item.file.name}を一覧から削除`}
                        onClick={() =>
                          setUploads((current) =>
                            current.filter((upload) => upload.id !== item.id),
                          )
                        }
                      >
                        <Icon name="close" size={17} />
                      </button>
                    ) : null}
                  </div>
                ))}
                <div className="upload-list-actions">
                  <Button
                    variant="secondary"
                    onClick={() => fileInputRef.current?.click()}
                    disabled={uploading}
                    leadingIcon="plus"
                  >
                    ファイルを追加
                  </Button>
                  <Button
                    onClick={() => void startUploads()}
                    disabled={
                      uploading ||
                      !uploads.some(
                        (item) =>
                          item.state === "ready" || item.state === "failed",
                      )
                    }
                    leadingIcon="upload"
                  >
                    {uploading ? "アップロード中…" : "アップロード開始"}
                  </Button>
                </div>
              </div>
            ) : null}
          </>
        )}
      </Card>

      <Card>
        <div className="card__header">
          <div>
            <h2>答案の処理状況</h2>
            <p>問題のある答案はまとめて絞り込めます。</p>
          </div>
          {submissions.data ? (
            <Badge tone="neutral">
              {submissions.data.totalApproximate ??
                submissions.data.items.length}
              件
            </Badge>
          ) : null}
        </div>
        <div className="list-toolbar list-toolbar--submissions">
          <SearchInput
            value={search}
            onChange={setSearch}
            placeholder={
              scanOperatorOnly
                ? "ファイル名で検索"
                : "ファイル名・生徒名で検索"
            }
          />
          <select
            aria-label="答案の状態"
            value={stateFilter}
            onChange={(event) => setStateFilter(event.target.value)}
          >
            <option value="all">すべての状態</option>
            {scanOperatorOnly ? (
              <>
                <option value="uploading">アップロード中</option>
                <option value="validating">ファイルを確認中</option>
                <option value="preprocessing">画像を準備中</option>
                <option value="awaitingAi">処理待ち</option>
                <option value="needsAttention">AI処理または画像の確認</option>
                <option value="readyForReview">先生が確認できます</option>
                <option value="finalized">処理完了</option>
              </>
            ) : (
              <>
                <option value="needsAttention">AI処理または画像の確認</option>
                <option value="needsNameReview">生徒名の確認が必要</option>
                <option value="needsGradeReview">採点の確認が必要</option>
                <option value="readyToFinalize">確定できます</option>
                <option value="finalized">確定済み</option>
              </>
            )}
            <option value="failed">失敗</option>
          </select>
        </div>
        {submissions.status === "loading" ? (
          <LoadingState label="答案の状態を確認しています" />
        ) : submissions.status === "error" ? (
          <ErrorState error={submissions.error} onRetry={submissions.reload} />
        ) : submissions.data?.items.length ? (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>ファイル</th>
                  {!scanOperatorOnly ? <th>生徒</th> : null}
                  {!scanOperatorOnly ? <th>ページ</th> : null}
                  <th>状態</th>
                  {!scanOperatorOnly ? <th>得点</th> : null}
                  <th>更新日時</th>
                  {!scanOperatorOnly ? (
                    <th>
                      <span className="sr-only">操作</span>
                    </th>
                  ) : null}
                </tr>
              </thead>
              <tbody>
                {submissions.data.items.map((submission) => {
                  const workflowHref = submissionWorkflowHref(submission);
                  return (
                    <tr key={submission.id}>
                    <td>
                      <div className="table-primary">
                        <span className="file-icon">
                          <Icon name="file" size={18} />
                        </span>
                        <div>
                          <strong>{submission.fileName || "答案"}</strong>
                          {submission.qualityWarnings?.[0] ? (
                            <small className="warning-text">
                              <Icon name="alert" size={14} />
                              {submission.qualityWarnings[0]}
                            </small>
                          ) : null}
                        </div>
                      </div>
                    </td>
                    {!scanOperatorOnly ? (
                      <td>
                        <div className="submission-student">
                          <span>
                            {submission.studentDisplayName || (
                              <span className="muted">未割り当て</span>
                            )}
                          </span>
                          {submission.attemptNumber &&
                          submission.attemptNumber > 1 ? (
                            <Badge tone="neutral">
                              第{submission.attemptNumber}回
                            </Badge>
                          ) : submission.canonicalForSession ? (
                            <Badge tone="success">代表答案</Badge>
                          ) : null}
                        </div>
                      </td>
                    ) : null}
                    {!scanOperatorOnly ? (
                      <td>{submission.pageCount ?? "—"}</td>
                    ) : null}
                    <td>
                      <StatusBadge status={submission.state} />
                      <small className="status-help">
                        {statusHelp(submission.state)}
                      </small>
                    </td>
                    {!scanOperatorOnly ? (
                      <td>
                        {submission.totalPossiblePointsMilli ? (
                          <>
                            {formatPoints(submission.totalEarnedPointsMilli)} /{" "}
                            {formatPoints(submission.totalPossiblePointsMilli)}
                            <small>
                              {formatPercentageBasisPoints(
                                ((submission.totalEarnedPointsMilli || 0) /
                                  submission.totalPossiblePointsMilli) *
                                  10_000,
                              )}
                            </small>
                          </>
                        ) : (
                          "—"
                        )}
                      </td>
                    ) : null}
                    <td>{formatDateTime(submission.updatedAt)}</td>
                    {!scanOperatorOnly ? (
                      <td className="table-action">
                        {workflowHref ? (
                          <Link
                            to={workflowHref}
                            aria-label={`${submission.fileName || "答案"}を開く`}
                          >
                            <Icon name="chevronRight" size={18} />
                          </Link>
                        ) : (
                          <span className="muted" aria-label="現在は操作できません">
                            —
                          </span>
                        )}
                      </td>
                    ) : null}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        ) : (
          <EmptyState
            icon="upload"
            title="該当する答案はありません"
            description={
              stateFilter === "all" && !search
                ? "答案をアップロードすると、処理状況がここに表示されます。"
                : "検索条件や状態を変更してください。"
            }
          />
        )}
      </Card>

      {canManageSession ? (
        <Modal
          open={closeOpen}
          onClose={() => !sessionStateWorking && setCloseOpen(false)}
          title="答案の受付を終了しますか？"
          description="新しいアップロードを停止します。送信済みの答案処理はそのまま続きます。"
          size="small"
          footer={
            <>
              <Button
                variant="secondary"
                onClick={() => setCloseOpen(false)}
                disabled={sessionStateWorking}
              >
                キャンセル
              </Button>
              <Button
                onClick={() => void toggleSessionState()}
                disabled={sessionStateWorking}
              >
                {sessionStateWorking ? "変更しています…" : "受付を終了"}
              </Button>
            </>
          }
        >
          {actionError ? (
            <InlineAlert tone="danger">
              <p>{actionError}</p>
            </InlineAlert>
          ) : null}
          <p>
            終了後も、先生は答案の確認・確定を続けられます。必要な場合は後から受付を再開できます。
          </p>
        </Modal>
      ) : null}
    </div>
  );
}

function SummaryMetric({
  label,
  value,
  suffix,
  icon,
  loading,
  tone = "default",
}: {
  label: string;
  value?: number;
  suffix: string;
  icon: "upload" | "alert" | "check" | "clock";
  loading: boolean;
  tone?: "default" | "warning" | "success";
}) {
  return (
    <Card className={`summary-metric summary-metric--${tone}`}>
      <span className="summary-metric__icon">
        <Icon name={icon} />
      </span>
      <div>
        <span>{label}</span>
        {loading ? (
          <span className="skeleton skeleton--metric" />
        ) : (
          <strong>
            {value ?? 0}
            <small>{suffix}</small>
          </strong>
        )}
      </div>
    </Card>
  );
}

function UploadState({ state }: { state: LocalUpload["state"] }) {
  if (state === "ready") return <Badge tone="neutral">送信待ち</Badge>;
  if (state === "uploading") return <Badge tone="info">送信中</Badge>;
  if (state === "completed") return <Badge tone="success">送信済み</Badge>;
  if (state === "duplicate") return <Badge tone="warning">重複</Badge>;
  return <Badge tone="danger">失敗</Badge>;
}

function statusHelp(state: string) {
  const messages: Record<string, string> = {
    preprocessing: "向き・ページ順・解答欄を学校内で確認中",
    awaiting_ai: "答案はホストに保存済みです",
    awaitingAi: "答案はホストに保存済みです",
    gemini_batch_running: "AIが答案を採点しています",
    geminiBatchRunning: "AIが答案を採点しています",
    openrouter_queued: "ホストの待機列から順に処理します",
    openRouterQueued: "ホストの待機列から順に処理します",
    budget_blocked: "管理者による利用上限の確認が必要です",
    budgetBlocked: "管理者による利用上限の確認が必要です",
  };
  return messages[state] || "";
}

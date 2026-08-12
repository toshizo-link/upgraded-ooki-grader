import { useState } from "react";
import { Link, useParams } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon } from "../components/Icon";
import { OrderedScanUploadBoard } from "../components/OrderedScanUploadBoard";
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
  api,
  asPaged,
  newIdempotencyKey,
} from "../lib/api";
import {
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
} from "../types";

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


export function SessionDetailPage() {
  const { sessionId = "" } = useParams();
  const { hasAnyRole } = useSession();
  const canManageSession = hasAnyRole("administrator", "teacher");
  const scanOperatorOnly =
    hasAnyRole("scanOperator") && !canManageSession;
  const [search, setSearch] = useState("");
  const [stateFilter, setStateFilter] = useState("all");
  const [sessionStateWorking, setSessionStateWorking] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const [closeOpen, setCloseOpen] = useState(false);
  const [archiveOpen, setArchiveOpen] = useState(false);

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


  async function toggleSessionState() {
    if (!session.data || sessionStateWorking) return;
    if (!["draft", "open", "closed"].includes(session.data.state)) return;
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

  async function archiveSession() {
    if (session.data?.state !== "closed" || sessionStateWorking) return;
    setSessionStateWorking(true);
    setActionError(undefined);
    try {
      await api.post(
        `/test-sessions/${encodeURIComponent(sessionId)}:archive`,
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      setArchiveOpen(false);
      session.reload();
      if (!scanOperatorOnly) summary.reload();
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "テスト実施をアーカイブできませんでした。",
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
  const isClosed = data.state === "closed";
  const isArchived = data.state === "archived";
  const operatorSummary = submissions.data?.summary;

  return (
    <div className="page session-detail-page">
      <PageHeader
        eyebrow={`${formatDate(data.testDate)} 実施`}
        title={
          data.templateTitle ||
          data.title ||
          data.name ||
          data.sessionName ||
          "テスト実施"
        }
        description={
          [
            data.subject,
            data.gradeLabel,
            data.category,
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
            {canManageSession && !isArchived ? (
              <>
                {isClosed ? (
                  <Button
                    variant="quiet"
                    disabled={sessionStateWorking}
                    onClick={() => {
                      setActionError(undefined);
                      setArchiveOpen(true);
                    }}
                  >
                    アーカイブ
                  </Button>
                ) : null}
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
                      : data.state === "draft"
                        ? "受付を開始"
                        : "受付を再開"}
                </Button>
              </>
            ) : null}
          </>
        }
      />
      {actionError && !closeOpen && !archiveOpen ? (
        <InlineAlert tone="danger">
          <p>{actionError}</p>
        </InlineAlert>
      ) : null}
      {isArchived ? (
        <InlineAlert tone="info" title="このテスト実施はアーカイブされています">
          <p>
            新しい答案の受付や状態変更はできません。答案と採点結果は引き続き確認できます。
          </p>
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

      <OrderedScanUploadBoard
        sessionId={sessionId}
        expectedPageCount={data.expectedSubmissionPageCount ?? undefined}
        isOpen={isOpen}
        onBatchChanged={() => {
          submissions.reload();
          if (!scanOperatorOnly) summary.reload();
        }}
      />

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
        <>
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
          <Modal
            open={archiveOpen}
            onClose={() => !sessionStateWorking && setArchiveOpen(false)}
            title="このテスト実施をアーカイブしますか？"
            description="通常の運用対象から外し、読み取り専用にします。"
            size="small"
            footer={
              <>
                <Button
                  variant="secondary"
                  onClick={() => setArchiveOpen(false)}
                  disabled={sessionStateWorking}
                >
                  キャンセル
                </Button>
                <Button
                  variant="danger"
                  onClick={() => void archiveSession()}
                  disabled={sessionStateWorking}
                >
                  {sessionStateWorking
                    ? "変更しています…"
                    : "アーカイブする"}
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
              すべての答案が確定または取消済みになり、アップロード、重複確認、順番取り込み、採点処理が完了してから実行できます。
            </p>
            <p>
              アーカイブ後は答案受付を再開できません。答案、採点結果、訂正履歴は削除されず、引き続き閲覧できます。
            </p>
          </Modal>
        </>
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

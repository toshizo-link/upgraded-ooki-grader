import { Link } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon, type IconName } from "../components/Icon";
import {
  Badge,
  Card,
  EmptyState,
  ErrorState,
  PageHeader,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { api, asPaged } from "../lib/api";
import {
  formatDate,
  formatDateTime,
  toDateInput,
} from "../lib/format";
import { submissionWorkflowHref } from "../lib/submissionNavigation";
import type {
  PagedResponse,
  ReviewCounts,
  SubmissionSummary,
  TestSessionSummary,
} from "../types";

export function DashboardPage() {
  const { hasAnyRole } = useSession();
  const scanOperatorOnly =
    hasAnyRole("scanOperator") && !hasAnyRole("administrator", "teacher");
  const readOnlyReviewerOnly =
    hasAnyRole("readOnlyReviewer") &&
    !hasAnyRole("administrator", "teacher");
  if (scanOperatorOnly) return <ScanOperatorDashboardPage />;
  if (readOnlyReviewerOnly) return <ReadOnlyDashboardPage />;
  return <StaffDashboardPage />;
}

function ReadOnlyDashboardPage() {
  const { user } = useSession();
  return (
    <div className="page dashboard-page">
      <PageHeader
        eyebrow={formatDate(toDateInput(), { weekday: "long" })}
        title={`${user?.displayName || "閲覧担当"}さん、お疲れさまです`}
        description="確定済みの成績と帳票を閲覧できます。"
        actions={
          <Link className="button button--primary button--large" to="/reports">
            <Icon name="reports" size={19} />
            <span>確定済みの帳票を見る</span>
          </Link>
        }
      />
      <Card>
        <EmptyState
          icon="reports"
          title="閲覧メニュー"
          description="未確定の答案や解答画像は表示されません。帳票から確定済みの結果を確認してください。"
        />
      </Card>
    </div>
  );
}

function ScanOperatorDashboardPage() {
  const { user } = useSession();
  const sessions = useApiQuery<PagedResponse<TestSessionSummary>>(
    "scan-dashboard-sessions",
    async (signal) =>
      asPaged(
        await api.get("/test-sessions", { state: "open", pageSize: 20 }, signal),
      ),
  );
  const displayName = user?.displayName || "スキャン担当";

  return (
    <div className="page dashboard-page">
      <PageHeader
        eyebrow={formatDate(toDateInput(), { weekday: "long" })}
        title={`${displayName}さん、お疲れさまです`}
        description="受付中のテストを選び、答案のアップロードと画像処理の状況を確認します。"
        actions={
          <Link className="button button--primary button--large" to="/sessions">
            <Icon name="upload" size={19} />
            <span>答案をアップロード</span>
          </Link>
        }
      />
      <Card className="dashboard-card">
        <div className="card__header">
          <div>
            <h2>受付中のテスト</h2>
            <p>答案を追加できる実施セッションです。</p>
          </div>
          <Link className="text-link" to="/sessions">
            すべて見る
            <Icon name="arrowRight" size={16} />
          </Link>
        </div>
        {sessions.status === "loading" ? (
          <SkeletonRows rows={4} />
        ) : sessions.status === "error" ? (
          <ErrorState error={sessions.error} onRetry={sessions.reload} compact />
        ) : sessions.data?.items.length ? (
          <div className="session-mini-list">
            {sessions.data.items.map((session) => (
              <Link
                to={`/sessions/${encodeURIComponent(session.id)}`}
                className="session-mini-row"
                key={session.id}
              >
                <div className="session-date-block">
                  <span>
                    {new Intl.DateTimeFormat("ja-JP", {
                      timeZone: "Asia/Tokyo",
                      month: "numeric",
                    }).format(new Date(`${session.testDate}T00:00:00+09:00`))}
                    月
                  </span>
                  <strong>
                    {new Intl.DateTimeFormat("ja-JP", {
                      timeZone: "Asia/Tokyo",
                      day: "numeric",
                    }).format(new Date(`${session.testDate}T00:00:00+09:00`))}
                  </strong>
                </div>
                <div className="session-mini-row__copy">
                  <strong>
                    {session.templateTitle ||
                      session.title ||
                      session.name ||
                      session.sessionName ||
                      "名称未設定"}
                  </strong>
                  <span>
                    {[session.classLabel, session.course]
                      .filter(Boolean)
                      .join("・") || "対象クラス未設定"}
                  </span>
                </div>
                <div className="session-mini-row__progress">
                  <strong>{session.submissionCount ?? 0}</strong>
                  <small>受信済み</small>
                </div>
                <Icon name="chevronRight" size={18} />
              </Link>
            ))}
          </div>
        ) : (
          <EmptyState
            icon="sessions"
            title="受付中のテストはありません"
            description="先生が答案受付を開始すると、ここからアップロードできます。"
          />
        )}
      </Card>
      <InlineOperatorNotice />
    </div>
  );
}

function InlineOperatorNotice() {
  return (
    <Card>
      <div className="card__header">
        <div>
          <h2>スキャン担当の画面</h2>
          <p>
            この画面ではアップロードと画像処理の状態だけを表示します。採点や生徒への割り当ては先生が確認します。
          </p>
        </div>
      </div>
    </Card>
  );
}

function StaffDashboardPage() {
  const review = useApiQuery<ReviewCounts>("dashboard-review", (signal) =>
    api.get("/review/counts", undefined, signal),
  );
  const sessions = useApiQuery<PagedResponse<TestSessionSummary>>(
    "dashboard-sessions",
    async (signal) =>
      asPaged(
        await api.get("/test-sessions", { state: "open", pageSize: 5 }, signal),
      ),
  );
  const recent = useApiQuery<PagedResponse<SubmissionSummary>>(
    "dashboard-recent",
    async (signal) =>
      asPaged(
        await api.get(
          "/submissions",
          { sort: "-updatedAt", pageSize: 6 },
          signal,
        ),
      ),
  );
  const today = toDateInput();
  const activeTasks = [
    {
      label: "生徒名を確認",
      description: "候補を見比べて答案を生徒に割り当てます",
      count: review.data?.needsNameReview,
      to: "/review?tab=name",
      icon: "user" as IconName,
      tone: "amber",
    },
    {
      label: "採点を確認",
      description: "判読困難・部分点などを先生が判断します",
      count: review.data?.needsGradeReview,
      to: "/review?tab=grading",
      icon: "edit" as IconName,
      tone: "purple",
    },
    {
      label: "答案を確定",
      description: "すべての確認が終わった答案です",
      count: review.data?.readyToFinalize,
      to: "/review?tab=finalize",
      icon: "check" as IconName,
      tone: "green",
    },
  ];

  return (
    <div className="page dashboard-page">
      <PageHeader
        eyebrow={formatDate(today, { weekday: "long" })}
        title="今日もお疲れさまです"
        description="確認が必要な答案と、進行中のテストをまとめています。"
        actions={
          <Link className="button button--primary button--large" to="/sessions">
            <Icon name="upload" size={19} />
            <span>答案をアップロード</span>
          </Link>
        }
      />

      <div className="dashboard-layout">
        <section aria-labelledby="next-actions-title">
          <Card className="dashboard-card">
            <div className="card__header">
              <div>
                <h2 id="next-actions-title">次にすること</h2>
                <p>上から順に確認すると、答案を確定できます。</p>
              </div>
              {review.status === "success" ? (
                <Badge tone="neutral">
                  {activeTasks.reduce((sum, task) => sum + (task.count || 0), 0)}
                  件
                </Badge>
              ) : null}
            </div>
            {review.status === "loading" ? (
              <SkeletonRows rows={3} />
            ) : review.status === "error" ? (
              <ErrorState
                error={review.error}
                onRetry={review.reload}
                compact
              />
            ) : activeTasks.every((task) => !task.count) ? (
              <EmptyState
                icon="check"
                title="確認待ちはありません"
                description="新しい答案が処理されると、ここに次の作業が表示されます。"
              />
            ) : (
              <div className="task-list">
                {activeTasks
                  .filter((task) => (task.count || 0) > 0)
                  .map((task) => (
                    <Link className="task-row" to={task.to} key={task.label}>
                      <span
                        className={`task-row__icon task-row__icon--${task.tone}`}
                      >
                        <Icon name={task.icon} />
                      </span>
                      <span className="task-row__copy">
                        <strong>{task.label}</strong>
                        <small>{task.description}</small>
                      </span>
                      <span className="task-row__count">{task.count}</span>
                      <Icon name="chevronRight" size={18} />
                    </Link>
                  ))}
              </div>
            )}
          </Card>
        </section>

        <section aria-labelledby="sessions-title">
          <Card className="dashboard-card">
            <div className="card__header">
              <div>
                <h2 id="sessions-title">受付中のテスト</h2>
                <p>答案を追加できる実施セッションです。</p>
              </div>
              <Link className="text-link" to="/sessions">
                すべて見る
                <Icon name="arrowRight" size={16} />
              </Link>
            </div>
            {sessions.status === "loading" ? (
              <SkeletonRows rows={3} />
            ) : sessions.status === "error" ? (
              <ErrorState
                error={sessions.error}
                onRetry={sessions.reload}
                compact
              />
            ) : sessions.data?.items.length ? (
              <div className="session-mini-list">
                {sessions.data.items.map((session) => (
                  <Link
                    to={`/sessions/${encodeURIComponent(session.id)}`}
                    className="session-mini-row"
                    key={session.id}
                  >
                    <div className="session-date-block">
                      <span>
                        {new Intl.DateTimeFormat("ja-JP", {
                          timeZone: "Asia/Tokyo",
                          month: "numeric",
                        }).format(
                          new Date(`${session.testDate}T00:00:00+09:00`),
                        )}
                        月
                      </span>
                      <strong>
                        {new Intl.DateTimeFormat("ja-JP", {
                          timeZone: "Asia/Tokyo",
                          day: "numeric",
                        }).format(
                          new Date(`${session.testDate}T00:00:00+09:00`),
                        )}
                      </strong>
                    </div>
                    <div className="session-mini-row__copy">
                      <strong>
                        {session.templateTitle ||
                          session.title ||
                          session.name ||
                          session.sessionName ||
                          "名称未設定"}
                      </strong>
                      <span>
                        {[session.classLabel, session.course]
                          .filter(Boolean)
                          .join("・") || "対象クラス未設定"}
                      </span>
                    </div>
                    <div className="session-mini-row__progress">
                      <strong>{session.submissionCount ?? 0}</strong>
                      <small>答案</small>
                    </div>
                    <Icon name="chevronRight" size={18} />
                  </Link>
                ))}
              </div>
            ) : (
              <EmptyState
                icon="sessions"
                title="受付中のテストはありません"
                description="テスト実施から新しいセッションを作成できます。"
              />
            )}
          </Card>
        </section>
      </div>

      <section aria-labelledby="recent-title">
        <Card>
          <div className="card__header">
            <div>
              <h2 id="recent-title">最近の答案</h2>
              <p>アップロード後の処理状況を確認できます。</p>
            </div>
          </div>
          {recent.status === "loading" ? (
            <SkeletonRows rows={4} />
          ) : recent.status === "error" ? (
            <ErrorState error={recent.error} onRetry={recent.reload} compact />
          ) : recent.data?.items.length ? (
            <div className="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th>答案</th>
                    <th>生徒</th>
                    <th>状態</th>
                    <th>更新日時</th>
                    <th>
                      <span className="sr-only">操作</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {recent.data.items.map((submission) => {
                    const workflowHref = submissionWorkflowHref(submission);
                    const destination =
                      workflowHref ||
                      (submission.testSessionId
                        ? `/sessions/${encodeURIComponent(submission.testSessionId)}`
                        : "/sessions");
                    return (
                      <tr key={submission.id}>
                        <td>
                          <div className="table-primary">
                            <span className="file-icon">
                              <Icon name="file" size={18} />
                            </span>
                            <strong>{submission.fileName || "答案"}</strong>
                          </div>
                        </td>
                        <td>{submission.studentDisplayName || "未割り当て"}</td>
                        <td>
                          <StatusBadge status={submission.state} />
                        </td>
                        <td>{formatDateTime(submission.updatedAt)}</td>
                        <td className="table-action">
                          <Link
                            aria-label={`${submission.fileName || "答案"}を開く`}
                            to={destination}
                          >
                            <Icon name="chevronRight" size={18} />
                          </Link>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <EmptyState
              title="答案はまだありません"
              description="テスト実施から答案をアップロードすると、ここに進捗が表示されます。"
              icon="upload"
            />
          )}
        </Card>
      </section>
    </div>
  );
}

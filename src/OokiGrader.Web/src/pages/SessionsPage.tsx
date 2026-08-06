import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon } from "../components/Icon";
import {
  Button,
  Card,
  EmptyState,
  ErrorState,
  Field,
  InlineAlert,
  Modal,
  PageHeader,
  Score,
  SearchInput,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { ApiError, api, asPaged, newIdempotencyKey } from "../lib/api";
import { formatDate, toDateInput } from "../lib/format";
import type {
  PagedResponse,
  TemplateSummary,
  TestSessionSummary,
} from "../types";

export function SessionsPage() {
  const navigate = useNavigate();
  const { hasAnyRole } = useSession();
  const canManageSessions = hasAnyRole("administrator", "teacher");
  const [searchParams, setSearchParams] = useSearchParams();
  const [search, setSearch] = useState(searchParams.get("q") || "");
  const [createOpen, setCreateOpen] = useState(false);
  const state = searchParams.get("state") || "open";
  const sessions = useApiQuery<PagedResponse<TestSessionSummary>>(
    `sessions:${searchParams.toString()}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/test-sessions",
          {
            search: searchParams.get("q"),
            state: state === "all" ? undefined : state,
            pageSize: 100,
          },
          signal,
        ),
      ),
  );

  function updateParam(key: string, value: string) {
    const next = new URLSearchParams(searchParams);
    if (value && value !== "all") next.set(key, value);
    else next.delete(key);
    setSearchParams(next, { replace: true });
  }

  return (
    <div className="page">
      <PageHeader
        eyebrow="答案の受付"
        title="テスト実施"
        description="テストごとに答案をまとめてアップロードし、処理状況を確認します。"
        actions={
          canManageSessions ? (
            <Button leadingIcon="plus" onClick={() => setCreateOpen(true)}>
              答案受付を開始
            </Button>
          ) : undefined
        }
      />
      <Card>
        <div className="list-toolbar">
          <SearchInput
            value={search}
            onChange={(value) => {
              setSearch(value);
              updateParam("q", value);
            }}
            placeholder="テスト名・クラスで検索"
            label="テスト実施を検索"
          />
          <div className="list-toolbar__filters">
            <select
              aria-label="実施状態"
              value={state}
              onChange={(event) => updateParam("state", event.target.value)}
            >
              <option value="open">受付中</option>
              <option value="draft">準備中</option>
              <option value="closed">終了</option>
              <option value="all">すべて</option>
            </select>
          </div>
          {sessions.data ? (
            <span className="result-count">
              {sessions.data.totalApproximate ?? sessions.data.items.length}件
            </span>
          ) : null}
        </div>
        {sessions.status === "loading" ? (
          <SkeletonRows rows={6} />
        ) : sessions.status === "error" ? (
          <ErrorState error={sessions.error} onRetry={sessions.reload} />
        ) : sessions.data?.items.length ? (
          <div className="session-grid">
            {sessions.data.items.map((session) => (
              <SessionCard
                session={session}
                showGradingProgress={canManageSessions}
                key={session.id}
              />
            ))}
          </div>
        ) : (
          <EmptyState
            icon="sessions"
            title={
              searchParams.toString()
                ? "条件に一致するテスト実施はありません"
                : "受付中のテストはありません"
            }
            description={
              searchParams.toString()
                ? "状態や検索条件を変更してください。"
                : "公開済みのひな形を選んで、答案受付を開始します。"
            }
          />
        )}
      </Card>
      <CreateSessionDialog
        open={canManageSessions && createOpen}
        onClose={() => setCreateOpen(false)}
        onCreated={(id) => {
          setCreateOpen(false);
          sessions.reload();
          navigate(`/sessions/${encodeURIComponent(id)}`);
        }}
      />
    </div>
  );
}

function SessionCard({
  session,
  showGradingProgress,
}: {
  session: TestSessionSummary;
  showGradingProgress: boolean;
}) {
  const submissionCount = session.submissionCount || 0;
  const finalized = session.finalizedCount || 0;
  const progress = submissionCount ? (finalized / submissionCount) * 100 : 0;
  return (
    <Link
      className="session-card"
      to={`/sessions/${encodeURIComponent(session.id)}`}
    >
      <div className="session-card__date">
        <span>
          {new Intl.DateTimeFormat("ja-JP", {
            timeZone: "Asia/Tokyo",
            month: "short",
          }).format(new Date(`${session.testDate}T00:00:00+09:00`))}
        </span>
        <strong>
          {new Intl.DateTimeFormat("ja-JP", {
            timeZone: "Asia/Tokyo",
            day: "numeric",
          }).format(new Date(`${session.testDate}T00:00:00+09:00`))}
        </strong>
      </div>
      <div className="session-card__body">
        <div className="session-card__meta">
          <StatusBadge status={session.state} />
        </div>
        <h2>
          {session.sessionName ||
            session.name ||
            session.templateTitle ||
            "名称未設定"}
        </h2>
        <p>
          {[
            session.templateTitle,
            session.classLabel,
            session.course,
          ]
            .filter(Boolean)
            .join("・") || formatDate(session.testDate)}
        </p>
        <div className="session-card__counts">
          <span>
            <strong>{submissionCount}</strong>
            答案
          </span>
          <span>
            <strong>{session.attentionCount || 0}</strong>
            要確認
          </span>
          {showGradingProgress ? (
            <span>
              <strong>{finalized}</strong>
              確定
            </span>
          ) : null}
        </div>
        {showGradingProgress ? (
          <div className="session-card__progress">
            <span style={{ width: `${progress}%` }} />
          </div>
        ) : null}
      </div>
      <Icon name="chevronRight" size={19} />
    </Link>
  );
}

function CreateSessionDialog({
  open,
  onClose,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const templates = useApiQuery<PagedResponse<TemplateSummary>>(
    "published-templates-for-session",
    async (signal) =>
      asPaged(
        await api.get(
          "/templates",
          { state: "active", pageSize: 200 },
          signal,
        ),
      ),
    open,
  );
  const [values, setValues] = useState({
    templateId: "",
    templateVersionId: "",
    sessionName: "",
    testDate: toDateInput(),
    classLabel: "",
    course: "",
  });
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();

  function selectTemplate(id: string) {
    const template = templates.data?.items.find((item) => item.id === id);
    setValues({
      ...values,
      templateId: id,
      templateVersionId: template?.activeVersionId || "",
      sessionName: values.sessionName || template?.title || "",
    });
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    setWorking(true);
    setError(undefined);
    try {
      const created = await api.post<TestSessionSummary>(
        "/test-sessions",
        {
          templateVersionId: values.templateVersionId,
          testDate: values.testDate,
          classLabel: values.classLabel || undefined,
          course: values.course || undefined,
          priority: "expedite",
          sessionName: values.sessionName,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      await api.post(
        `/test-sessions/${encodeURIComponent(created.id)}:open`,
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      onCreated(created.id);
    } catch (reason) {
      setError(
        reason instanceof ApiError
          ? reason.problem.errors?.[0]?.message || reason.message
          : "答案受付を開始できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={() => !working && onClose()}
      title="答案受付を開始"
      description="公開済みのひな形と、答案に記載された実施日を選びます。"
      size="large"
    >
      {templates.status === "loading" ? (
        <SkeletonRows rows={3} />
      ) : templates.status === "error" ? (
        <ErrorState error={templates.error} onRetry={templates.reload} compact />
      ) : !templates.data?.items.length ? (
        <EmptyState
          icon="templates"
          title="公開済みのひな形がありません"
          description="先に採点基準を確認し、ひな形を公開してください。"
          action={
            <Link className="button button--primary button--medium" to="/templates">
              <span>ひな形を開く</span>
            </Link>
          }
        />
      ) : (
        <form className="session-form" onSubmit={submit}>
          {error ? (
            <InlineAlert tone="danger">
              <p>{error}</p>
            </InlineAlert>
          ) : null}
          <Field label="テストひな形" htmlFor="session-template" required>
            <select
              id="session-template"
              value={values.templateId}
              onChange={(event) => selectTemplate(event.target.value)}
              required
            >
              <option value="">選択してください</option>
              {templates.data.items.map((template) => (
                <option value={template.id} key={template.id}>
                  {template.title}（第{template.activeVersionNumber || "—"}版）
                </option>
              ))}
            </select>
          </Field>
          <div className="form-grid form-grid--2">
            <Field label="実施名" htmlFor="session-name" required>
              <input
                id="session-name"
                value={values.sessionName}
                onChange={(event) =>
                  setValues({ ...values, sessionName: event.target.value })
                }
                placeholder="例：7月27日 漢字確認テスト"
                required
              />
            </Field>
            <Field
              label="テスト実施日"
              htmlFor="session-date"
              required
              hint="進捗グラフと帳票にはこの日付が使われます。"
            >
              <input
                id="session-date"
                type="date"
                value={values.testDate}
                onChange={(event) =>
                  setValues({ ...values, testDate: event.target.value })
                }
                required
              />
            </Field>
            <Field label="クラス" htmlFor="session-class">
              <input
                id="session-class"
                value={values.classLabel}
                onChange={(event) =>
                  setValues({ ...values, classLabel: event.target.value })
                }
                placeholder="例：中2-A"
              />
            </Field>
            <Field label="コース" htmlFor="session-course">
              <input
                id="session-course"
                value={values.course}
                onChange={(event) =>
                  setValues({ ...values, course: event.target.value })
                }
              />
            </Field>
          </div>
          <div className="form-actions">
            <Button type="button" variant="secondary" onClick={onClose}>
              キャンセル
            </Button>
            <Button
              type="submit"
              disabled={
                working ||
                !values.templateVersionId ||
                !values.sessionName.trim()
              }
            >
              {working ? "開始しています…" : "答案受付を開始"}
            </Button>
          </div>
        </form>
      )}
    </Modal>
  );
}

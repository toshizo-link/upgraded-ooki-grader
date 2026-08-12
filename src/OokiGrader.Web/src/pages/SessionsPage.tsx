import { useRef, useState, type FormEvent } from "react";
import { Link, useNavigate } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon } from "../components/Icon";
import { TemplateSessionMetadata } from "../components/TemplateSessionMetadata";
import {
  ActiveFilterSummary,
  facetOptions,
  facetSuggestions,
  FilterTextInput,
  ListPagination,
  ListSortControls,
} from "../components/ListControls";
import {
  Button,
  Card,
  EmptyState,
  ErrorState,
  Field,
  InlineAlert,
  Modal,
  PageHeader,
  SearchInput,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { useListQueryState } from "../hooks/useListQueryState";
import { ApiError, api, asPaged, newIdempotencyKey } from "../lib/api";
import { formatDate, toDateInput } from "../lib/format";
import type {
  PagedResponse,
  TemplateSummary,
  TestSessionSummary,
} from "../types";

const SESSION_SORTS = [
  { value: "testDate", label: "実施日", defaultDirection: "desc" },
  { value: "updatedAt", label: "更新日時", defaultDirection: "desc" },
  { value: "name", label: "試験名", defaultDirection: "asc" },
] as const;

export async function loadActiveTemplates(signal: AbortSignal) {
  const items: TemplateSummary[] = [];
  let cursor: string | undefined;
  const seenCursors = new Set<string>();
  do {
    const page = asPaged(
      await api.get<PagedResponse<TemplateSummary> | TemplateSummary[]>(
        "/templates",
        {
          state: "active",
          cursor,
          pageSize: 200,
        },
        signal,
      ),
    );
    items.push(...page.items);
    const nextCursor = page.nextCursor || undefined;
    if (!nextCursor || seenCursors.has(nextCursor)) break;
    seenCursors.add(nextCursor);
    cursor = nextCursor;
  } while (cursor);
  return {
    items,
    nextCursor: null,
    totalApproximate: items.length,
  } satisfies PagedResponse<TemplateSummary>;
}

const SESSION_QUERY_OPTIONS = {
  allowedSorts: [
    "-testDate",
    "testDate",
    "-updatedAt",
    "updatedAt",
    "name",
    "-name",
  ],
  defaultSort: "-testDate",
  enumParams: { state: ["draft", "open", "closed", "archived", "all"] },
  dateParams: ["from", "to"],
  textParams: ["templateId", "class", "course"],
  defaultPageSize: 50,
} as const;

const SESSION_FILTER_KEYS = [
  "q",
  "state",
  "from",
  "to",
  "templateId",
  "class",
  "course",
] as const;

export function SessionsPage() {
  const navigate = useNavigate();
  const { hasAnyRole } = useSession();
  const canManageSessions = hasAnyRole("administrator", "teacher");
  const list = useListQueryState(SESSION_QUERY_OPTIONS);
  const { searchParams } = list;
  const [createOpen, setCreateOpen] = useState(false);
  const state = searchParams.get("state") || "open";
  const from = searchParams.get("from") || "";
  const to = searchParams.get("to") || "";
  const templateId = searchParams.get("templateId") || "";
  const classFilter = searchParams.get("class") || "";
  const courseFilter = searchParams.get("course") || "";
  const sessions = useApiQuery<PagedResponse<TestSessionSummary>>(
    `sessions:${searchParams.toString()}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/test-sessions",
          {
            search: searchParams.get("q"),
            state: state === "all" ? undefined : state,
            from: from || undefined,
            to: to || undefined,
            templateId: templateId || undefined,
            class: classFilter || undefined,
            course: courseFilter || undefined,
            sort: list.sort,
            cursor: list.cursor,
            pageSize: list.pageSize,
            includeFacets: true,
          },
          signal,
        ),
      ),
  );
  const templates = facetOptions(
    sessions.data?.facets,
    "templates",
    (sessions.data?.items || []).map((session) => ({
      value: session.templateId,
      label: session.templateTitle || session.templateId,
    })),
  );
  const classes = facetSuggestions(
    sessions.data?.facets,
    "classes",
    (sessions.data?.items || []).map((session) => session.classLabel),
  );
  const courses = facetSuggestions(
    sessions.data?.facets,
    "courses",
    (sessions.data?.items || []).map((session) => session.course),
  );
  const activeFilters = [
    searchParams.get("q")
      ? { key: "q", label: "検索", value: `「${searchParams.get("q")}」` }
      : undefined,
    state !== "all"
      ? {
          key: "state",
          label: "状態",
          value:
            { draft: "準備中", open: "受付中", closed: "終了", archived: "アーカイブ" }[
              state
            ] || state,
        }
      : undefined,
    from ? { key: "from", label: "開始日", value: from } : undefined,
    to ? { key: "to", label: "終了日", value: to } : undefined,
    templateId
      ? {
          key: "templateId",
          label: "ひな形",
          value: templates.find((item) => item.value === templateId)?.label || templateId,
        }
      : undefined,
    classFilter ? { key: "class", label: "クラス", value: classFilter } : undefined,
    courseFilter ? { key: "course", label: "コース", value: courseFilter } : undefined,
  ].filter((value): value is { key: string; label: string; value: string } => Boolean(value));

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
            value={list.search}
            onChange={list.setSearch}
            placeholder="テスト名・クラスで検索"
            label="テスト実施を検索"
          />
          <ListSortControls
            value={list.sort}
            options={SESSION_SORTS}
            defaultValue="-testDate"
            onChange={(value) => list.updateParam("sort", value)}
          />
          {sessions.data ? (
            <span className="result-count">
              {sessions.data.totalApproximate ?? sessions.data.items.length}件
            </span>
          ) : null}
        </div>
        <div className="list-filter-panel" aria-label="テスト実施の絞り込み">
          <label className="filter-field">
            <span>実施状態</span>
            <select
              value={state}
              onChange={(event) => list.updateParam("state", event.target.value)}
            >
              <option value="open">受付中</option>
              <option value="draft">準備中</option>
              <option value="closed">終了</option>
              <option value="archived">アーカイブ</option>
              <option value="all">すべて</option>
            </select>
          </label>
          <label className="filter-field">
            <span>ひな形</span>
            <select
              value={templateId}
              onChange={(event) => list.updateParam("templateId", event.target.value)}
            >
              <option value="">すべてのひな形</option>
              {templateId && !templates.some((item) => item.value === templateId) ? (
                <option value={templateId}>{templateId}</option>
              ) : null}
              {templates.map((item) => (
                <option value={item.value} key={item.value}>
                  {item.label}
                </option>
              ))}
            </select>
          </label>
          <FilterTextInput
            label="クラス"
            value={classFilter}
            suggestions={classes}
            onCommit={(value) => list.updateParam("class", value)}
          />
          <FilterTextInput
            label="コース"
            value={courseFilter}
            suggestions={courses}
            onCommit={(value) => list.updateParam("course", value)}
          />
          <label className="filter-field">
            <span>開始日</span>
            <input
              type="date"
              value={from}
              max={to || undefined}
              onChange={(event) => list.updateParam("from", event.target.value)}
            />
          </label>
          <label className="filter-field">
            <span>終了日</span>
            <input
              type="date"
              value={to}
              min={from || undefined}
              onChange={(event) => list.updateParam("to", event.target.value)}
            />
          </label>
        </div>
        <ActiveFilterSummary
          filters={activeFilters}
          onClear={() => list.clearFilters(SESSION_FILTER_KEYS, { state: "all" })}
        />
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
              activeFilters.length
                ? "条件に一致するテスト実施はありません"
                : "受付中のテストはありません"
            }
            description={
              activeFilters.length
                ? "状態や検索条件を変更してください。"
                : "受付に使えるひな形を選んで、答案受付を開始します。"
            }
          />
        )}
        <ListPagination
          page={list.page}
          pageSize={list.pageSize}
          itemCount={sessions.data?.items.length || 0}
          totalApproximate={sessions.data?.totalApproximate}
          hasNext={list.canNavigateNext(sessions.data?.nextCursor)}
          nextBlockedReason={
            sessions.data?.nextCursor && !list.canNavigateNext(sessions.data.nextCursor)
              ? "これ以上は絞り込みを追加するか、1ページの件数を増やしてください。"
              : undefined
          }
          canGoPrevious={list.canGoPrevious}
          onNext={() => list.nextPage(sessions.data?.nextCursor)}
          onPrevious={list.previousPage}
          onPageSizeChange={list.setPageSize}
        />
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
          {session.templateTitle ||
            session.title ||
            session.name ||
            session.sessionName ||
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
    loadActiveTemplates,
    open,
  );
  const [values, setValues] = useState({
    templateId: "",
    templateVersionId: "",
    testDate: toDateInput(),
    classLabel: "",
  });
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();
  const idempotencyKeyRef = useRef("");

  function closeDialog() {
    idempotencyKeyRef.current = "";
    onClose();
  }

  function selectTemplate(id: string) {
    const template = templates.data?.items.find((item) => item.id === id);
    idempotencyKeyRef.current = "";
    setValues((current) => ({
      ...current,
      templateId: id,
      templateVersionId: template?.activeVersionId || "",
    }));
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    setWorking(true);
    setError(undefined);
    try {
      const idempotencyKey =
        idempotencyKeyRef.current || newIdempotencyKey();
      idempotencyKeyRef.current = idempotencyKey;
      const created = await api.post<TestSessionSummary>(
        "/test-sessions",
        {
          templateVersionId: values.templateVersionId,
          testDate: values.testDate,
          classLabel: values.classLabel || undefined,
          openImmediately: true,
        },
        { idempotencyKey },
      );
      idempotencyKeyRef.current = "";
      onCreated(created.id);
    } catch (reason) {
      if (reason instanceof ApiError) {
        idempotencyKeyRef.current = "";
      }
      setError(
        reason instanceof ApiError
          ? reason.problem.errors?.[0]?.message || reason.message
          : "答案受付を開始できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  const selectedTemplate = templates.data?.items.find(
    (item) => item.id === values.templateId,
  );

  return (
    <Modal
      open={open}
      onClose={() => !working && closeDialog()}
      title="答案受付を開始"
      description="受付に使えるひな形を選び、実施日だけを入力します。必要な場合は対象クラスも指定できます。"
      size="large"
    >
      {templates.status === "loading" ? (
        <SkeletonRows rows={3} />
      ) : templates.status === "error" ? (
        <ErrorState error={templates.error} onRetry={templates.reload} compact />
      ) : !templates.data?.items.length ? (
        <EmptyState
          icon="templates"
          title="受付に使えるひな形がありません"
          description="ひな形を開き、採点基準を確認して「受付を開始」を押してください。"
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
          {selectedTemplate ? (
            <TemplateSessionMetadata template={selectedTemplate} />
          ) : null}
          <div className="form-grid form-grid--2">
            <Field
              label="実施日"
              htmlFor="session-date"
              required
              hint="進捗グラフと帳票にはこの日付が使われます。"
            >
              <input
                id="session-date"
                type="date"
                value={values.testDate}
                onChange={(event) => {
                  idempotencyKeyRef.current = "";
                  setValues({ ...values, testDate: event.target.value });
                }}
                required
              />
            </Field>
            <Field label="クラス" htmlFor="session-class">
              <input
                id="session-class"
                value={values.classLabel}
                onChange={(event) => {
                  idempotencyKeyRef.current = "";
                  setValues({ ...values, classLabel: event.target.value });
                }}
                placeholder="例：中2-A"
              />
            </Field>
          </div>
          <InlineAlert tone="info">
            <p>
              試験名・教科・学年・カテゴリ・コースはひな形から引き継がれます。ここで入力し直す必要はありません。
            </p>
          </InlineAlert>
          <div className="form-actions">
            <Button
              type="button"
              variant="secondary"
              onClick={closeDialog}
              disabled={working}
            >
              キャンセル
            </Button>
            <Button
              type="submit"
              disabled={
                working ||
                !values.templateVersionId ||
                !values.testDate
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

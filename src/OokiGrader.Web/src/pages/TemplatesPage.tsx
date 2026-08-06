import { useMemo, useState } from "react";
import { Link, useSearchParams } from "../router";
import { Icon } from "../components/Icon";
import {
  Badge,
  Card,
  EmptyState,
  ErrorState,
  PageHeader,
  SearchInput,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { api, asPaged } from "../lib/api";
import { formatDateTime, formatPoints } from "../lib/format";
import type { PagedResponse, TemplateSummary } from "../types";

export function TemplatesPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [search, setSearch] = useState(searchParams.get("q") || "");
  const state = searchParams.get("state") || "all";
  const templates = useApiQuery<PagedResponse<TemplateSummary>>(
    `templates:${searchParams.toString()}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/templates",
          {
            search: searchParams.get("q"),
            state: state === "all" ? undefined : state,
            pageSize: 100,
          },
          signal,
        ),
      ),
  );

  const subjects = useMemo(
    () =>
      Array.from(
        new Set(
          (templates.data?.items || [])
            .map((template) => template.subject)
            .filter((value): value is string => Boolean(value)),
        ),
      ),
    [templates.data],
  );
  const subject = searchParams.get("subject") || "";

  function submitSearch(value: string) {
    setSearch(value);
    const next = new URLSearchParams(searchParams);
    if (value.trim()) next.set("q", value.trim());
    else next.delete("q");
    setSearchParams(next, { replace: true });
  }

  function setFilter(key: string, value: string) {
    const next = new URLSearchParams(searchParams);
    if (value && value !== "all") next.set(key, value);
    else next.delete(key);
    setSearchParams(next);
  }

  const filteredItems = subject
    ? templates.data?.items.filter((item) => item.subject === subject)
    : templates.data?.items;

  return (
    <div className="page">
      <PageHeader
        eyebrow="採点基準"
        title="テストひな形"
        description="問題用紙と採点基準を版ごとに管理します。"
        actions={
          <Link
            className="button button--primary button--medium"
            to="/templates/new"
          >
            <Icon name="plus" size={18} />
            <span>ひな形を作成</span>
          </Link>
        }
      />
      <Card>
        <div className="list-toolbar">
          <SearchInput
            value={search}
            onChange={submitSearch}
            placeholder="タイトル・教科・カテゴリで検索"
            label="テストひな形を検索"
          />
          <div className="list-toolbar__filters">
            <select
              aria-label="公開状態"
              value={state}
              onChange={(event) => setFilter("state", event.target.value)}
            >
              <option value="all">すべての状態</option>
              <option value="active">公開中</option>
              <option value="draft">下書き</option>
              <option value="retired">利用終了</option>
              <option value="archived">アーカイブ</option>
            </select>
            <select
              aria-label="教科"
              value={subject}
              onChange={(event) => setFilter("subject", event.target.value)}
            >
              <option value="">すべての教科</option>
              {subjects.map((value) => (
                <option value={value} key={value}>
                  {value}
                </option>
              ))}
            </select>
          </div>
          {templates.data ? (
            <span className="result-count">
              {filteredItems?.length || 0}件
            </span>
          ) : null}
        </div>

        {templates.status === "loading" ? (
          <SkeletonRows rows={6} />
        ) : templates.status === "error" ? (
          <ErrorState error={templates.error} onRetry={templates.reload} />
        ) : filteredItems?.length ? (
          <div className="template-grid">
            {filteredItems.map((template) => (
              <TemplateCard template={template} key={template.id} />
            ))}
          </div>
        ) : (
          <EmptyState
            icon="templates"
            title={
              searchParams.toString()
                ? "条件に一致するひな形はありません"
                : "テストひな形がまだありません"
            }
            description={
              searchParams.toString()
                ? "検索条件や公開状態を変更してください。"
                : "問題用紙をアップロードして、採点基準の下書きを作成します。"
            }
          />
        )}
      </Card>
    </div>
  );
}

function TemplateCard({ template }: { template: TemplateSummary }) {
  const editorUrl =
    template.activeVersionId || template.lifecycleState === "draft"
      ? `/templates/${encodeURIComponent(template.id)}/versions/${encodeURIComponent(template.activeVersionId || "draft")}`
      : `/templates/${encodeURIComponent(template.id)}`;
  return (
    <Link className="template-card" to={editorUrl}>
      <div className="template-card__visual">
        <Icon name="templates" size={30} />
        <span>{template.subject || "教科未設定"}</span>
      </div>
      <div className="template-card__body">
        <div className="template-card__meta">
          <StatusBadge status={template.lifecycleState} />
          {template.activeVersionNumber ? (
            <Badge tone="neutral">第{template.activeVersionNumber}版</Badge>
          ) : null}
        </div>
        <h2>{template.title}</h2>
        <p>
          {[template.subject, template.category, template.gradeLabel]
            .filter(Boolean)
            .join("・") || "詳細未設定"}
        </p>
        <div className="template-card__stats">
          <span>
            <strong>{template.questionCount ?? "—"}</strong>
            問
          </span>
          <span>
            <strong>{formatPoints(template.totalPointsMilli)}</strong>
            点
          </span>
          <span className="template-card__updated">
            {formatDateTime(template.updatedAt)} 更新
          </span>
        </div>
      </div>
      <Icon name="chevronRight" size={19} />
    </Link>
  );
}

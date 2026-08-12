import { useState } from "react";
import { Link } from "../router";
import { Icon } from "../components/Icon";
import {
  ActiveFilterSummary,
  facetSuggestions,
  FilterTextInput,
  ListPagination,
  ListSortControls,
} from "../components/ListControls";
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  InlineAlert,
  Modal,
  PageHeader,
  SearchInput,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import { useApiQuery, type ApiQuery } from "../hooks/useApiQuery";
import { useListQueryState } from "../hooks/useListQueryState";
import { useRuntimeCapabilities } from "../hooks/useRuntimeCapabilities";
import { api, asPaged, newIdempotencyKey } from "../lib/api";
import { formatDateTime, formatPoints } from "../lib/format";
import {
  templateGenerationApi,
  testTypeLabel,
} from "../lib/templateGeneration";
import type {
  PagedResponse,
  ResumableTemplateGenerationBatchList,
  TemplateGenerationBatchStatus,
  TemplateGenerationBatchSummary,
  TemplateSummary,
} from "../types";

const TEMPLATE_SORTS = [
  { value: "updatedAt", label: "更新日時", defaultDirection: "desc" },
  { value: "name", label: "タイトル", defaultDirection: "asc" },
  { value: "subject", label: "教科", defaultDirection: "asc" },
] as const;

const TEMPLATE_QUERY_OPTIONS = {
  allowedSorts: [
    "-updatedAt",
    "updatedAt",
    "name",
    "-name",
    "subject",
    "-subject",
  ],
  defaultSort: "-updatedAt",
  enumParams: {
    state: ["draft", "active", "retired", "archived"],
    testType: ["hop", "step", "classPlacement", "other"],
  },
  textParams: ["subject", "category", "course", "grade"],
  defaultPageSize: 50,
} as const;

const TEMPLATE_FILTER_KEYS = [
  "q",
  "state",
  "subject",
  "category",
  "course",
  "grade",
  "testType",
] as const;

export function TemplatesPage() {
  const list = useListQueryState(TEMPLATE_QUERY_OPTIONS);
  const { searchParams } = list;
  const [lifecycleTarget, setLifecycleTarget] = useState<TemplateSummary>();
  const [lifecycleWorking, setLifecycleWorking] = useState(false);
  const [lifecycleError, setLifecycleError] = useState<string>();
  const [lifecycleMessage, setLifecycleMessage] = useState<string>();
  const state = searchParams.get("state") || "";
  const subject = searchParams.get("subject") || "";
  const category = searchParams.get("category") || "";
  const course = searchParams.get("course") || "";
  const grade = searchParams.get("grade") || "";
  const testType = searchParams.get("testType") || "";
  const templates = useApiQuery<PagedResponse<TemplateSummary>>(
    `templates:${searchParams.toString()}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/templates",
          {
            search: searchParams.get("q"),
            state: state || undefined,
            subject: subject || undefined,
            category: category || undefined,
            course: course || undefined,
            grade: grade || undefined,
            testType: testType || undefined,
            sort: list.sort,
            cursor: list.cursor,
            pageSize: list.pageSize,
            includeFacets: true,
          },
          signal,
        ),
      ),
  );
  const capabilities = useRuntimeCapabilities();
  const generationEnabled =
    capabilities.data?.ai.templateGeneration.enabled === true;
  const resumableBatches =
    useApiQuery<ResumableTemplateGenerationBatchList>(
      "template-generation-resumable",
      (signal) => templateGenerationApi.listResumableBatches(signal),
      generationEnabled,
    );

  const subjects = facetSuggestions(
    templates.data?.facets,
    "subjects",
    (templates.data?.items || []).map((template) => template.subject),
  );
  const categories = facetSuggestions(
    templates.data?.facets,
    "categories",
    (templates.data?.items || []).map((template) => template.category),
  );
  const courses = facetSuggestions(
    templates.data?.facets,
    "courses",
    (templates.data?.items || []).map((template) => template.course),
  );
  const grades = facetSuggestions(
    templates.data?.facets,
    "grades",
    (templates.data?.items || []).map((template) => template.gradeLabel),
  );
  const activeFilters = [
    searchParams.get("q")
      ? { key: "q", label: "検索", value: `「${searchParams.get("q")}」` }
      : undefined,
    state
      ? {
          key: "state",
          label: "状態",
          value:
            { draft: "下書き", active: "利用中", retired: "利用終了", archived: "アーカイブ" }[
              state
            ] || state,
        }
      : undefined,
    subject ? { key: "subject", label: "教科", value: subject } : undefined,
    category ? { key: "category", label: "カテゴリ", value: category } : undefined,
    course ? { key: "course", label: "コース", value: course } : undefined,
    grade ? { key: "grade", label: "学年", value: grade } : undefined,
    testType
      ? {
          key: "testType",
          label: "テスト種別",
          value:
            { hop: "HOP", step: "STEP", classPlacement: "クラス分け", other: "その他" }[
              testType
            ] || testType,
        }
      : undefined,
  ].filter((value): value is { key: string; label: string; value: string } => Boolean(value));

  async function changeLifecycle() {
    if (!lifecycleTarget || lifecycleWorking) return;
    const restoring = lifecycleTarget.lifecycleState === "archived";
    setLifecycleWorking(true);
    setLifecycleError(undefined);
    try {
      let revision = lifecycleTarget.revision;
      if (!revision) {
        const latest = await api.get<TemplateSummary>(
          `/templates/${encodeURIComponent(lifecycleTarget.id)}`,
        );
        revision = latest.revision;
      }
      if (!revision) {
        throw new Error("ひな形の更新番号を確認できませんでした。一覧を再読み込みしてください。");
      }

      const etag = `"rev-${revision}"`;
      if (restoring) {
        await api.post(
          `/templates/${encodeURIComponent(lifecycleTarget.id)}:restore`,
          { revision },
          { etag, idempotencyKey: newIdempotencyKey() },
        );
      } else {
        await api.delete(
          `/templates/${encodeURIComponent(lifecycleTarget.id)}`,
          { etag, idempotencyKey: newIdempotencyKey() },
        );
      }

      setLifecycleTarget(undefined);
      setLifecycleMessage(
        restoring
          ? `「${lifecycleTarget.title}」を復元しました。`
          : `「${lifecycleTarget.title}」をアーカイブしました。`,
      );
      templates.reload();
    } catch (reason) {
      setLifecycleError(
        reason instanceof Error
          ? reason.message
          : restoring
            ? "ひな形を復元できませんでした。"
            : "ひな形をアーカイブできませんでした。",
      );
    } finally {
      setLifecycleWorking(false);
    }
  }

  return (
    <div className="page">
      <PageHeader
        eyebrow="採点基準"
        title="テストひな形"
        description="問題用紙と採点基準を版ごとに管理します。"
        actions={
          generationEnabled ? (
            <Link
              className="button button--primary button--medium"
              to="/templates/new"
            >
              <Icon name="plus" size={18} />
              <span>ひな形を作成</span>
            </Link>
          ) : (
            <span
              className="button button--secondary button--medium"
              aria-disabled="true"
              title="テンプレート生成は現在停止しています"
            >
              <Icon name="lock" size={18} />
              <span>ひな形作成は停止中</span>
            </span>
          )
        }
      />
      {lifecycleMessage ? (
        <InlineAlert tone="success">
          <p>{lifecycleMessage}</p>
        </InlineAlert>
      ) : null}
      {generationEnabled ? (
        <ResumableGenerationPanel query={resumableBatches} />
      ) : null}
      <Card>
        <div className="list-toolbar">
          <SearchInput
            value={list.search}
            onChange={list.setSearch}
            placeholder="タイトル・教科・カテゴリで検索"
            label="テストひな形を検索"
          />
          <ListSortControls
            value={list.sort}
            options={TEMPLATE_SORTS}
            defaultValue="-updatedAt"
            onChange={(value) => list.updateParam("sort", value)}
          />
          {templates.data ? (
            <span className="result-count">
              約{templates.data.totalApproximate ?? templates.data.items.length}件
            </span>
          ) : null}
        </div>

        <div className="list-filter-panel" aria-label="テストひな形の絞り込み">
          <label className="filter-field">
            <span>ひな形の状態</span>
            <select
              value={state}
              onChange={(event) => list.updateParam("state", event.target.value)}
            >
              <option value="">通常（アーカイブ以外）</option>
              <option value="active">利用中</option>
              <option value="draft">下書き</option>
              <option value="retired">利用終了</option>
              <option value="archived">アーカイブ</option>
            </select>
          </label>
          <FilterTextInput
            label="教科"
            value={subject}
            suggestions={subjects}
            onCommit={(value) => list.updateParam("subject", value)}
          />
          <FilterTextInput
            label="カテゴリ"
            value={category}
            suggestions={categories}
            onCommit={(value) => list.updateParam("category", value)}
          />
          <FilterTextInput
            label="コース"
            value={course}
            suggestions={courses}
            onCommit={(value) => list.updateParam("course", value)}
          />
          <FilterTextInput
            label="学年"
            value={grade}
            suggestions={grades}
            onCommit={(value) => list.updateParam("grade", value)}
          />
          <label className="filter-field">
            <span>テスト種別</span>
            <select
              value={testType}
              onChange={(event) => list.updateParam("testType", event.target.value)}
            >
              <option value="">すべての種別</option>
              <option value="hop">HOP</option>
              <option value="step">STEP</option>
              <option value="classPlacement">クラス分け</option>
              <option value="other">その他</option>
            </select>
          </label>
        </div>
        <ActiveFilterSummary
          filters={activeFilters}
          onClear={() => list.clearFilters(TEMPLATE_FILTER_KEYS)}
        />

        {templates.status === "loading" ? (
          <SkeletonRows rows={6} />
        ) : templates.status === "error" ? (
          <ErrorState error={templates.error} onRetry={templates.reload} />
        ) : templates.data?.items.length ? (
          <div className="template-grid">
            {templates.data.items.map((template) => (
              <TemplateCard
                template={template}
                onLifecycleAction={() => {
                  setLifecycleError(undefined);
                  setLifecycleMessage(undefined);
                  setLifecycleTarget(template);
                }}
                key={template.id}
              />
            ))}
          </div>
        ) : (
          <EmptyState
            icon="templates"
            title={
              activeFilters.length
                ? "条件に一致するひな形はありません"
                : "テストひな形がまだありません"
            }
            description={
              activeFilters.length
                ? "検索条件やひな形の状態を変更してください。"
                : "問題用紙をアップロードして、採点基準の下書きを作成します。"
            }
          />
        )}
        <ListPagination
          page={list.page}
          pageSize={list.pageSize}
          itemCount={templates.data?.items.length || 0}
          totalApproximate={templates.data?.totalApproximate}
          hasNext={list.canNavigateNext(templates.data?.nextCursor)}
          nextBlockedReason={
            templates.data?.nextCursor && !list.canNavigateNext(templates.data.nextCursor)
              ? "これ以上は絞り込みを追加するか、1ページの件数を増やしてください。"
              : undefined
          }
          canGoPrevious={list.canGoPrevious}
          onNext={() => list.nextPage(templates.data?.nextCursor)}
          onPrevious={list.previousPage}
          onPageSizeChange={list.setPageSize}
        />
      </Card>
      <Modal
        open={Boolean(lifecycleTarget)}
        onClose={() => !lifecycleWorking && setLifecycleTarget(undefined)}
        title={
          lifecycleTarget?.lifecycleState === "archived"
            ? `「${lifecycleTarget.title}」を復元しますか？`
            : `「${lifecycleTarget?.title || "このひな形"}」をアーカイブしますか？`
        }
        description={
          lifecycleTarget?.lifecycleState === "archived"
            ? "ひな形を通常の運用対象に戻します。"
            : "新しいテスト実施では選べなくなります。"
        }
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setLifecycleTarget(undefined)}
              disabled={lifecycleWorking}
            >
              キャンセル
            </Button>
            <Button
              variant={
                lifecycleTarget?.lifecycleState === "archived"
                  ? "primary"
                  : "danger"
              }
              onClick={() => void changeLifecycle()}
              disabled={lifecycleWorking}
            >
              {lifecycleWorking
                ? "変更しています…"
                : lifecycleTarget?.lifecycleState === "archived"
                  ? "復元する"
                  : "アーカイブする"}
            </Button>
          </>
        }
      >
        {lifecycleError ? (
          <InlineAlert tone="danger">
            <p>{lifecycleError}</p>
          </InlineAlert>
        ) : null}
        <p>
          {lifecycleTarget?.lifecycleState === "archived"
            ? "保存されている版と採点基準はそのまま利用できます。"
            : "ひな形の版、過去のテスト実施、答案、採点結果は削除されません。後から復元できます。"}
        </p>
      </Modal>
    </div>
  );
}

function ResumableGenerationPanel({
  query,
}: {
  query: ApiQuery<ResumableTemplateGenerationBatchList>;
}) {
  const batches = query.data?.items ?? [];
  if (query.status === "loading" && !query.data) {
    return (
      <Card className="resumable-generation-panel is-loading">
        <span className="spinner" aria-hidden="true" />
        <div>
          <strong>作成中のひな形を確認しています</strong>
          <small>画面を離れて続行している生成も、ここから開けます。</small>
        </div>
      </Card>
    );
  }

  if (query.status === "error") {
    return (
      <InlineAlert
        tone="warning"
        title="作成中のひな形を確認できませんでした"
        action={
          <Button size="small" variant="secondary" onClick={query.reload}>
            再読み込み
          </Button>
        }
      >
        <p>生成処理は止まりません。しばらくしてから一覧を更新してください。</p>
      </InlineAlert>
    );
  }

  if (!batches.length) {
    return query.data?.browserRecoveryOnly ? (
      <InlineAlert
        tone="info"
        title="このブラウザに記録された作成中のひな形はありません"
      >
        <p>生成処理は画面を離れても続きます。次回の作成から、この一覧に表示されます。</p>
      </InlineAlert>
    ) : null;
  }

  return (
    <Card className="resumable-generation-panel">
      <div className="resumable-generation-panel__heading">
        <div>
          <span className="resumable-generation-panel__icon">
            <Icon name="spark" size={20} />
          </span>
          <div>
            <h2>作成中・確認待ちのひな形</h2>
            <p>生成ページを閉じても処理は続きます。ここからいつでも戻れます。</p>
          </div>
        </div>
        <Button size="small" variant="quiet" onClick={query.reload}>
          状況を更新
        </Button>
      </div>
      {query.data?.browserRecoveryOnly ? (
        <div className="resumable-generation-panel__recovery-note">
          <Icon name="info" size={15} />
          <span>現在は、このブラウザで開始・表示した作業を復元しています。</span>
        </div>
      ) : null}
      <div className="resumable-generation-list">
        {batches.map((batch) => (
          <ResumableGenerationRow batch={batch} key={batch.id} />
        ))}
      </div>
    </Card>
  );
}

function ResumableGenerationRow({
  batch,
}: {
  batch: TemplateGenerationBatchSummary;
}) {
  const completed = Math.min(
    batch.expectedUnitCount,
    Math.max(0, batch.completedUnitCount),
  );
  const expected = Math.max(1, batch.expectedUnitCount);
  const progress = Math.round((completed / expected) * 100);
  const finalCheck = batch.status === "needsFinalCheck";
  const destination = finalCheck
    ? `/templates/generation/${encodeURIComponent(batch.id)}/final-check`
    : `/templates/generation/${encodeURIComponent(batch.id)}`;
  const status = resumableStatusPresentation(batch.status);

  return (
    <article className={`resumable-generation-row is-${batch.status}`}>
      <div className="resumable-generation-row__summary">
        <div className="resumable-generation-row__meta">
          <Badge tone={status.tone} dot>
            {status.label}
          </Badge>
          <span>{testTypeLabel(batch.testType)}</span>
          <span>{batch.subject}</span>
          <span>{batch.sourcePageCount}ページ</span>
        </div>
        <strong>{status.detail}</strong>
        <small>
          {batch.status === "failed" && batch.failedUnitCount > 0
            ? `${batch.failedUnitCount}件失敗・成功済みは保持されています`
            : `${completed} / ${batch.expectedUnitCount}件 完了`}
          {batch.updatedAt ? `・${formatDateTime(batch.updatedAt)} 更新` : ""}
        </small>
        <div
          className="resumable-generation-row__progress"
          role="progressbar"
          aria-label={`${testTypeLabel(batch.testType)} ${batch.subject}の生成進捗`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={progress}
        >
          <span style={{ width: `${progress}%` }} />
        </div>
      </div>
      <Link className="button button--secondary button--small" to={destination}>
        <span>{status.action}</span>
        <Icon name="arrowRight" size={16} />
      </Link>
    </article>
  );
}

function resumableStatusPresentation(status: TemplateGenerationBatchStatus): {
  label: string;
  detail: string;
  action: string;
  tone: "neutral" | "info" | "success" | "warning" | "danger";
} {
  switch (status) {
    case "draft":
      return {
        label: "開始待ち",
        detail: "PDFの分割予定を保存しています",
        action: "作成予定を開く",
        tone: "neutral",
      };
    case "validating":
      return {
        label: "準備中",
        detail: "PDFを分割しています",
        action: "生成状況を見る",
        tone: "info",
      };
    case "generating":
      return {
        label: "生成中",
        detail: "問題・正答・配点を生成しています",
        action: "生成状況を見る",
        tone: "info",
      };
    case "needsFinalCheck":
      return {
        label: "確認待ち",
        detail: "生成が終わりました。テスト名と学年を確認してください",
        action: "最終確認へ",
        tone: "success",
      };
    case "confirming":
      return {
        label: "保存中",
        detail: "確認済みのひな形を保存しています",
        action: "保存状況を見る",
        tone: "info",
      };
    case "failed":
      return {
        label: "要確認",
        detail: "生成できなかった項目があります",
        action: "確認・再試行へ",
        tone: "danger",
      };
    default:
      return {
        label: "確認中",
        detail: "現在の状態を確認してください",
        action: "詳細を見る",
        tone: "warning",
      };
  }
}

function TemplateCard({
  template,
  onLifecycleAction,
}: {
  template: TemplateSummary;
  onLifecycleAction: () => void;
}) {
  const editorUrl =
    template.activeVersionId || template.lifecycleState === "draft"
      ? `/templates/${encodeURIComponent(template.id)}/versions/${encodeURIComponent(template.activeVersionId || "draft")}`
      : `/templates/${encodeURIComponent(template.id)}`;
  return (
    <article className="template-card">
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
      <div className="template-card__actions">
        <Button
          variant="quiet"
          size="small"
          onClick={onLifecycleAction}
        >
          {template.lifecycleState === "archived" ? "復元" : "アーカイブ"}
        </Button>
        <Icon name="chevronRight" size={19} />
      </div>
      <Link
        className="template-card__open"
        to={editorUrl}
        aria-label={`「${template.title}」を開く`}
      />
    </article>
  );
}

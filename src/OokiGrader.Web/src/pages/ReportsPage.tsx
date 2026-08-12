import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon } from "../components/Icon";
import {
  ActiveFilterSummary,
  facetOptions,
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
  LoadingState,
  Modal,
  PageHeader,
  Score,
  SearchInput,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { useListQueryState } from "../hooks/useListQueryState";
import { ApiError, api, asPaged, newIdempotencyKey } from "../lib/api";
import {
  formatDate,
  formatPercentageBasisPoints,
  formatPoints,
} from "../lib/format";
import type { PagedResponse, SubmissionSummary } from "../types";

interface FinalizedSubmission extends SubmissionSummary {
  testTitle?: string;
  testDate?: string;
  finalizedAt?: string;
  percentageBasisPoints?: number;
  exportState?: string;
  templateId?: string;
  subject?: string;
  category?: string;
  course?: string;
  classLabel?: string;
}

interface SelectedResult {
  submissionId: string;
  studentId?: string;
  studentName: string;
}

type TranscriptFilter = {
  search?: string;
  from?: string;
  to?: string;
  studentId?: string;
  templateId?: string;
  subject?: string;
  category?: string;
  course?: string;
  class?: string;
  sort: string;
};

type TranscriptSelector =
  | { submissionIds: string[] }
  | { filter: TranscriptFilter };

interface TranscriptExportPreview {
  normalizedSelector: TranscriptSelector;
  studentCount: number;
  resultCount: number;
  sourceFingerprint: string;
}

interface TranscriptExportStatus {
  id: string;
  state: "queued" | "rendering" | "verified" | "failed" | "superseded" | string;
  progressBasisPoints?: number;
  processedResultCount?: number;
  studentCount: number;
  resultCount: number;
  sourceFingerprint: string;
  createdAt?: string;
  startedAt?: string;
  completedAt?: string;
  errorCode?: string;
  safeErrorDetail?: string;
  fileUrl?: string;
  normalizedSelector?: TranscriptSelector;
}

type ExportMode = "rows" | "filtered";
type ExportErrorStage =
  | "preview"
  | "createRetryable"
  | "createInvalidated";

const REPORT_SORTS = [
  { value: "testDate", label: "実施日", defaultDirection: "desc" },
  { value: "finalizedAt", label: "確定日時", defaultDirection: "desc" },
  { value: "studentName", label: "生徒名", defaultDirection: "asc" },
  { value: "testTitle", label: "テスト名", defaultDirection: "asc" },
] as const;

const REPORT_QUERY_OPTIONS = {
  allowedSorts: [
    "-testDate",
    "testDate",
    "-finalizedAt",
    "finalizedAt",
    "studentName",
    "-studentName",
    "testTitle",
    "-testTitle",
  ],
  defaultSort: "-testDate",
  dateParams: ["from", "to"],
  textParams: [
    "studentId",
    "templateId",
    "subject",
    "category",
    "course",
    "class",
  ],
  auxiliaryIdParams: ["bulkExport"],
  defaultPageSize: 50,
} as const;

const REPORT_FILTER_KEYS = [
  "q",
  "from",
  "to",
  "studentId",
  "templateId",
  "subject",
  "category",
  "course",
  "class",
] as const;
const MAX_EXPLICIT_RESULT_SELECTION = 500;

export function ReportsPage() {
  const { hasAnyRole } = useSession();
  const canBulkExport = hasAnyRole("administrator", "teacher");
  const list = useListQueryState(REPORT_QUERY_OPTIONS);
  const { searchParams } = list;
  const from = searchParams.get("from") || "";
  const to = searchParams.get("to") || "";
  const studentId = searchParams.get("studentId") || "";
  const templateId = searchParams.get("templateId") || "";
  const subject = searchParams.get("subject") || "";
  const category = searchParams.get("category") || "";
  const course = searchParams.get("course") || "";
  const classFilter = searchParams.get("class") || "";
  const bulkExportId = searchParams.get("bulkExport") || "";
  const [selectedRows, setSelectedRows] = useState<Map<string, SelectedResult>>(
    () => new Map(),
  );
  const [selectionWarning, setSelectionWarning] = useState<string>();
  const [exportOpen, setExportOpen] = useState(false);
  const [exportMode, setExportMode] = useState<ExportMode>("rows");
  const [preview, setPreview] = useState<TranscriptExportPreview>();
  const [previewing, setPreviewing] = useState(false);
  const [creatingExport, setCreatingExport] = useState(false);
  const [exportStatus, setExportStatus] = useState<TranscriptExportStatus>();
  const [exportError, setExportError] = useState<Error>();
  const [exportErrorStage, setExportErrorStage] = useState<ExportErrorStage>();
  const [pollError, setPollError] = useState<Error>();
  const [recoveryError, setRecoveryError] = useState<Error>();
  const [acknowledged, setAcknowledged] = useState(false);
  const selectPageRef = useRef<HTMLInputElement>(null);
  const createIdempotencyKeyRef = useRef<string | undefined>(undefined);
  const lastPreviewSelectorRef = useRef<TranscriptSelector | undefined>(undefined);
  const closeExport = useCallback(() => setExportOpen(false), []);

  const reportListParams = new URLSearchParams(searchParams);
  reportListParams.delete("bulkExport");
  const results = useApiQuery<PagedResponse<FinalizedSubmission>>(
    `reports:${reportListParams.toString()}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/submissions",
          {
            state: "finalized",
            finalizedOnly: true,
            search: searchParams.get("q"),
            from: from || undefined,
            to: to || undefined,
            studentId: studentId || undefined,
            templateId: templateId || undefined,
            subject: subject || undefined,
            category: category || undefined,
            course: course || undefined,
            class: classFilter || undefined,
            sort: list.sort,
            cursor: list.cursor,
            pageSize: list.pageSize,
            includeFacets: true,
          },
          signal,
        ),
      ),
  );

  useEffect(() => {
    setSelectedRows(new Map());
    setSelectionWarning(undefined);
  }, [list.filterFingerprint]);

  useEffect(() => {
    if (!bulkExportId || exportStatus?.id === bulkExportId) return;
    let disposed = false;
    setRecoveryError(undefined);
    api
      .get<TranscriptExportStatus>(
        `/transcript-exports/${encodeURIComponent(bulkExportId)}`,
      )
      .then((status) => {
        if (!disposed) {
          setExportStatus(status);
          if (status.normalizedSelector) {
            setExportMode(modeForSelector(status.normalizedSelector));
          }
        }
      })
      .catch((reason: unknown) => {
        if (!disposed) {
          setRecoveryError(
            toError(reason, "前回の一括出力状況を読み込めませんでした。"),
          );
        }
      });
    return () => {
      disposed = true;
    };
  }, [bulkExportId, exportStatus?.id]);

  const students = facetOptions(
    results.data?.facets,
    "students",
    (results.data?.items || [])
      .filter((result) => Boolean(result.studentId))
      .map((result) => ({
        value: result.studentId || "",
        label: result.studentDisplayName || result.studentNumber || result.studentId || "",
      })),
  );
  const templates = facetOptions(
    results.data?.facets,
    "templates",
    (results.data?.items || [])
      .filter((result) => Boolean(result.templateId))
      .map((result) => ({
        value: result.templateId || "",
        label: result.testTitle || result.templateId || "",
      })),
  );
  const subjects = facetSuggestions(
    results.data?.facets,
    "subjects",
    (results.data?.items || []).map((result) => result.subject),
  );
  const categories = facetSuggestions(
    results.data?.facets,
    "categories",
    (results.data?.items || []).map((result) => result.category),
  );
  const courses = facetSuggestions(
    results.data?.facets,
    "courses",
    (results.data?.items || []).map((result) => result.course),
  );
  const classes = facetSuggestions(
    results.data?.facets,
    "classes",
    (results.data?.items || []).map((result) => result.classLabel),
  );
  const activeFilters = [
    searchParams.get("q")
      ? { key: "q", label: "検索", value: `「${searchParams.get("q")}」` }
      : undefined,
    from ? { key: "from", label: "開始日", value: from } : undefined,
    to ? { key: "to", label: "終了日", value: to } : undefined,
    studentId
      ? {
          key: "studentId",
          label: "生徒",
          value: students.find((item) => item.value === studentId)?.label || studentId,
        }
      : undefined,
    templateId
      ? {
          key: "templateId",
          label: "ひな形",
          value: templates.find((item) => item.value === templateId)?.label || templateId,
        }
      : undefined,
    subject ? { key: "subject", label: "教科", value: subject } : undefined,
    category ? { key: "category", label: "カテゴリ", value: category } : undefined,
    course ? { key: "course", label: "コース", value: course } : undefined,
    classFilter ? { key: "class", label: "クラス", value: classFilter } : undefined,
  ].filter((value): value is { key: string; label: string; value: string } => Boolean(value));

  const pageRows = results.data?.items || [];
  const selectablePageRows = pageRows.filter((result) => Boolean(result.studentId));
  const allPageSelected =
    selectablePageRows.length > 0 &&
    selectablePageRows.every((result) => selectedRows.has(result.id));
  const somePageSelected = selectablePageRows.some((result) =>
    selectedRows.has(result.id),
  );
  useEffect(() => {
    if (selectPageRef.current) {
      selectPageRef.current.indeterminate = somePageSelected && !allPageSelected;
    }
  }, [allPageSelected, somePageSelected]);

  const selectedStudentIds = useMemo(
    () =>
      Array.from(
        new Set(
          Array.from(selectedRows.values())
            .map((row) => row.studentId)
            .filter((value): value is string => Boolean(value)),
        ),
      ).sort(),
    [selectedRows],
  );

  function selectedResult(result: FinalizedSubmission): SelectedResult {
    return {
      submissionId: result.id,
      studentId: result.studentId || undefined,
      studentName: result.studentDisplayName || "未割り当て",
    };
  }

  function toggleResult(result: FinalizedSubmission, checked: boolean) {
    if (
      checked &&
      !selectedRows.has(result.id) &&
      selectedRows.size >= MAX_EXPLICIT_RESULT_SELECTION
    ) {
      setSelectionWarning("一度に選択できる結果は500件までです。条件を絞ってください。");
      return;
    }
    setSelectionWarning(undefined);
    setSelectedRows((current) => {
      const next = new Map(current);
      if (checked) next.set(result.id, selectedResult(result));
      else next.delete(result.id);
      return next;
    });
  }

  function toggleCurrentPage(checked: boolean) {
    const additions = selectablePageRows.filter(
      (result) => !selectedRows.has(result.id),
    );
    if (
      checked &&
      selectedRows.size + additions.length > MAX_EXPLICIT_RESULT_SELECTION
    ) {
      setSelectionWarning(
        "500件まで選択しました。残りは条件を絞って別のZIPにしてください。",
      );
    } else {
      setSelectionWarning(undefined);
    }
    setSelectedRows((current) => {
      const next = new Map(current);
      selectablePageRows.forEach((result) => {
        if (checked) {
          if (next.has(result.id) || next.size < MAX_EXPLICIT_RESULT_SELECTION) {
            next.set(result.id, selectedResult(result));
          }
        } else {
          next.delete(result.id);
        }
      });
      return next;
    });
  }

  function buildSelector(mode: ExportMode): TranscriptSelector {
    if (mode === "rows") {
      return { submissionIds: Array.from(selectedRows.keys()).sort() };
    }
    return {
      filter: {
        search: searchParams.get("q") || undefined,
        from: from || undefined,
        to: to || undefined,
        studentId: studentId || undefined,
        templateId: templateId || undefined,
        subject: subject || undefined,
        category: category || undefined,
        course: course || undefined,
        class: classFilter || undefined,
        sort: list.sort,
      },
    };
  }

  async function beginExport(
    mode: ExportMode,
    selectorOverride?: TranscriptSelector,
  ) {
    if (!canBulkExport) return;
    const selector = selectorOverride || buildSelector(mode);
    list.updateParam("bulkExport", undefined, { preservePage: true });
    createIdempotencyKeyRef.current = undefined;
    lastPreviewSelectorRef.current = selector;
    setExportMode(mode);
    setExportOpen(true);
    setPreview(undefined);
    setExportStatus(undefined);
    setExportError(undefined);
    setExportErrorStage(undefined);
    setPollError(undefined);
    setRecoveryError(undefined);
    setAcknowledged(false);
    setPreviewing(true);
    try {
      const nextPreview = await api.post<TranscriptExportPreview>(
        "/transcript-exports:preview",
        { selector },
        { idempotency: false },
      );
      setPreview(nextPreview);
    } catch (reason) {
      setExportError(toError(reason, "出力対象を確認できませんでした。"));
      setExportErrorStage("preview");
    } finally {
      setPreviewing(false);
    }
  }

  async function createExport() {
    if (!canBulkExport || !preview || !acknowledged || creatingExport) return;
    setCreatingExport(true);
    setExportError(undefined);
    setExportErrorStage(undefined);
    try {
      const idempotencyKey =
        createIdempotencyKeyRef.current || newIdempotencyKey();
      createIdempotencyKeyRef.current = idempotencyKey;
      const created = await api.post<TranscriptExportStatus>(
        "/transcript-exports",
        {
          sourceFingerprint: preview.sourceFingerprint,
          selector: preview.normalizedSelector,
        },
        { idempotencyKey },
      );
      setExportStatus(created);
      list.updateParam("bulkExport", created.id, { preservePage: true });
    } catch (reason) {
      setExportError(toError(reason, "一括出力を開始できませんでした。"));
      if (isDefinitiveCreateFailure(reason)) {
        createIdempotencyKeyRef.current = undefined;
        setPreview(undefined);
        setAcknowledged(false);
        setExportErrorStage("createInvalidated");
      } else {
        setExportErrorStage("createRetryable");
      }
    } finally {
      setCreatingExport(false);
    }
  }

  async function refreshExportStatus() {
    if (!exportStatus?.id) return;
    setPollError(undefined);
    try {
      const latest = await api.get<TranscriptExportStatus>(
        `/transcript-exports/${encodeURIComponent(exportStatus.id)}`,
      );
      setExportStatus(latest);
    } catch (reason) {
      setPollError(toError(reason, "出力状況を確認できませんでした。"));
    }
  }

  useEffect(() => {
    if (!exportStatus?.id || !["queued", "rendering"].includes(exportStatus.state)) {
      return;
    }
    let disposed = false;
    const timer = window.setTimeout(async () => {
      try {
        const latest = await api.get<TranscriptExportStatus>(
          `/transcript-exports/${encodeURIComponent(exportStatus.id)}`,
        );
        if (!disposed) {
          setPollError(undefined);
          setExportStatus(latest);
        }
      } catch (reason) {
        if (!disposed) {
          setPollError(toError(reason, "出力状況を確認できませんでした。"));
        }
      }
    }, 1_500);
    return () => {
      disposed = true;
      window.clearTimeout(timer);
    };
  }, [exportStatus]);

  const exportInProgress =
    exportStatus && ["queued", "rendering"].includes(exportStatus.state);
  const exportRecoveryPending = Boolean(
    bulkExportId && exportStatus?.id !== bulkExportId && !recoveryError,
  );
  const createOutcomeUnknown = Boolean(
    exportErrorStage === "createRetryable" && preview && !exportStatus,
  );

  return (
    <div className="page">
      <PageHeader
        eyebrow="結果・帳票"
        title="帳票"
        description="確定済み結果を探し、生徒別結果PDFをまとめて出力できます。"
      />
      {exportStatus && (!bulkExportId || exportStatus.id === bulkExportId) ? (
        <InlineAlert
          tone={
            exportStatus.state === "verified"
              ? "success"
              : exportStatus.state === "failed" || exportStatus.state === "superseded"
                ? "danger"
                : "info"
          }
          title="生徒別結果PDFの一括出力"
          action={
            <Button variant="secondary" size="small" onClick={() => setExportOpen(true)}>
              状況を開く
            </Button>
          }
        >
          <p>
            {exportStatus.state === "verified"
              ? `${exportStatus.resultCount}件のZIPをダウンロードできます。`
              : exportStatus.state === "failed" || exportStatus.state === "superseded"
                ? "一括出力を完了できませんでした。"
                : `${exportStatus.processedResultCount || 0} / ${exportStatus.resultCount}件を作成中です。`}
          </p>
        </InlineAlert>
      ) : null}
      {exportRecoveryPending ? (
        <InlineAlert tone="info" title="前回の一括出力状況を確認しています">
          <p>確認が終わるまで、この画面でお待ちください。</p>
        </InlineAlert>
      ) : null}
      {recoveryError ? (
        <InlineAlert
          tone="danger"
          title="前回の一括出力状況を読み込めませんでした"
          action={
            <Button
              variant="secondary"
              size="small"
              onClick={() => {
                setRecoveryError(undefined);
                list.updateParam("bulkExport", undefined, { preservePage: true });
              }}
            >
              状況表示を解除
            </Button>
          }
        >
          <p>{recoveryError.message}</p>
        </InlineAlert>
      ) : null}
      {createOutcomeUnknown ? (
        <InlineAlert
          tone="warning"
          title="一括出力の開始結果を確認できません"
          action={
            <Button
              variant="secondary"
              size="small"
              onClick={() => setExportOpen(true)}
            >
              同じ操作を再確認
            </Button>
          }
        >
          <p>重複作成を防ぐため、同じ操作を再確認してから続けてください。</p>
        </InlineAlert>
      ) : null}
      <Card>
        <div className="list-toolbar reports-toolbar">
          <SearchInput
            value={list.search}
            onChange={list.setSearch}
            placeholder="生徒名・番号・テスト名で検索"
            label="確定結果を検索"
          />
          <ListSortControls
            value={list.sort}
            options={REPORT_SORTS}
            defaultValue="-testDate"
            onChange={(value) => list.updateParam("sort", value)}
          />
          {results.data ? (
            <span className="result-count">
              約{results.data.totalApproximate ?? results.data.items.length}件
            </span>
          ) : null}
        </div>
        <div className="list-filter-panel" aria-label="確定結果の絞り込み">
          <label className="filter-field">
            <span>生徒</span>
            <select
              value={studentId}
              onChange={(event) => list.updateParam("studentId", event.target.value)}
            >
              <option value="">すべての生徒</option>
              {studentId && !students.some((item) => item.value === studentId) ? (
                <option value={studentId}>{studentId}</option>
              ) : null}
              {students.map((item) => (
                <option value={item.value} key={item.value}>
                  {item.label}
                </option>
              ))}
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
            label="クラス"
            value={classFilter}
            suggestions={classes}
            onCommit={(value) => list.updateParam("class", value)}
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
          onClear={() => list.clearFilters(REPORT_FILTER_KEYS)}
        />

        {canBulkExport ? <div className="bulk-export-bar">
          <div>
            <strong>{selectedRows.size}件の結果を選択中</strong>
            <span>
              {selectedStudentIds.length}名
              {selectedRows.size > selectedStudentIds.length
                ? "（同じ生徒の複数結果を含みます）"
                : ""}
            </span>
            {selectionWarning ? <span role="alert">{selectionWarning}</span> : null}
          </div>
          <div>
            <Button
              variant="secondary"
              size="small"
              disabled={
                !selectedRows.size ||
                Boolean(exportInProgress) ||
                exportRecoveryPending ||
                createOutcomeUnknown
              }
              onClick={() => void beginExport("rows")}
            >
              選択した結果を一括出力
            </Button>
            <Button
              size="small"
              disabled={
                Boolean(exportInProgress) ||
                exportRecoveryPending ||
                createOutcomeUnknown ||
                (!results.data?.totalApproximate && !results.data?.items.length)
              }
              onClick={() => void beginExport("filtered")}
            >
              絞り込み結果を一括出力
            </Button>
          </div>
        </div> : null}

        {results.status === "loading" ? (
          <SkeletonRows rows={7} />
        ) : results.status === "error" ? (
          <ErrorState error={results.error} onRetry={results.reload} />
        ) : results.data?.items.length ? (
          <div className="table-scroll">
            <table className={`reports-table${canBulkExport ? " reports-table--selectable" : ""}`}>
              <thead>
                <tr>
                  {canBulkExport ? <th className="selection-cell">
                    <input
                      ref={selectPageRef}
                      type="checkbox"
                      aria-label="このページの出力可能な結果をすべて選択"
                      checked={allPageSelected}
                      disabled={!selectablePageRows.length}
                      onChange={(event) => toggleCurrentPage(event.target.checked)}
                    />
                  </th> : null}
                  <th>実施日</th>
                  <th>生徒</th>
                  <th>テスト</th>
                  <th>得点</th>
                  <th>得点率</th>
                  <th>画像</th>
                  <th>結果PDF</th>
                  <th>
                    <span className="sr-only">詳細</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {results.data.items.map((result) => {
                  const percentage =
                    result.percentageBasisPoints ??
                    (result.totalPossiblePointsMilli
                      ? ((result.totalEarnedPointsMilli || 0) /
                          result.totalPossiblePointsMilli) *
                        10_000
                      : undefined);
                  return (
                    <tr key={result.id} className={selectedRows.has(result.id) ? "is-selected" : ""}>
                      {canBulkExport ? <td className="selection-cell">
                        <input
                          type="checkbox"
                          aria-label={`${result.studentDisplayName || "未割り当て"}・${result.testTitle || result.fileName || "テスト"}を選択`}
                          checked={selectedRows.has(result.id)}
                          disabled={!result.studentId}
                          title={
                            result.studentId
                              ? undefined
                              : "生徒が割り当てられていない結果は一括出力できません"
                          }
                          onChange={(event) => toggleResult(result, event.target.checked)}
                        />
                      </td> : null}
                      <td>{formatDate(result.testDate)}</td>
                      <td>
                        <strong>{result.studentDisplayName || "未割り当て"}</strong>
                        {result.studentNumber ? <small>{result.studentNumber}</small> : null}
                      </td>
                      <td>{result.testTitle || result.fileName || "テスト"}</td>
                      <td>
                        <Score
                          compact
                          earned={formatPoints(result.totalEarnedPointsMilli)}
                          possible={formatPoints(result.totalPossiblePointsMilli)}
                        />
                      </td>
                      <td>{formatPercentageBasisPoints(percentage)}</td>
                      <td>
                        {result.scanPayloadState === "scan_deleted" ? (
                          <StatusBadge status="scan_deleted" />
                        ) : (
                          <Badge tone="neutral">保存中</Badge>
                        )}
                      </td>
                      <td>
                        {result.exportState ? (
                          <StatusBadge status={result.exportState} />
                        ) : (
                          <span className="muted">未作成</span>
                        )}
                      </td>
                      <td className="table-action">
                        <Link
                          to={`/results/${encodeURIComponent(result.id)}`}
                          aria-label={`${result.studentDisplayName || "生徒"}の結果を開く`}
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
            icon="reports"
            title={
              activeFilters.length
                ? "条件に一致する確定結果はありません"
                : "確定済みの結果はまだありません"
            }
            description={
              activeFilters.length
                ? "検索条件や期間を変更してください。"
                : "答案を確定すると、ここから結果PDFを作成できます。"
            }
          />
        )}
        <ListPagination
          page={list.page}
          pageSize={list.pageSize}
          itemCount={results.data?.items.length || 0}
          totalApproximate={results.data?.totalApproximate}
          hasNext={list.canNavigateNext(results.data?.nextCursor)}
          nextBlockedReason={
            results.data?.nextCursor && !list.canNavigateNext(results.data.nextCursor)
              ? "これ以上は絞り込みを追加するか、1ページの件数を増やしてください。"
              : undefined
          }
          canGoPrevious={list.canGoPrevious}
          onNext={() => list.nextPage(results.data?.nextCursor)}
          onPrevious={list.previousPage}
          onPageSizeChange={list.setPageSize}
        />
      </Card>

      <Modal
        open={exportOpen}
        onClose={closeExport}
        title="生徒別結果PDFを一括出力"
        description="対象を確認してから、PDFと一覧CSVをZIPにまとめます。"
        size="large"
        footer={
          canBulkExport && preview && !exportStatus ? (
            <>
              <Button variant="secondary" onClick={closeExport} disabled={creatingExport}>
                キャンセル
              </Button>
              <Button onClick={() => void createExport()} disabled={!acknowledged || creatingExport}>
                {creatingExport ? "開始しています…" : "この対象で一括出力を開始"}
              </Button>
            </>
          ) : exportStatus?.state === "verified" ? (
            <>
              <Button variant="secondary" onClick={closeExport}>
                閉じる
              </Button>
              <a
                className="button button--primary button--medium"
                href={`/api/v1/transcript-exports/${encodeURIComponent(exportStatus.id)}/file`}
                download
              >
                ZIPをダウンロード
              </a>
            </>
          ) : undefined
        }
      >
        {previewing ? <LoadingState label="対象件数を確認しています" /> : null}
        {exportError ? (
          <ErrorState
            error={exportError}
            title={
              exportErrorStage === "createRetryable"
                ? "開始結果を確認できませんでした"
                : "一括出力を準備できませんでした"
            }
            onRetry={
              exportErrorStage === "createRetryable"
                ? () => void createExport()
                : () =>
                    void beginExport(
                      exportMode,
                      lastPreviewSelectorRef.current,
                    )
            }
            compact
          />
        ) : null}
        {preview && !exportStatus ? (
          <div className="bulk-export-confirmation">
            <div className="bulk-export-counts">
              <div>
                <span>対象の生徒</span>
                <strong>{preview.studentCount}名</strong>
              </div>
              <div>
                <span>対象の確定結果</span>
                <strong>{preview.resultCount}件</strong>
              </div>
            </div>
            <InlineAlert tone={exportMode === "rows" ? "info" : "warning"}>
              <p>{exportConfirmationText(exportMode, activeFilters.length > 0)}</p>
            </InlineAlert>
            <label className="confirmation-checkbox">
              <input
                type="checkbox"
                checked={acknowledged}
                onChange={(event) => setAcknowledged(event.target.checked)}
              />
              <span>
                上記の{preview.studentCount}名・{preview.resultCount}件が出力対象であることを確認しました。
              </span>
            </label>
            <p className="muted bulk-export-note">
              生徒ごとのフォルダーに詳細結果PDFを格納し、UTF-8形式の一覧CSVを同梱します。
            </p>
          </div>
        ) : null}
        {exportStatus ? (
          <div className="bulk-export-progress">
            <StatusBadge status={exportStatus.state} />
            <div>
              <strong>
                {exportStatus.processedResultCount || 0} / {exportStatus.resultCount}件
              </strong>
              <progress
                max={10_000}
                value={
                  exportStatus.state === "verified"
                    ? 10_000
                    : exportStatus.progressBasisPoints || 0
                }
              />
            </div>
            {exportInProgress ? (
              <p aria-live="polite">画面を閉じてもサーバーで作成は続きます。</p>
            ) : null}
            {pollError ? (
              <InlineAlert
                tone="warning"
                action={
                  <Button variant="secondary" size="small" onClick={() => void refreshExportStatus()}>
                    状態を再確認
                  </Button>
                }
              >
                <p>{pollError.message}</p>
              </InlineAlert>
            ) : null}
            {exportStatus.state === "failed" || exportStatus.state === "superseded" ? (
              <InlineAlert
                tone="danger"
                title="一括出力を完了できませんでした"
                action={canBulkExport ? (
                  <Button
                    variant="secondary"
                    size="small"
                    onClick={() => {
                      const selector = exportStatus.normalizedSelector;
                      void beginExport(
                        selector ? modeForSelector(selector) : exportMode,
                        selector,
                      );
                    }}
                  >
                    同じ対象をもう一度準備
                  </Button>
                ) : undefined}
              >
                <p>{exportStatus.safeErrorDetail || "対象を再確認して、もう一度お試しください。"}</p>
              </InlineAlert>
            ) : null}
            {exportStatus.state === "verified" ? (
              <InlineAlert tone="success" title="ZIPを作成しました">
                <p>{exportStatus.studentCount}名・{exportStatus.resultCount}件を収録しています。</p>
              </InlineAlert>
            ) : null}
          </div>
        ) : null}
      </Modal>
    </div>
  );
}

function exportConfirmationText(mode: ExportMode, hasFilters: boolean) {
  if (mode === "rows") {
    return "チェックを付けた結果行だけを出力します。ほかのページの未選択結果は含みません。";
  }
  return hasFilters
    ? "表示中の1ページだけでなく、現在の絞り込みに一致する全ページの結果を出力します。"
    : "全期間・全生徒の確定済み結果が対象です。件数を必ず確認してください。";
}

function modeForSelector(selector: TranscriptSelector): ExportMode {
  return "filter" in selector ? "filtered" : "rows";
}

function toError(reason: unknown, fallback: string) {
  if (reason instanceof ApiError) {
    return new Error(reason.problem.errors?.[0]?.message || reason.message || fallback);
  }
  return reason instanceof Error ? reason : new Error(fallback);
}

function isDefinitiveCreateFailure(reason: unknown) {
  return (
    reason instanceof ApiError &&
    reason.status >= 400 &&
    reason.status < 500 &&
    ![408, 425, 429].includes(reason.status)
  );
}

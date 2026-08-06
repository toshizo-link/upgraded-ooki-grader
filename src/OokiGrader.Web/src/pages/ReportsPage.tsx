import { useState } from "react";
import { Link, useSearchParams } from "../router";
import { Icon } from "../components/Icon";
import {
  Badge,
  Card,
  EmptyState,
  ErrorState,
  PageHeader,
  SearchInput,
  Score,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { api, asPaged } from "../lib/api";
import {
  formatDate,
  formatPercentageBasisPoints,
  formatPoints,
} from "../lib/format";
import type { PagedResponse, SubmissionSummary } from "../types";

interface FinalizedSubmission extends SubmissionSummary {
  testTitle?: string;
  testDate?: string;
  percentageBasisPoints?: number;
  exportState?: string;
}

export function ReportsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [search, setSearch] = useState(searchParams.get("q") || "");
  const from = searchParams.get("from") || "";
  const to = searchParams.get("to") || "";
  const results = useApiQuery<PagedResponse<FinalizedSubmission>>(
    `reports:${searchParams.toString()}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/submissions",
          {
            state: "finalized",
            search: searchParams.get("q"),
            from: from || undefined,
            to: to || undefined,
            pageSize: 100,
          },
          signal,
        ),
      ),
  );

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(searchParams);
    if (value) next.set(key, value);
    else next.delete(key);
    setSearchParams(next, { replace: true });
  }

  return (
    <div className="page">
      <PageHeader
        eyebrow="結果・帳票"
        title="帳票"
        description="確定済みの結果を確認し、生徒ごとの日本語PDFを作成します。"
      />
      <Card>
        <div className="list-toolbar reports-toolbar">
          <SearchInput
            value={search}
            onChange={(value) => {
              setSearch(value);
              setParam("q", value);
            }}
            placeholder="生徒名・テスト名で検索"
            label="確定結果を検索"
          />
          <div className="date-filter-inline">
            <label>
              <span>開始日</span>
              <input
                type="date"
                value={from}
                max={to || undefined}
                onChange={(event) => setParam("from", event.target.value)}
              />
            </label>
            <span>〜</span>
            <label>
              <span>終了日</span>
              <input
                type="date"
                value={to}
                min={from || undefined}
                onChange={(event) => setParam("to", event.target.value)}
              />
            </label>
          </div>
          {results.data ? (
            <span className="result-count">
              {results.data.totalApproximate ?? results.data.items.length}件
            </span>
          ) : null}
        </div>
        {results.status === "loading" ? (
          <SkeletonRows rows={7} />
        ) : results.status === "error" ? (
          <ErrorState error={results.error} onRetry={results.reload} />
        ) : results.data?.items.length ? (
          <div className="table-scroll">
            <table className="reports-table">
              <thead>
                <tr>
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
                    <tr key={result.id}>
                      <td>{formatDate(result.testDate)}</td>
                      <td>
                        <strong>
                          {result.studentDisplayName || "未割り当て"}
                        </strong>
                        {result.studentNumber ? (
                          <small>{result.studentNumber}</small>
                        ) : null}
                      </td>
                      <td>{result.testTitle || result.fileName || "テスト"}</td>
                      <td>
                        <Score
                          compact
                          earned={formatPoints(result.totalEarnedPointsMilli)}
                          possible={formatPoints(
                            result.totalPossiblePointsMilli,
                          )}
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
              searchParams.toString()
                ? "条件に一致する確定結果はありません"
                : "確定済みの結果はまだありません"
            }
            description={
              searchParams.toString()
                ? "検索条件や期間を変更してください。"
                : "答案を確定すると、ここから結果PDFを作成できます。"
            }
          />
        )}
      </Card>
    </div>
  );
}

import type { ProgressPoint } from "../types";
import { formatDate, formatPercentageBasisPoints } from "../lib/format";

const width = 760;
const height = 280;
const padding = { top: 24, right: 24, bottom: 54, left: 54 };

export function ProgressChart({ series }: { series: ProgressPoint[] }) {
  if (series.length === 0) return null;
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const xFor = (index: number) =>
    series.length === 1
      ? padding.left + plotWidth / 2
      : padding.left + (index / (series.length - 1)) * plotWidth;
  const yFor = (basisPoints: number) =>
    padding.top + plotHeight - (basisPoints / 10_000) * plotHeight;
  const points = series
    .map((point, index) => `${xFor(index)},${yFor(point.percentageBasisPoints)}`)
    .join(" ");

  return (
    <div className="progress-chart">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-labelledby="progress-chart-title progress-chart-description"
      >
        <title id="progress-chart-title">得点率の推移</title>
        <desc id="progress-chart-description">
          {series.length === 1
            ? "期間内のテストは1件です。"
            : `${series.length}件のテストを日付順に結んだグラフです。`}
        </desc>
        {[0, 25, 50, 75, 100].map((tick) => {
          const y = yFor(tick * 100);
          return (
            <g key={tick}>
              <line
                className="progress-chart__grid"
                x1={padding.left}
                x2={width - padding.right}
                y1={y}
                y2={y}
              />
              <text
                className="progress-chart__axis"
                x={padding.left - 12}
                y={y + 4}
                textAnchor="end"
              >
                {tick}%
              </text>
            </g>
          );
        })}
        {series.length > 1 ? (
          <polyline className="progress-chart__line" points={points} />
        ) : null}
        {series.map((point, index) => {
          const x = xFor(index);
          const y = yFor(point.percentageBasisPoints);
          return (
            <a
              href={`/results/${encodeURIComponent(point.submissionId)}`}
              key={`${point.submissionId}-${point.resultRevision}`}
              aria-label={`${formatDate(point.testDate)} ${point.testTitle} ${formatPercentageBasisPoints(point.percentageBasisPoints)}`}
            >
              <circle className="progress-chart__halo" cx={x} cy={y} r="10" />
              <circle className="progress-chart__point" cx={x} cy={y} r="5" />
              {(index === 0 ||
                index === series.length - 1 ||
                series.length <= 4) && (
                <text
                  className="progress-chart__date"
                  x={x}
                  y={height - 20}
                  textAnchor="middle"
                >
                  {new Intl.DateTimeFormat("ja-JP", {
                    timeZone: "Asia/Tokyo",
                    month: "numeric",
                    day: "numeric",
                  }).format(new Date(`${point.testDate}T00:00:00+09:00`))}
                </text>
              )}
              <title>
                {point.testTitle}・
                {formatPercentageBasisPoints(point.percentageBasisPoints)}
              </title>
            </a>
          );
        })}
      </svg>
    </div>
  );
}

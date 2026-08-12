import { useEffect, useId, useRef, useState } from "react";
import { Button } from "./ui";

export interface ActiveFilter {
  key: string;
  label: string;
  value: string;
}

export interface SortFieldOption {
  value: string;
  label: string;
  defaultDirection?: "asc" | "desc";
}

export function ListSortControls({
  value,
  options,
  defaultValue,
  onChange,
}: {
  value: string;
  options: readonly SortFieldOption[];
  defaultValue: string;
  onChange: (value: string) => void;
}) {
  const descending = value.startsWith("-");
  const field = descending ? value.slice(1) : value;

  function commit(nextField: string, nextDirection: "asc" | "desc") {
    const signed = `${nextDirection === "desc" ? "-" : ""}${nextField}`;
    onChange(signed === defaultValue ? "" : signed);
  }

  return (
    <div className="list-sort-controls" role="group" aria-label="並び替え">
      <label>
        <span>並び順</span>
        <select
          aria-label="並び順"
          value={field}
          onChange={(event) => {
            const selected = options.find(
              (option) => option.value === event.target.value,
            );
            commit(
              event.target.value,
              selected?.defaultDirection || (descending ? "desc" : "asc"),
            );
          }}
        >
          {options.map((option) => (
            <option value={option.value} key={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>方向</span>
        <select
          aria-label="並び方向"
          value={descending ? "desc" : "asc"}
          onChange={(event) =>
            commit(field, event.target.value === "desc" ? "desc" : "asc")
          }
        >
          <option value="asc">昇順</option>
          <option value="desc">降順</option>
        </select>
      </label>
    </div>
  );
}

export function ActiveFilterSummary({
  filters,
  onClear,
}: {
  filters: readonly ActiveFilter[];
  onClear: () => void;
}) {
  return (
    <div className="active-filter-summary" aria-live="polite">
      <div>
        <strong>現在の絞り込み</strong>
        {filters.length ? (
          <ul aria-label="適用中の絞り込み">
            {filters.map((filter) => (
              <li key={filter.key}>
                <span>{filter.label}</span>
                {filter.value}
              </li>
            ))}
          </ul>
        ) : (
          <span className="muted">追加の絞り込みはありません</span>
        )}
      </div>
      {filters.length ? (
        <Button type="button" variant="quiet" size="small" onClick={onClear}>
          絞り込みをすべて解除
        </Button>
      ) : null}
    </div>
  );
}

export function ListPagination({
  page,
  pageSize,
  itemCount,
  totalApproximate,
  hasNext,
  nextBlockedReason,
  canGoPrevious,
  onNext,
  onPrevious,
  onPageSizeChange,
}: {
  page: number;
  pageSize: number;
  itemCount: number;
  totalApproximate?: number;
  hasNext: boolean;
  nextBlockedReason?: string;
  canGoPrevious: boolean;
  onNext: () => void;
  onPrevious: () => void;
  onPageSizeChange: (value: number) => void;
}) {
  if (!itemCount && page === 1) return null;
  const first = (page - 1) * pageSize + 1;
  const last = first + Math.max(itemCount - 1, 0);
  return (
    <nav className="list-pagination" aria-label="一覧のページ切り替え">
      <label>
        <span>1ページの件数</span>
        <select
          aria-label="1ページの件数"
          value={pageSize}
          onChange={(event) => onPageSizeChange(Number(event.target.value))}
        >
          {[25, 50, 100, 200].map((value) => (
            <option value={value} key={value}>
              {value}件
            </option>
          ))}
        </select>
      </label>
      <span className="list-pagination__position">
        {totalApproximate !== undefined
          ? `${first}〜${Math.min(last, totalApproximate)}件 / 約${totalApproximate}件`
          : `${first}〜${last}件`}
      </span>
      <div>
        <Button
          type="button"
          variant="secondary"
          size="small"
          disabled={!canGoPrevious}
          onClick={onPrevious}
        >
          前へ
        </Button>
        <span aria-current="page">{page}ページ</span>
        <Button
          type="button"
          variant="secondary"
          size="small"
          disabled={!hasNext}
          title={nextBlockedReason}
          onClick={onNext}
        >
          次へ
        </Button>
      </div>
      {nextBlockedReason ? (
        <span className="list-pagination__limit" role="status">
          {nextBlockedReason}
        </span>
      ) : null}
    </nav>
  );
}

export function FilterTextInput({
  label,
  value,
  onCommit,
  suggestions = [],
  placeholder,
}: {
  label: string;
  value: string;
  onCommit: (value: string) => void;
  suggestions?: readonly string[];
  placeholder?: string;
}) {
  const [draft, setDraft] = useState(value);
  const lastExternalValue = useRef(value);
  const onCommitRef = useRef(onCommit);
  onCommitRef.current = onCommit;
  const listId = useId();

  useEffect(() => {
    if (value === lastExternalValue.current) return;
    lastExternalValue.current = value;
    setDraft(value);
  }, [value]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      const normalized = draft.trim().replace(/\s+/gu, " ").slice(0, 100);
      if (normalized !== value) {
        lastExternalValue.current = normalized;
        onCommitRef.current(normalized);
      }
    }, 450);
    return () => window.clearTimeout(timer);
  }, [draft, value]);

  return (
    <label className="filter-field">
      <span>{label}</span>
      <input
        value={draft}
        list={suggestions.length ? listId : undefined}
        placeholder={placeholder || `${label}を入力`}
        onChange={(event) => setDraft(event.target.value)}
        onBlur={() => {
          const normalized = draft.trim().replace(/\s+/gu, " ").slice(0, 100);
          if (normalized !== value) onCommitRef.current(normalized);
        }}
        onKeyDown={(event) => {
          if (event.key === "Enter") event.currentTarget.blur();
          if (event.key === "Escape") {
            setDraft("");
            onCommitRef.current("");
          }
        }}
      />
      {suggestions.length ? (
        <datalist id={listId}>
          {suggestions.map((suggestion) => (
            <option value={suggestion} key={suggestion} />
          ))}
        </datalist>
      ) : null}
    </label>
  );
}

export function uniqueSuggestions(values: Array<string | null | undefined>) {
  return Array.from(new Set(values.filter((value): value is string => Boolean(value))))
    .sort((left, right) => left.localeCompare(right, "ja"))
    .slice(0, 200);
}

export function facetSuggestions(
  facets: Record<
    string,
    Array<string | { value: string; label?: string; count?: number }>
  > | null | undefined,
  key: string,
  fallback: Array<string | null | undefined> = [],
) {
  const authoritative = (facets?.[key] || []).map((entry) =>
    typeof entry === "string" ? entry : entry.value,
  );
  return uniqueSuggestions(authoritative.length ? authoritative : fallback);
}

export function facetOptions(
  facets: Record<
    string,
    Array<string | { value: string; label?: string; count?: number }>
  > | null | undefined,
  key: string,
  fallback: Array<{ value: string; label?: string }> = [],
) {
  const source = facets?.[key]?.length ? facets[key] : fallback;
  const deduplicated = new Map<
    string,
    { value: string; label: string; count?: number }
  >();
  source.forEach((entry) => {
    const option =
      typeof entry === "string"
        ? { value: entry, label: entry }
        : {
            value: entry.value,
            label: entry.label || entry.value,
            count: "count" in entry ? entry.count : undefined,
          };
    if (option.value && !deduplicated.has(option.value)) {
      deduplicated.set(option.value, option);
    }
  });
  return Array.from(deduplicated.values()).sort((left, right) =>
    left.label.localeCompare(right.label, "ja"),
  );
}

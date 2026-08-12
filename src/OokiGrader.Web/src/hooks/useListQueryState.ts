import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { useSearchParams } from "../router";

const PAGINATION_KEYS = ["cursor", "page", "trail"] as const;
const DEFAULT_PAGE_SIZES = [25, 50, 100, 200] as const;
const MAX_CURSOR_LENGTH = 8_192;
const MAX_CURSOR_TRAIL = 50;
const MAX_SERIALIZED_CURSOR_LENGTH = 32_768;

export interface ListQueryOptions {
  allowedSorts: readonly string[];
  defaultSort: string;
  enumParams?: Readonly<Record<string, readonly string[]>>;
  dateParams?: readonly string[];
  textParams?: readonly string[];
  auxiliaryIdParams?: readonly string[];
  defaultPageSize?: number;
  allowedPageSizes?: readonly number[];
  searchDebounceMs?: number;
}

interface ParamUpdateOptions {
  replace?: boolean;
  preservePage?: boolean;
}

export function normalizeListSearch(value: string, maxLength = 200) {
  return value.trim().replace(/\s+/gu, " ").slice(0, maxLength);
}

export function isIsoDate(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}$/u.test(value)) return false;
  const [year, month, day] = value.split("-").map(Number);
  const date = new Date(Date.UTC(year || 0, (month || 1) - 1, day || 1));
  return (
    date.getUTCFullYear() === year &&
    date.getUTCMonth() + 1 === month &&
    date.getUTCDate() === day
  );
}

export function listFilterFingerprint(params: URLSearchParams) {
  const normalized = new URLSearchParams(params);
  [...PAGINATION_KEYS, "pageSize", "bulkExport"].forEach((key) =>
    normalized.delete(key),
  );
  normalized.sort();
  return normalized.toString();
}

export function canAppendCursorTrail(
  trail: readonly string[],
  currentCursor: string | undefined,
  nextCursor: string | null | undefined,
) {
  if (!nextCursor || nextCursor.length > MAX_CURSOR_LENGTH) return false;
  const nextTrail = [...trail, currentCursor || ""];
  return (
    nextTrail.length <= MAX_CURSOR_TRAIL &&
    serializedCursorLength(nextTrail, nextCursor) <=
      MAX_SERIALIZED_CURSOR_LENGTH
  );
}

export function useListQueryState(options: ListQueryOptions) {
  const [searchParams, setSearchParams] = useSearchParams();
  const allowedPageSizes = options.allowedPageSizes || DEFAULT_PAGE_SIZES;
  const defaultPageSize = options.defaultPageSize || 50;
  const safeParams = useMemo(
    () => sanitizeParams(searchParams, options, allowedPageSizes),
    [allowedPageSizes, options, searchParams],
  );
  const safeSerialized = safeParams.toString();
  const originalSerialized = searchParams.toString();

  useEffect(() => {
    if (safeSerialized !== originalSerialized) {
      setSearchParams(safeParams, { replace: true });
    }
  }, [originalSerialized, safeParams, safeSerialized, setSearchParams]);

  const querySearch = safeParams.get("q") || "";
  const [search, setSearch] = useState(querySearch);
  const lastExternalSearch = useRef(querySearch);

  useEffect(() => {
    if (querySearch === lastExternalSearch.current) return;
    lastExternalSearch.current = querySearch;
    setSearch(querySearch);
  }, [querySearch]);

  const updateParam = useCallback(
    (key: string, value?: string | null, updateOptions: ParamUpdateOptions = {}) => {
      setSearchParams(
        (current) => {
          const next = new URLSearchParams(current);
          const normalized = value?.trim() || "";
          if (normalized) next.set(key, normalized);
          else next.delete(key);
          if (!updateOptions.preservePage) resetPagination(next);
          return next;
        },
        { replace: updateOptions.replace ?? true },
      );
    },
    [setSearchParams],
  );

  useEffect(() => {
    const timer = window.setTimeout(() => {
      const normalized = normalizeListSearch(search);
      if (normalized === querySearch) return;
      lastExternalSearch.current = normalized;
      updateParam("q", normalized);
    }, options.searchDebounceMs ?? 350);
    return () => window.clearTimeout(timer);
  }, [options.searchDebounceMs, querySearch, search, updateParam]);

  const clearFilters = useCallback(
    (keys: readonly string[], replacements: Readonly<Record<string, string>> = {}) => {
      setSearchParams(
        (current) => {
          const next = new URLSearchParams(current);
          keys.forEach((key) => next.delete(key));
          Object.entries(replacements).forEach(([key, value]) => {
            if (value) next.set(key, value);
          });
          resetPagination(next);
          return next;
        },
        { replace: true },
      );
      if (keys.includes("q") || replacements.q !== undefined) {
        const replacementSearch = replacements.q || "";
        lastExternalSearch.current = replacementSearch;
        setSearch(replacementSearch);
      }
    },
    [setSearchParams],
  );

  const page = Number(safeParams.get("page")) || 1;
  const pageSize = Number(safeParams.get("pageSize")) || defaultPageSize;
  const cursor = safeParams.get("cursor") || undefined;
  const trail = decodeTrail(safeParams.get("trail"));

  const canNavigateNext = useCallback(
    (nextCursor: string | null | undefined) =>
      canAppendCursorTrail(trail, cursor, nextCursor),
    [cursor, trail],
  );

  const nextPage = useCallback(
    (nextCursor: string | null | undefined) => {
      if (!canNavigateNext(nextCursor)) return;
      const safeNextCursor = nextCursor as string;
      setSearchParams((current) => {
        const next = new URLSearchParams(current);
        const currentPage = Number(next.get("page")) || 1;
        const currentTrail = decodeTrail(next.get("trail"));
        currentTrail.push(next.get("cursor") || "");
        next.set("cursor", safeNextCursor);
        next.set("page", String(currentPage + 1));
        next.set("trail", JSON.stringify(currentTrail));
        return next;
      });
    },
    [canNavigateNext, setSearchParams],
  );

  const previousPage = useCallback(() => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      const currentPage = Number(next.get("page")) || 1;
      const currentTrail = decodeTrail(next.get("trail"));
      if (currentPage <= 1 || currentTrail.length === 0) return next;
      const previousCursor = currentTrail.pop() || "";
      if (previousCursor) next.set("cursor", previousCursor);
      else next.delete("cursor");
      if (currentPage - 1 > 1) next.set("page", String(currentPage - 1));
      else next.delete("page");
      if (currentTrail.length) next.set("trail", JSON.stringify(currentTrail));
      else next.delete("trail");
      return next;
    });
  }, [setSearchParams]);

  const setPageSize = useCallback(
    (value: number) => {
      const selected = allowedPageSizes.includes(value)
        ? value
        : defaultPageSize;
      updateParam(
        "pageSize",
        selected === defaultPageSize ? undefined : String(selected),
      );
    },
    [allowedPageSizes, defaultPageSize, updateParam],
  );

  return {
    searchParams: safeParams,
    search,
    setSearch,
    updateParam,
    clearFilters,
    sort: safeParams.get("sort") || options.defaultSort,
    page,
    pageSize,
    cursor,
    canGoPrevious: page > 1 && trail.length > 0,
    canNavigateNext,
    nextPage,
    previousPage,
    setPageSize,
    filterFingerprint: listFilterFingerprint(safeParams),
  };
}

function sanitizeParams(
  input: URLSearchParams,
  options: ListQueryOptions,
  allowedPageSizes: readonly number[],
) {
  const next = new URLSearchParams(input);
  const allowedKeys = new Set([
    "q",
    "sort",
    "pageSize",
    ...PAGINATION_KEYS,
    ...Object.keys(options.enumParams || {}),
    ...(options.dateParams || []),
    ...(options.textParams || []),
    ...(options.auxiliaryIdParams || []),
  ]);
  Array.from(next.keys()).forEach((key) => {
    if (!allowedKeys.has(key)) next.delete(key);
  });
  const q = normalizeListSearch(next.get("q") || "");
  if (q) next.set("q", q);
  else next.delete("q");

  Object.entries(options.enumParams || {}).forEach(([key, allowed]) => {
    const value = next.get(key);
    if (value && !allowed.includes(value)) next.delete(key);
    else if (value) next.set(key, value);
  });
  (options.textParams || []).forEach((key) => {
    const value = normalizeListSearch(next.get(key) || "", 100);
    if (value) next.set(key, value);
    else next.delete(key);
  });
  (options.dateParams || []).forEach((key) => {
    const value = next.get(key);
    if (value && !isIsoDate(value)) next.delete(key);
    else if (value) next.set(key, value);
  });
  (options.auxiliaryIdParams || []).forEach((key) => {
    const value = next.get(key) || "";
    if (!/^[a-zA-Z0-9_-]{1,100}$/u.test(value)) next.delete(key);
    else next.set(key, value);
  });

  const from = next.get("from");
  const to = next.get("to");
  if (from && to && from > to) next.delete("to");

  const sort = next.get("sort");
  if (sort && !options.allowedSorts.includes(sort)) next.delete("sort");
  else if (sort) next.set("sort", sort);

  const pageSize = Number(next.get("pageSize"));
  if (next.has("pageSize") && !allowedPageSizes.includes(pageSize)) {
    next.delete("pageSize");
  } else if (next.has("pageSize")) {
    next.set("pageSize", String(pageSize));
  }

  const cursor = next.get("cursor");
  const page = Number(next.get("page"));
  const trail = decodeTrail(next.get("trail"));
  const hasValidPagination =
    Boolean(cursor) &&
    Boolean(Number.isInteger(page) && page > 1 && page <= MAX_CURSOR_TRAIL + 1) &&
    cursor!.length <= MAX_CURSOR_LENGTH &&
    serializedCursorLength(trail, cursor!) <= MAX_SERIALIZED_CURSOR_LENGTH &&
    trail.length === page - 1;
  if (!hasValidPagination) resetPagination(next);
  else {
    next.set("cursor", cursor!);
    next.set("page", String(page));
    next.set("trail", JSON.stringify(trail));
  }
  return next;
}

function decodeTrail(value: string | null) {
  if (!value) return [] as string[];
  try {
    const parsed = JSON.parse(value) as unknown;
    if (
      !Array.isArray(parsed) ||
      parsed.length > MAX_CURSOR_TRAIL ||
      encodeURIComponent(value).length > MAX_SERIALIZED_CURSOR_LENGTH ||
      !parsed.every(
        (item) => typeof item === "string" && item.length <= MAX_CURSOR_LENGTH,
      )
    ) {
      return [];
    }
    return parsed;
  } catch {
    return [];
  }
}

function serializedCursorLength(trail: readonly string[], cursor: string) {
  return (
    encodeURIComponent(JSON.stringify(trail)).length +
    encodeURIComponent(cursor).length
  );
}

function resetPagination(params: URLSearchParams) {
  PAGINATION_KEYS.forEach((key) => params.delete(key));
}

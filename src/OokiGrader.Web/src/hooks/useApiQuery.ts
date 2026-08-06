import { useCallback, useEffect, useRef, useState } from "react";

export type QueryStatus = "loading" | "success" | "empty" | "error";

export interface ApiQuery<T> {
  data: T | undefined;
  error: Error | undefined;
  status: QueryStatus;
  reload: () => void;
}

function isEmpty(value: unknown) {
  if (Array.isArray(value)) return value.length === 0;
  if (value && typeof value === "object" && "items" in value) {
    const items = (value as { items?: unknown[] }).items;
    return Array.isArray(items) && items.length === 0;
  }
  return false;
}

export function useApiQuery<T>(
  key: string,
  loader: (signal: AbortSignal) => Promise<T>,
  enabled = true,
): ApiQuery<T> {
  const loaderRef = useRef(loader);
  loaderRef.current = loader;
  const [refreshToken, setRefreshToken] = useState(0);
  const [data, setData] = useState<T>();
  const [error, setError] = useState<Error>();
  const [status, setStatus] = useState<QueryStatus>("loading");

  const reload = useCallback(() => {
    setRefreshToken((value) => value + 1);
  }, []);

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    setStatus("loading");
    setError(undefined);

    loaderRef
      .current(controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return;
        setData(value);
        setStatus(isEmpty(value) ? "empty" : "success");
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted) return;
        setError(
          reason instanceof Error
            ? reason
            : new Error("データを取得できませんでした。"),
        );
        setStatus("error");
      });

    return () => controller.abort();
  }, [enabled, key, refreshToken]);

  return { data, error, status, reload };
}

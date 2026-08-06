import type {
  PagedResponse,
  ProblemDetails,
  UploadCreateResponse,
  UploadFinalizeResponse,
} from "../types";

const API_BASE = "/api/v1";
let csrfToken: string | null = null;

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;
  readonly correlationId?: string;

  constructor(status: number, problem: ProblemDetails, correlationId?: string) {
    super(problem.detail || problem.title || "API request failed");
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
    this.correlationId = problem.correlationId || correlationId;
  }
}

type QueryValue = string | number | boolean | null | undefined;

export function apiPath(
  path: string,
  query?: Record<string, QueryValue>,
): string {
  const normalized = path.startsWith("/") ? path : `/${path}`;
  const url = new URL(`${API_BASE}${normalized}`, window.location.origin);
  if (query) {
    Object.entries(query).forEach(([key, value]) => {
      if (value !== null && value !== undefined && value !== "") {
        url.searchParams.set(key, String(value));
      }
    });
  }
  return `${url.pathname}${url.search}`;
}

async function parseProblem(response: Response): Promise<ProblemDetails> {
  const contentType = response.headers.get("content-type") || "";
  if (contentType.includes("json")) {
    try {
      return (await response.json()) as ProblemDetails;
    } catch {
      // Fall through to the status-only problem below.
    }
  }
  return {
    status: response.status,
    title: "通信に失敗しました",
    detail: response.statusText || "サーバーから応答を取得できませんでした。",
  };
}

async function readCsrfToken(signal?: AbortSignal) {
  if (csrfToken) return csrfToken;
  const response = await fetch(apiPath("/auth/csrf"), {
    method: "GET",
    credentials: "include",
    headers: { Accept: "application/json" },
    signal,
  });
  if (!response.ok) {
    throw new ApiError(
      response.status,
      await parseProblem(response),
      response.headers.get("X-Correlation-Id") || undefined,
    );
  }
  const value = (await response.json()) as {
    csrfToken?: string;
    token?: string;
  };
  csrfToken = value.csrfToken || value.token || null;
  if (!csrfToken) {
    throw new Error("CSRFトークンの応答形式が正しくありません。");
  }
  return csrfToken;
}

export interface RequestOptions extends Omit<RequestInit, "body"> {
  body?: unknown;
  query?: Record<string, QueryValue>;
  etag?: string;
  idempotencyKey?: string;
  csrf?: boolean;
}

export interface ApiResponse<T> {
  data: T;
  etag?: string;
  correlationId?: string;
}

export async function requestWithMeta<T>(
  path: string,
  options: RequestOptions = {},
): Promise<ApiResponse<T>> {
  const method = (options.method || "GET").toUpperCase();
  const isMutation = !["GET", "HEAD", "OPTIONS"].includes(method);
  const headers = new Headers(options.headers);
  headers.set("Accept", "application/json");

  let body: BodyInit | undefined;
  if (options.body instanceof FormData || options.body instanceof Blob) {
    body = options.body;
  } else if (options.body !== undefined) {
    headers.set("Content-Type", "application/json; charset=utf-8");
    body = JSON.stringify(options.body);
  }

  if (options.etag) headers.set("If-Match", options.etag);
  if (isMutation && !path.startsWith("/auth/")) {
    headers.set(
      "Idempotency-Key",
      options.idempotencyKey || newIdempotencyKey(),
    );
  }
  if (isMutation && options.csrf !== false) {
    headers.set(
      "X-CSRF-Token",
      await readCsrfToken(options.signal ?? undefined),
    );
  }

  const response = await fetch(apiPath(path, options.query), {
    ...options,
    method,
    headers,
    body,
    credentials: "include",
  });

  const sessionExpiresAt = response.headers.get("X-Session-Expires-At");
  if (sessionExpiresAt) {
    window.dispatchEvent(
      new CustomEvent("ooki:session-expiry", {
        detail: { sessionExpiresAt },
      }),
    );
  }
  const correlationId = response.headers.get("X-Correlation-Id") || undefined;
  if (!response.ok) {
    if (response.status === 401) csrfToken = null;
    throw new ApiError(
      response.status,
      await parseProblem(response),
      correlationId,
    );
  }

  const data =
    response.status === 204
      ? (undefined as T)
      : ((await response.json()) as T);

  return {
    data,
    etag: response.headers.get("ETag") || undefined,
    correlationId,
  };
}

export async function request<T>(
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  return (await requestWithMeta<T>(path, options)).data;
}

export const api = {
  get<T>(path: string, query?: Record<string, QueryValue>, signal?: AbortSignal) {
    return request<T>(path, { query, signal });
  },
  post<T>(path: string, body?: unknown, options: RequestOptions = {}) {
    return request<T>(path, {
      ...options,
      method: "POST",
      body,
    });
  },
  put<T>(path: string, body?: unknown, options: RequestOptions = {}) {
    return request<T>(path, {
      ...options,
      method: "PUT",
      body,
    });
  },
  patch<T>(path: string, body?: unknown, options: RequestOptions = {}) {
    return request<T>(path, {
      ...options,
      method: "PATCH",
      body,
    });
  },
  delete<T>(path: string, options: RequestOptions = {}) {
    return request<T>(path, { ...options, method: "DELETE" });
  },
};

export function newIdempotencyKey() {
  return crypto.randomUUID();
}

export function asPaged<T>(value: PagedResponse<T> | T[]): PagedResponse<T> {
  return Array.isArray(value)
    ? { items: value, nextCursor: null, totalApproximate: value.length }
    : value;
}

export interface UploadFileOptions {
  purpose: "completedTest" | "templateSource";
  testSessionId?: string;
  onProgress?: (uploadedBytes: number, totalBytes: number) => void;
  signal?: AbortSignal;
}

export async function uploadFile(
  file: File,
  options: UploadFileOptions,
): Promise<UploadFinalizeResponse> {
  const created = await api.post<UploadCreateResponse>(
    "/uploads",
    {
      purpose: options.purpose,
      testSessionId: options.testSessionId,
      fileName: file.name,
      declaredMimeType: file.type || "application/octet-stream",
      length: file.size,
    },
    {
      idempotencyKey: newIdempotencyKey(),
      signal: options.signal,
    },
  );

  const token = await readCsrfToken(options.signal);
  let offset = created.offset;
  const chunkSize = Math.min(created.maxChunkBytes || 8_388_608, 8_388_608);
  while (offset < file.size) {
    const end = Math.min(offset + chunkSize, file.size);
    const response = await fetch(created.chunkUrl, {
      method: "PATCH",
      credentials: "include",
      headers: {
        "Content-Type": "application/offset+octet-stream",
        "Upload-Offset": String(offset),
        "X-CSRF-Token": token,
      },
      body: file.slice(offset, end),
      signal: options.signal,
    });

    if (!response.ok) {
      throw new ApiError(
        response.status,
        await parseProblem(response),
        response.headers.get("X-Correlation-Id") || undefined,
      );
    }

    const nextOffset = Number(response.headers.get("Upload-Offset"));
    offset = Number.isFinite(nextOffset) && nextOffset > offset ? nextOffset : end;
    options.onProgress?.(offset, file.size);
  }

  return api.post<UploadFinalizeResponse>(
    `/uploads/${encodeURIComponent(created.uploadId)}:finalize`,
    undefined,
    { idempotencyKey: newIdempotencyKey(), signal: options.signal },
  );
}

export function resetCsrfTokenForTests() {
  csrfToken = null;
}

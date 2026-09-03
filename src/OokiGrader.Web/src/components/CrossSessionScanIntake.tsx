import {
  useRef,
  useState,
  type ChangeEvent,
  type DragEvent,
} from "react";
import type { OrderedScanBatchDetail, TestSessionSummary } from "../types";
import {
  ApiError,
  api,
  newIdempotencyKey,
  uploadFile,
} from "../lib/api";
import {
  naturalSortScanItems,
  orderedScanApi,
} from "../lib/orderedScans";
import { classNames } from "../lib/format";
import { Icon } from "./Icon";
import { Badge, Button, Card, InlineAlert } from "./ui";

type IntakeState =
  | "queued"
  | "classifying"
  | "ready"
  | "needsReview"
  | "uploading"
  | "processing"
  | "completed"
  | "failed";

interface IntakeItem {
  id: string;
  file: File;
  inputOrdinal: number;
  state: IntakeState;
  destinationSessionId?: string;
  expectedPageCount?: number;
  detectedTemplatePageNumber?: number;
  message?: string;
  progress: number;
  createIdempotencyKey: string;
  finalizeIdempotencyKey: string;
}

interface RoutingResponse {
  classificationMode: string;
  openSessions: Array<{
    id: string;
    title: string;
    testDate: string;
    classLabel?: string | null;
    gradeLabel?: string | null;
    subject?: string | null;
    course?: string | null;
    expectedPageCount?: number | null;
  }>;
  items: Array<{
    clientItemId: string;
    inputOrdinal: number;
    state: "routed" | "needsReview" | string;
    destinationSessionId?: string | null;
    expectedPageCount?: number | null;
    detectedTemplatePageNumber?: number | null;
    issueCode?: string | null;
  }>;
}

interface RoutingRun {
  sessionId: string;
  expectedPageCount: number;
  items: IntakeItem[];
}

const maximumUploadBytes = 250_000_000;

export function CrossSessionScanIntake({
  openSessions,
  onChanged,
}: {
  openSessions: TestSessionSummary[];
  onChanged: () => void;
}) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [items, setItems] = useState<IntakeItem[]>([]);
  const [routingSessions, setRoutingSessions] = useState<
    RoutingResponse["openSessions"]
  >([]);
  const [working, setWorking] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [error, setError] = useState<string>();

  const availableSessions = routingSessions.length
    ? routingSessions
    : openSessions.map((session) => ({
        id: session.id,
        title:
          session.templateTitle ||
          session.title ||
          session.name ||
          session.sessionName ||
          "名称未設定",
        testDate: session.testDate,
        classLabel: session.classLabel,
        gradeLabel: session.gradeLabel,
        subject: session.subject,
        course: session.course || session.templateCourse,
        expectedPageCount: session.expectedSubmissionPageCount,
      }));
  const routingRuns = buildRoutingRuns(items);
  const planError = validateRoutingRuns(items, routingRuns);
  const canProcess =
    items.length > 0 &&
    !working &&
    !planError &&
    items.every(
      (item) =>
        item.state === "ready" &&
        item.destinationSessionId &&
        item.expectedPageCount,
    );

  function updateItem(id: string, changes: Partial<IntakeItem>) {
    setItems((current) =>
      current.map((item) => (item.id === id ? { ...item, ...changes } : item)),
    );
  }

  function addFiles(files: File[]) {
    if (working) return;
    const accepted = files.filter(canUseFile);
    const rejected = files.filter((file) => !canUseFile(file));
    const next = naturalSortScanItems([
      ...items,
      ...accepted.map((file) => ({
        id: crypto.randomUUID(),
        file,
        inputOrdinal: 0,
        state: "queued" as const,
        progress: 0,
        createIdempotencyKey: newIdempotencyKey(),
        finalizeIdempotencyKey: newIdempotencyKey(),
      })),
    ]).map((item, index) => ({
      ...item,
      inputOrdinal: index + 1,
      state: "queued" as const,
      destinationSessionId: undefined,
      expectedPageCount: undefined,
      detectedTemplatePageNumber: undefined,
      message: undefined,
      progress: 0,
    }));
    setItems(next);
    setError(
      rejected.length
        ? `${rejected.map((file) => file.name).join("、")} は追加できません。1ページPDF（250 MB以下）を選択してください。`
        : undefined,
    );
    if (next.length) void classify(next);
  }

  function handleInput(event: ChangeEvent<HTMLInputElement>) {
    addFiles(Array.from(event.target.files || []));
    event.target.value = "";
  }

  function handleDrop(event: DragEvent) {
    event.preventDefault();
    setDragging(false);
    addFiles(Array.from(event.dataTransfer.files));
  }

  async function classify(planned: IntakeItem[]) {
    setWorking(true);
    setError(undefined);
    setItems(planned.map((item) => ({ ...item, state: "classifying" })));
    const decisions = new Map<string, RoutingResponse["items"][number]>();
    const failures = new Map<string, string>();
    let latestSessions: RoutingResponse["openSessions"] = [];
    try {
      for (const item of planned) {
        try {
          const pdfBody = item.file.type === "application/pdf"
            ? item.file
            : item.file.slice(0, item.file.size, "application/pdf");
          const response = await api.post<RoutingResponse>(
            "/ordered-scan-routing:classify-page",
            pdfBody,
            {
              query: {
                clientItemId: item.id,
                fileName: item.file.name,
                sizeBytes: item.file.size,
                inputOrdinal: item.inputOrdinal,
              },
              idempotency: false,
            },
          );
          latestSessions = response.openSessions;
          const decision = response.items.find(
            (candidate) => candidate.clientItemId === item.id,
          );
          if (decision) {
            decisions.set(item.id, decision);
          } else {
            failures.set(item.id, "サーバーから仕分け結果を取得できませんでした。");
          }
        } catch (reason) {
          failures.set(
            item.id,
            errorMessage(reason, "このPDFを仕分けできませんでした。"),
          );
        }
      }
      setRoutingSessions(latestSessions);
      setItems(
        planned.map((item) => {
          const decision = decisions.get(item.id);
          const destination = decision?.destinationSessionId || undefined;
          const failure = failures.get(item.id);
          return {
            ...item,
            destinationSessionId: destination,
            expectedPageCount: decision?.expectedPageCount || undefined,
            detectedTemplatePageNumber:
              decision?.detectedTemplatePageNumber || undefined,
            state: failure
              ? "failed"
              : destination
                ? "ready"
                : "needsReview",
            message: failure || (destination
              ? undefined
              : routingMessage(decision?.issueCode)),
          };
        }),
      );
      if (failures.size) {
        setError(
          `${failures.size}件のPDFを判定できませんでした。各ファイルの内容を確認してください。`,
        );
      }
    } finally {
      setWorking(false);
    }
  }

  function changeDestination(itemId: string, sessionId: string) {
    const session = availableSessions.find((candidate) => candidate.id === sessionId);
    setItems((current) =>
      current.map((item) =>
        item.id === itemId
          ? {
              ...item,
              destinationSessionId: sessionId || undefined,
              expectedPageCount: session?.expectedPageCount || undefined,
              detectedTemplatePageNumber: undefined,
              state: sessionId ? "ready" : "needsReview",
              message:
                sessionId && !session?.expectedPageCount
                  ? "答案ページ数を確認できないため、この受付先は使用できません。"
                  : undefined,
            }
          : item,
      ),
    );
  }

  async function processAll() {
    if (!canProcess) return;
    setWorking(true);
    setError(undefined);
    try {
      for (const run of routingRuns) {
        const created = await orderedScanApi.create(
          run.sessionId,
          {
            items: run.items.map((item, index) => ({
              clientItemId: item.id,
              fileName: item.file.name,
              inputOrdinal: index + 1,
            })),
          },
          newIdempotencyKey(),
        );
        for (let index = 0; index < run.items.length; index++) {
          const item = run.items[index]!;
          updateItem(item.id, { state: "uploading", progress: 0, message: undefined });
          await uploadFile(item.file, {
            purpose: "completedTestPage",
            testSessionId: run.sessionId,
            orderedScanBatchId: created.id,
            inputOrdinal: index + 1,
            clientItemId: item.id,
            createIdempotencyKey: item.createIdempotencyKey,
            finalizeIdempotencyKey: item.finalizeIdempotencyKey,
            onProgress: (uploaded, total) =>
              updateItem(item.id, {
                progress: total ? Math.round((uploaded / total) * 100) : 0,
              }),
          });
          updateItem(item.id, { state: "processing", progress: 100 });
        }
        const staged = await orderedScanApi.get(created.id);
        const queued = await orderedScanApi.finalize(staged.id, staged.rowVersion);
        const completed = await waitForTerminalBatch(queued);
        if (completed.status !== "completed") {
          const detail = completed.issues[0]?.message || "仕分け後の答案を組み立てられませんでした。";
          run.items.forEach((item) =>
            updateItem(item.id, { state: "failed", message: detail }),
          );
          throw new Error(detail);
        }
        run.items.forEach((item) =>
          updateItem(item.id, { state: "completed", progress: 100 }),
        );
        onChanged();
      }
    } catch (reason) {
      setError(errorMessage(reason, "答案の自動仕分けを完了できませんでした。"));
    } finally {
      setWorking(false);
    }
  }

  return (
    <Card className="cross-session-intake">
      <div className="cross-session-intake__header">
        <div>
          <h2>受付中テストへ一括仕分け</h2>
          <p>
            すべての1ページPDFを一か所に入れると、答案の内容を受付中テストの原稿と照合し、1ファイルずつ送信します。
          </p>
        </div>
        <Badge tone="info">受付中のみ</Badge>
      </div>
      <InlineAlert tone="info" title="原稿の並び順を保ちます">
        <p>
          スキャナーのファイル名を保持して自然順で処理します。原稿との照合が弱い・曖昧なファイルだけ受付先を選んでください。
        </p>
      </InlineAlert>
      {error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
      <div
        className={classNames(
          "cross-session-drop-zone",
          dragging && "is-dragging",
        )}
        onDragOver={(event) => {
          event.preventDefault();
          if (!working) setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={handleDrop}
      >
        <input
          ref={inputRef}
          type="file"
          accept=".pdf,application/pdf"
          multiple
          disabled={working}
          onChange={handleInput}
        />
        <Icon name="upload" size={28} />
        <strong>全テストのスキャンPDFをここにドロップ</strong>
        <span>PDF / 各ファイル1ページ / 250 MB以下</span>
        <Button
          type="button"
          variant="secondary"
          disabled={working}
          onClick={() => inputRef.current?.click()}
        >
          ファイルを選択
        </Button>
      </div>

      {items.length ? (
        <div className="cross-session-intake__workspace">
          <div className="cross-session-intake__summary">
            <strong>{items.length}ファイル</strong>
            <span>ファイル名順・上から1件ずつ処理</span>
            <Button
              type="button"
              size="small"
              variant="quiet"
              disabled={working}
              onClick={() => void classify(items)}
            >
              仕分けをやり直す
            </Button>
          </div>
          <ol className="cross-session-routing-list">
            {items.map((item) => {
              const destination = availableSessions.find(
                (session) => session.id === item.destinationSessionId,
              );
              return (
                <li key={item.id} className={`is-${item.state}`}>
                  <span className="cross-session-routing-list__ordinal">
                    {item.inputOrdinal}
                  </span>
                  <div className="cross-session-routing-list__file">
                    <strong title={item.file.name}>{item.file.name}</strong>
                    <small>{formatBytes(item.file.size)}</small>
                  </div>
                  <label>
                    <span className="sr-only">{item.file.name}の受付先</span>
                    <select
                      aria-label={`${item.file.name}の受付先`}
                      value={item.destinationSessionId || ""}
                      disabled={
                        working
                        || item.state === "completed"
                        || item.state === "failed"
                      }
                      onChange={(event) =>
                        changeDestination(item.id, event.target.value)
                      }
                    >
                      <option value="">受付先を選択</option>
                      {availableSessions.map((session) => (
                        <option
                          value={session.id}
                          disabled={!session.expectedPageCount}
                          key={session.id}
                        >
                          {session.title}
                          {session.classLabel ? `・${session.classLabel}` : ""}
                          {session.expectedPageCount
                            ? `（${session.expectedPageCount}ページ）`
                            : "（ページ数未設定）"}
                        </option>
                      ))}
                    </select>
                    {destination ? (
                      <small>
                        {destination.testDate}・1答案 {item.expectedPageCount}ページ
                      </small>
                    ) : null}
                  </label>
                  <IntakeStatus item={item} />
                </li>
              );
            })}
          </ol>
          {planError ? (
            <InlineAlert tone="warning" title="ページのまとまりを確認してください">
              <p>{planError}</p>
            </InlineAlert>
          ) : null}
          <div className="cross-session-intake__actions">
            <Button
              type="button"
              variant="secondary"
              disabled={working}
              onClick={() => {
                setItems([]);
                setError(undefined);
              }}
            >
              クリア
            </Button>
            <Button
              type="button"
              disabled={!canProcess}
              onClick={() => void processAll()}
            >
              {working ? "1件ずつ処理しています…" : "仕分け結果で受付を開始"}
            </Button>
          </div>
        </div>
      ) : null}
    </Card>
  );
}

function IntakeStatus({ item }: { item: IntakeItem }) {
  const labels: Record<IntakeState, string> = {
    queued: "判定待ち",
    classifying: "仕分け中",
    ready: "受付先決定",
    needsReview: "受付先を確認",
    uploading: `送信中 ${item.progress}%`,
    processing: "答案を組み立て中",
    completed: "受付完了",
    failed: "要確認",
  };
  const tone = item.state === "completed"
    ? "success"
    : item.state === "failed" || item.state === "needsReview"
      ? "warning"
      : "neutral";
  return (
    <div className="cross-session-routing-list__status">
      <Badge tone={tone}>{labels[item.state]}</Badge>
      {item.message ? <small>{item.message}</small> : null}
    </div>
  );
}

function canUseFile(file: File) {
  const extension = file.name.slice(file.name.lastIndexOf(".")).toLowerCase();
  return (
    file.size > 0 &&
    file.size <= maximumUploadBytes &&
    (file.type === "application/pdf" || extension === ".pdf")
  );
}

function buildRoutingRuns(items: IntakeItem[]): RoutingRun[] {
  const runs: RoutingRun[] = [];
  for (const item of items) {
    if (!item.destinationSessionId || !item.expectedPageCount) continue;
    const previous = runs[runs.length - 1];
    if (previous?.sessionId === item.destinationSessionId) {
      previous.items.push(item);
    } else {
      runs.push({
        sessionId: item.destinationSessionId,
        expectedPageCount: item.expectedPageCount,
        items: [item],
      });
    }
  }
  return runs;
}

function validateRoutingRuns(items: IntakeItem[], runs: RoutingRun[]) {
  if (items.some((item) => !item.destinationSessionId)) {
    return "受付先が未確定のファイルがあります。";
  }
  if (items.some((item) => !item.expectedPageCount)) {
    return "答案ページ数を確認できない受付先があります。";
  }
  const incomplete = runs.find(
    (run) => run.items.length % run.expectedPageCount !== 0,
  );
  if (incomplete) {
    const remaining =
      incomplete.expectedPageCount -
      (incomplete.items.length % incomplete.expectedPageCount);
    return `同じ受付先の連続したまとまりに、あと${remaining}ページ必要です。ファイル順または受付先を確認してください。`;
  }
  const outOfOrder = runs.find((run) => {
    if (run.items.some((item) => !item.detectedTemplatePageNumber)) {
      return false;
    }
    return run.items.some(
      (item, index) =>
        item.detectedTemplatePageNumber !==
        ((index % run.expectedPageCount) + 1),
    );
  });
  return outOfOrder
    ? "原稿ページの順番と一致しないPDFがあります。ファイル名順を確認してください。"
    : undefined;
}

async function waitForTerminalBatch(initial: OrderedScanBatchDetail) {
  let current = initial;
  for (let attempt = 0; attempt < 240; attempt++) {
    if (["completed", "needsReview", "failed", "expired", "cancelled"].includes(current.status)) {
      return current;
    }
    await new Promise<void>((resolve) => window.setTimeout(resolve, 1_000));
    current = await orderedScanApi.get(current.id);
  }
  throw new Error("答案の組み立てが続いています。受付中テストの箱で進行状況を確認してください。");
}

function routingMessage(issueCode?: string | null) {
  if (issueCode === "NO_ROUTABLE_OPEN_SESSION") {
    return "ページ数が設定された受付中テストがありません。";
  }
  if (issueCode === "ROUTING_TEMPLATE_REFERENCE_UNAVAILABLE") {
    return "照合できるテスト原稿がありません。先生が受付先を選択してください。";
  }
  if (issueCode === "ROUTING_VISUAL_WEAK") {
    return "原稿との一致が弱いため、先生が受付先を確認してください。";
  }
  return "複数の原稿に一致するため、先生が受付先を選択してください。";
}

function errorMessage(reason: unknown, fallback: string) {
  if (reason instanceof ApiError) return reason.message;
  return reason instanceof Error ? reason.message : fallback;
}

function formatBytes(bytes: number) {
  if (bytes < 1_000_000) return `${Math.max(1, Math.round(bytes / 1_000))} KB`;
  return `${(bytes / 1_000_000).toFixed(1)} MB`;
}

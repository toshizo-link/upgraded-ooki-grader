import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type DragEvent,
} from "react";
import type { OrderedScanBatchDetail } from "../types";
import { ApiError, newIdempotencyKey, uploadFile } from "../lib/api";
import {
  groupOrderedScans,
  moveScanItem,
  naturalSortScanItems,
  orderedScanApi,
  orderedScanBatchStorageKey,
  type OrderedScanGroup,
} from "../lib/orderedScans";
import { classNames } from "../lib/format";
import { Icon } from "./Icon";
import { Badge, Button, Card, InlineAlert, StatusBadge } from "./ui";

type LocalPageState = "ready" | "uploading" | "staged" | "failed";

interface LocalPage {
  id: string;
  file: File;
  progress: number;
  state: LocalPageState;
  message?: string;
  orderedScanItemId?: string;
  createIdempotencyKey: string;
  finalizeIdempotencyKey: string;
}

const maximumUploadBytes = 250_000_000;
function canUploadAsPage(file: File) {
  const dot = file.name.lastIndexOf(".");
  const extension = dot >= 0
    ? file.name.slice(dot).toLocaleLowerCase("en-US")
    : "";
  return (
    file.size <= maximumUploadBytes &&
    (file.type === "application/pdf" || extension === ".pdf")
  );
}

export function OrderedScanUploadBoard({
  sessionId,
  expectedPageCount,
  isOpen,
  onBatchChanged,
}: {
  sessionId: string;
  expectedPageCount?: number;
  isOpen: boolean;
  onBatchChanged: () => void;
}) {
  const [pages, setPages] = useState<LocalPage[]>([]);
  const [batch, setBatch] = useState<OrderedScanBatchDetail>();
  const [dragging, setDragging] = useState(false);
  const [working, setWorking] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const inputRef = useRef<HTMLInputElement>(null);
  const notifiedBatchId = useRef<string | undefined>(undefined);
  const createAttemptRef = useRef<{
    manifest: string;
    idempotencyKey: string;
  } | undefined>(undefined);
  const validExpectedPageCount =
    Number.isInteger(expectedPageCount) && (expectedPageCount || 0) > 0
      ? expectedPageCount
      : undefined;

  const updatePage = useCallback(
    (id: string, changes: Partial<LocalPage>) => {
      setPages((current) =>
        current.map((page) =>
          page.id === id ? { ...page, ...changes } : page,
        ),
      );
    },
    [],
  );

  useEffect(() => {
    const storedBatchId = window.sessionStorage.getItem(
      orderedScanBatchStorageKey(sessionId),
    );
    if (!storedBatchId) return;

    const controller = new AbortController();
    void orderedScanApi
      .get(storedBatchId, controller.signal)
      .then((value) => {
        if (value.testSessionId === sessionId) setBatch(value);
      })
      .catch(() => {
        if (!controller.signal.aborted) {
          window.sessionStorage.removeItem(
            orderedScanBatchStorageKey(sessionId),
          );
        }
      });
    return () => controller.abort();
  }, [sessionId]);

  useEffect(() => {
    if (!batch || batch.status !== "processing") return;
    const timer = window.setInterval(() => {
      void orderedScanApi
        .get(batch.id)
        .then(setBatch)
        .catch(() => undefined);
    }, 2_000);
    return () => window.clearInterval(timer);
  }, [batch]);

  useEffect(() => {
    if (
      batch?.status === "completed" &&
      notifiedBatchId.current !== batch.id
    ) {
      notifiedBatchId.current = batch.id;
      onBatchChanged();
    }
  }, [batch, onBatchChanged]);

  const localGroups = useMemo(
    () =>
      validExpectedPageCount
        ? groupOrderedScans(pages, validExpectedPageCount)
        : [],
    [pages, validExpectedPageCount],
  );
  const serverGroups = useMemo(() => {
    if (!batch) return [];
    const sortedItems = [...batch.items].sort(
      (left, right) => left.inputOrdinal - right.inputOrdinal,
    );
    if (!batch.groups.length) {
      return groupOrderedScans(sortedItems, batch.expectedPageCount);
    }

    const byId = new Map(sortedItems.map((item) => [item.id, item]));
    return [...batch.groups]
      .sort((left, right) => left.groupOrdinal - right.groupOrdinal)
      .map<OrderedScanGroup<OrderedScanBatchDetail["items"][number]>>(
        (group) => {
          const items = group.itemIds
            .map((id) => byId.get(id))
            .filter((item): item is OrderedScanBatchDetail["items"][number] =>
              Boolean(item),
            )
            .map((item, index) => ({
              item,
              pageNumber: item.submissionPageNumber || index + 1,
              inputOrdinal: item.inputOrdinal,
            }));
          return {
            groupNumber: group.groupOrdinal,
            items,
            complete: group.status === "complete",
          };
        },
      );
  }, [batch]);
  const stagedCount = pages.filter((page) => page.state === "staged").length;
  const failedCount = pages.filter((page) => page.state === "failed").length;
  const serverUploadedCount =
    batch?.items.filter((item) => item.status === "uploaded").length || 0;
  const serverReadyToFinalize = Boolean(
    batch &&
      batch.status === "draft" &&
      batch.itemCount > 0 &&
      batch.items.length === batch.itemCount &&
      serverUploadedCount === batch.itemCount &&
      batch.itemCount % batch.expectedPageCount === 0,
  );
  const localReadyToFinalize = Boolean(
    batch &&
      batch.status === "draft" &&
      pages.length > 0 &&
      stagedCount === pages.length,
  );
  const canFinalize = serverReadyToFinalize || localReadyToFinalize;
  const hasIncompleteGroup = localGroups.some((group) => !group.complete);
  const frozen = Boolean(batch);
  const resetWithoutCleanup =
    batch?.status === "completed" ||
    batch?.status === "cancelled";
  const cleanupBeforeReset =
    batch?.status === "needsReview" ||
    batch?.status === "failed" ||
    batch?.status === "expired";

  function addFiles(files: File[]) {
    if (frozen) return;
    const accepted = files.filter(canUploadAsPage);
    const rejected = files.filter((file) => !canUploadAsPage(file));
    setActionError(
      rejected.length
        ? `${rejected.map((file) => file.name).join("、")} は追加できません。1ページのPDF（250 MB以下）を選択してください。`
        : undefined,
    );
    setPages((current) =>
      naturalSortScanItems([
        ...current,
        ...accepted.map((file) => ({
          id: crypto.randomUUID(),
          file,
          progress: 0,
          state: "ready" as const,
          createIdempotencyKey: newIdempotencyKey(),
          finalizeIdempotencyKey: newIdempotencyKey(),
        })),
      ]),
    );
  }

  function handleFileInput(event: ChangeEvent<HTMLInputElement>) {
    addFiles(Array.from(event.target.files || []));
    event.target.value = "";
  }

  function handleDrop(event: DragEvent) {
    event.preventDefault();
    setDragging(false);
    addFiles(Array.from(event.dataTransfer.files));
  }

  async function uploadLocalPages(
    batchId: string,
    pending: Array<{
      id: string;
      file: File;
      inputOrdinal: number;
      createIdempotencyKey: string;
      finalizeIdempotencyKey: string;
    }>,
  ) {
    let cursor = 0;
    let failed = false;
    async function worker() {
      while (cursor < pending.length) {
        const page = pending[cursor++];
        if (!page) return;
        updatePage(page.id, {
          state: "uploading",
          progress: 0,
          message: undefined,
        });
        try {
          const result = await uploadFile(page.file, {
            purpose: "completedTestPage",
            testSessionId: sessionId,
            orderedScanBatchId: batchId,
            inputOrdinal: page.inputOrdinal,
            clientItemId: page.id,
            createIdempotencyKey: page.createIdempotencyKey,
            finalizeIdempotencyKey: page.finalizeIdempotencyKey,
            onProgress: (uploaded, total) =>
              updatePage(page.id, {
                progress: total
                  ? Math.round((uploaded / total) * 100)
                  : 0,
              }),
          });
          updatePage(page.id, {
            state: "staged",
            progress: 100,
            orderedScanItemId: result.orderedScanItemId,
          });
        } catch (reason) {
          failed = true;
          updatePage(page.id, {
            state: "failed",
            ...(reason instanceof ApiError && reason.status < 500
              ? {
                  createIdempotencyKey: newIdempotencyKey(),
                  finalizeIdempotencyKey: newIdempotencyKey(),
                }
              : {}),
            message:
              reason instanceof Error
                ? reason.message
                : "ページを送信できませんでした。",
          });
        }
      }
    }
    await Promise.all(
      Array.from({ length: Math.min(3, pending.length) }, () => worker()),
    );
    return failed;
  }

  async function stagePages() {
    if (
      working ||
      !isOpen ||
      batch ||
      !validExpectedPageCount ||
      !pages.length ||
      hasIncompleteGroup
    ) {
      return;
    }

    setWorking(true);
    setActionError(undefined);
    const snapshot = pages.map((page, index) => ({
      ...page,
      inputOrdinal: index + 1,
    }));
    const createManifest = snapshot
      .map((page) => `${page.id}:${page.inputOrdinal}`)
      .join("|");
    if (createAttemptRef.current?.manifest !== createManifest) {
      createAttemptRef.current = {
        manifest: createManifest,
        idempotencyKey: newIdempotencyKey(),
      };
    }

    try {
      const created = await orderedScanApi.create(
        sessionId,
        {
          items: snapshot.map((page) => ({
            clientItemId: page.id,
            fileName: page.file.name,
            inputOrdinal: page.inputOrdinal,
          })),
        },
        createAttemptRef.current.idempotencyKey,
      );
      setBatch(created);
      window.sessionStorage.setItem(
        orderedScanBatchStorageKey(sessionId),
        created.id,
      );

      const failed = await uploadLocalPages(created.id, snapshot);
      const refreshed = await orderedScanApi.get(created.id);
      setBatch(refreshed);
      if (failed) {
        setActionError(
          "送信できなかったページがあります。並び順は固定されています。新しいバッチを作らず、再送してください。",
        );
      }
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "答案ページの送信を開始できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function retryFailedPages() {
    if (!batch || working || !failedCount) return;
    setWorking(true);
    setActionError(undefined);
    try {
      const pending = pages.flatMap((page, index) =>
        page.state === "failed"
          ? [{
              id: page.id,
              file: page.file,
              inputOrdinal: index + 1,
              createIdempotencyKey: page.createIdempotencyKey,
              finalizeIdempotencyKey: page.finalizeIdempotencyKey,
            }]
          : [],
      );
      const failed = await uploadLocalPages(batch.id, pending);
      setBatch(await orderedScanApi.get(batch.id));
      if (failed) {
        setActionError("再送できなかったページがあります。もう一度お試しください。");
      }
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "ページを再送できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function finalizeBatch() {
    if (!batch || working || !canFinalize) {
      return;
    }
    setWorking(true);
    setActionError(undefined);
    try {
      const latest = await orderedScanApi.get(batch.id);
      const result = await orderedScanApi.finalize(
        latest.id,
        latest.rowVersion,
      );
      setBatch(result);
      onBatchChanged();
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "答案の組み立てを開始できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function cancelBatch(startOver: boolean) {
    if (
      !batch ||
      working ||
      !["draft", "needsReview", "failed", "expired"].includes(batch.status)
    ) {
      return;
    }
    if (
      !window.confirm(
        startOver
          ? "このスキャンバッチを取り消してやり直しますか？答案に使われていない受信済みページは解放されます。"
          : "このスキャンバッチを取り消しますか？受信済みページから答案は作成されません。",
      )
    ) {
      return;
    }
    setWorking(true);
    setActionError(undefined);
    try {
      const latest = await orderedScanApi.get(batch.id);
      const cancelled = await orderedScanApi.cancel(
        latest.id,
        latest.rowVersion,
      );
      if (startOver) {
        clearLocalBatch();
      } else {
        setBatch(cancelled);
      }
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "スキャンバッチを取り消せませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  function clearLocalBatch() {
    window.sessionStorage.removeItem(orderedScanBatchStorageKey(sessionId));
    setBatch(undefined);
    setPages([]);
    setActionError(undefined);
  }

  function resetForNextBatch() {
    if (!resetWithoutCleanup || !batch) return;
    clearLocalBatch();
  }

  return (
    <Card className="upload-board ordered-scan-board">
      <div className="card__header">
        <div>
          <h2>スキャン順で答案をアップロード</h2>
          <p>
            1ページずつのファイルを、各生徒の1ページ目から順番に並べます。氏名は各答案の1ページ目から読み取ります。
          </p>
        </div>
        {validExpectedPageCount ? (
          <Badge tone="info">1答案 {validExpectedPageCount}ページ</Badge>
        ) : null}
      </div>

      {actionError ? (
        <InlineAlert tone="danger">
          <p>{actionError}</p>
        </InlineAlert>
      ) : null}

      {!validExpectedPageCount ? (
        <InlineAlert tone="warning" title="答案のページ数を確認できません">
          <p>使用するひな形のページ数を確認してから、もう一度開いてください。</p>
        </InlineAlert>
      ) : (
        <>
          <InlineAlert tone="warning" title="スキャン順が生徒のまとまりになります">
            <p>
              各生徒の答案を1ページ目から最後のページまで続けて並べてください。ページの種類や抜けは確認できますが、2ページ目以降だけでは別の生徒との取り違えを判定できません。
            </p>
          </InlineAlert>
          {!isOpen ? (
            <InlineAlert tone="warning" title="答案の受付は終了しています">
              <p>
                新しいページの送信はできません。受信済みバッチの処理状況は引き続き確認できます。
              </p>
            </InlineAlert>
          ) : null}
          {isOpen && !frozen ? (
            <div
              className={classNames(
                "file-drop-zone",
                "file-drop-zone--session",
                dragging && "is-dragging",
              )}
              onDragOver={(event) => {
                event.preventDefault();
                setDragging(true);
              }}
              onDragLeave={() => setDragging(false)}
              onDrop={handleDrop}
            >
              <input
                ref={inputRef}
                type="file"
                accept=".pdf,application/pdf"
                multiple
                onChange={handleFileInput}
                disabled={working}
              />
              <span className="file-drop-zone__icon">
                <Icon name="upload" size={28} />
              </span>
              <strong>1ページずつの答案をここにドロップ</strong>
              <span>ファイル名の数字を考慮して自然順に並べます</span>
              <Button
                type="button"
                variant="secondary"
                onClick={() => inputRef.current?.click()}
                disabled={working}
              >
                ファイルを選択
              </Button>
              <small>PDF / 各ファイルは必ず1ページ</small>
            </div>
          ) : null}

          {pages.length ? (
            <div className="ordered-scan-workspace">
              <div className="ordered-scan-summary" aria-live="polite">
                <div>
                  <strong>{pages.length}ページ</strong>
                  <span>
                    見込み {Math.ceil(pages.length / validExpectedPageCount)}答案・完成{" "}
                    {Math.floor(pages.length / validExpectedPageCount)}答案
                  </span>
                </div>
                {frozen ? (
                  <Badge tone="neutral">並び順を固定済み</Badge>
                ) : (
                  <Button
                    size="small"
                    variant="quiet"
                    onClick={() => setPages(naturalSortScanItems(pages))}
                  >
                    ファイル名で並べ直す
                  </Button>
                )}
              </div>

              {hasIncompleteGroup && !frozen ? (
                <InlineAlert tone="warning" title="最後の答案が未完成です">
                  <p>
                    あと
                    {validExpectedPageCount -
                      (pages.length % validExpectedPageCount)}
                    ページ追加すると送信できます。
                  </p>
                </InlineAlert>
              ) : null}

              <div className="ordered-scan-groups">
                {localGroups.map((group) => (
                  <section
                    className={classNames(
                      "ordered-scan-group",
                      !group.complete && "ordered-scan-group--incomplete",
                    )}
                    key={group.groupNumber}
                    aria-label={`答案 ${group.groupNumber}`}
                  >
                    <div className="ordered-scan-group__header">
                      <div>
                        <strong>答案 {group.groupNumber}</strong>
                        <span>
                          {group.items.length} / {validExpectedPageCount}ページ
                        </span>
                      </div>
                      <Badge tone={group.complete ? "success" : "warning"}>
                        {group.complete ? "完成" : "不足"}
                      </Badge>
                    </div>
                    <div className="ordered-scan-group__pages">
                      {group.items.map(({ item, pageNumber, inputOrdinal }) => {
                        const index = inputOrdinal - 1;
                        return (
                          <div
                            className={classNames(
                              "ordered-scan-page",
                              `ordered-scan-page--${item.state}`,
                            )}
                            key={item.id}
                          >
                            <PagePreview file={item.file} />
                            <div className="ordered-scan-page__copy">
                              <div>
                                <Badge tone={pageNumber === 1 ? "accent" : "neutral"}>
                                  {pageNumber} / {validExpectedPageCount}
                                </Badge>
                                <span>読取順 {inputOrdinal}</span>
                              </div>
                              <strong title={item.file.name}>{item.file.name}</strong>
                              <small>
                                {pageNumber === 1
                                  ? "氏名を読み取るページ"
                                  : `同じ答案の${pageNumber}ページ目`}
                              </small>
                              {item.state === "uploading" ? (
                                <div
                                  className="upload-progress"
                                  role="progressbar"
                                  aria-valuenow={item.progress}
                                  aria-valuemin={0}
                                  aria-valuemax={100}
                                >
                                  <span style={{ width: `${item.progress}%` }} />
                                </div>
                              ) : null}
                              {item.message ? <small>{item.message}</small> : null}
                            </div>
                            <PageState state={item.state} />
                            {!frozen ? (
                              <div className="ordered-scan-page__actions">
                                <button
                                  type="button"
                                  aria-label={`${item.file.name}を1つ前へ移動`}
                                  disabled={index === 0}
                                  onClick={() =>
                                    setPages((current) =>
                                      moveScanItem(current, index, index - 1),
                                    )
                                  }
                                >
                                  <Icon name="arrowLeft" size={16} />
                                </button>
                                <button
                                  type="button"
                                  aria-label={`${item.file.name}を1つ後へ移動`}
                                  disabled={index === pages.length - 1}
                                  onClick={() =>
                                    setPages((current) =>
                                      moveScanItem(current, index, index + 1),
                                    )
                                  }
                                >
                                  <Icon name="arrowRight" size={16} />
                                </button>
                                <button
                                  type="button"
                                  aria-label={`${item.file.name}を一覧から削除`}
                                  onClick={() =>
                                    setPages((current) =>
                                      current.filter((page) => page.id !== item.id),
                                    )
                                  }
                                >
                                  <Icon name="close" size={16} />
                                </button>
                              </div>
                            ) : null}
                          </div>
                        );
                      })}
                    </div>
                  </section>
                ))}
              </div>

              <div className="upload-list-actions ordered-scan-actions">
                {!frozen ? (
                  <>
                    <Button
                      variant="secondary"
                      onClick={() => inputRef.current?.click()}
                      disabled={working || !isOpen}
                      leadingIcon="plus"
                    >
                      ページを追加
                    </Button>
                    <Button
                      onClick={() => void stagePages()}
                      disabled={working || !isOpen || hasIncompleteGroup}
                      leadingIcon="upload"
                    >
                      {working ? "送信中…" : "この順番でページを送信"}
                    </Button>
                  </>
                ) : (
                  <>
                    <span>
                      {stagedCount} / {pages.length}ページを受信済み
                    </span>
                    {failedCount ? (
                      <Button
                        variant="secondary"
                        onClick={() => void retryFailedPages()}
                        disabled={working || !isOpen}
                        leadingIcon="retry"
                      >
                        失敗した{failedCount}ページを再送
                      </Button>
                    ) : null}
                    <Button
                      onClick={() => void finalizeBatch()}
                      disabled={working || !canFinalize}
                      leadingIcon="check"
                    >
                      {working ? "処理中…" : "答案を組み立てて採点へ"}
                    </Button>
                  </>
                )}
              </div>
            </div>
          ) : batch ? (
            <RecoveredBatch batch={batch} groups={serverGroups} />
          ) : null}

          {batch ? (
            <div className="ordered-scan-batch-status">
              <div>
                <strong>バッチの状態</strong>
                <StatusBadge status={batch.status} />
                <span>
                  {batch.itemCount}ページ・{batch.submissionIds.length}答案
                </span>
              </div>
              {batch.status === "processing" ? (
                <p>ページ順を検証し、答案を組み立てています。この画面を閉じても処理は続きます。</p>
              ) : null}
              {!pages.length && batch.status === "draft" ? (
                <p>
                  {serverUploadedCount} / {batch.itemCount}ページを受信済みです。
                  {serverReadyToFinalize
                    ? "元のファイルを選び直さず、このまま組み立てを開始できます。"
                    : "未送信のページがあるため、このバッチはまだ組み立てできません。"}
                </p>
              ) : null}
              {batch.issues.length ? (
                <div className="ordered-scan-issues" role="alert">
                  <strong>確認が必要な項目</strong>
                  <ul>
                    {batch.issues.map((issue, index) => (
                      <li
                        key={`${issue.code}:${issue.inputOrdinal ?? "batch"}:${issue.groupOrdinal ?? "batch"}:${index}`}
                      >
                        {issue.inputOrdinal
                          ? `読取順 ${issue.inputOrdinal}: `
                          : ""}
                        {issue.message}
                      </li>
                    ))}
                  </ul>
                </div>
              ) : null}
              {resetWithoutCleanup ? (
                <Button variant="secondary" onClick={resetForNextBatch}>
                  次のスキャンバッチを追加
                </Button>
              ) : cleanupBeforeReset ? (
                <Button
                  variant="secondary"
                  onClick={() => void cancelBatch(true)}
                  disabled={working}
                >
                  取り消して次のバッチを追加
                </Button>
              ) : !pages.length && serverReadyToFinalize ? (
                <Button
                  onClick={() => void finalizeBatch()}
                  disabled={working}
                  leadingIcon="check"
                >
                  {working ? "処理中…" : "答案を組み立てて採点へ"}
                </Button>
              ) : null}
              {batch.status === "draft" ? (
                <Button
                  variant="quiet"
                  onClick={() => void cancelBatch(false)}
                  disabled={working}
                >
                  このバッチを取り消す
                </Button>
              ) : null}
            </div>
          ) : null}
        </>
      )}
    </Card>
  );
}

function PagePreview({ file }: { file: File }) {
  const [url, setUrl] = useState<string>();
  useEffect(() => {
    if (typeof URL.createObjectURL !== "function") return;
    const next = URL.createObjectURL(file);
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [file]);

  if (url && file.type.startsWith("image/")) {
    return <img className="ordered-scan-page__preview" src={url} alt="" />;
  }
  return (
    <span className="ordered-scan-page__preview ordered-scan-page__preview--file">
      <Icon name="file" size={22} />
      {url ? (
        <a href={url} target="_blank" rel="noreferrer">
          プレビュー
        </a>
      ) : null}
    </span>
  );
}

function PageState({ state }: { state: LocalPageState }) {
  if (state === "ready") return <Badge tone="neutral">準備済み</Badge>;
  if (state === "uploading") return <Badge tone="info">送信中</Badge>;
  if (state === "staged") return <Badge tone="success">受信済み</Badge>;
  return <Badge tone="danger">送信失敗</Badge>;
}

function RecoveredBatch({
  batch,
  groups,
}: {
  batch: OrderedScanBatchDetail;
  groups: ReturnType<typeof groupOrderedScans<OrderedScanBatchDetail["items"][number]>>;
}) {
  const groupedIds = new Set(
    groups.flatMap((group) => group.items.map(({ item }) => item.id)),
  );
  const ungroupedItems = batch.items
    .filter((item) => !groupedIds.has(item.id))
    .sort((left, right) => left.inputOrdinal - right.inputOrdinal);
  return (
    <div className="ordered-scan-workspace">
      <InlineAlert tone="info" title="送信済みバッチを復元しました">
        <p>固定済みの読取順とサーバー側の処理状況を表示しています。</p>
      </InlineAlert>
      <div className="ordered-scan-groups ordered-scan-groups--recovered">
        {groups.map((group) => (
          <section className="ordered-scan-group" key={group.groupNumber}>
            <div className="ordered-scan-group__header">
              <strong>答案 {group.groupNumber}</strong>
              <span>
                {group.items.length} / {batch.expectedPageCount}ページ
              </span>
            </div>
            <ol>
              {group.items.map(({ item, pageNumber }) => (
                <li key={item.clientItemId}>
                  <Badge tone={pageNumber === 1 ? "accent" : "neutral"}>
                    {pageNumber} / {batch.expectedPageCount}
                  </Badge>
                  <span>{item.fileName}</span>
                  {item.detectedTemplatePageNumber ? (
                    <small>
                      ひな形{item.detectedTemplatePageNumber}ページと判定
                    </small>
                  ) : null}
                </li>
              ))}
            </ol>
          </section>
        ))}
      </div>
      {ungroupedItems.length ? (
        <section className="ordered-scan-ungrouped">
          <strong>答案に割り当てられていないページ</strong>
          <ul>
            {ungroupedItems.map((item) => (
              <li key={item.id}>
                <Badge tone="warning">読取順 {item.inputOrdinal}</Badge>
                <span>{item.fileName}</span>
                <small>ページの確認が必要です</small>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}

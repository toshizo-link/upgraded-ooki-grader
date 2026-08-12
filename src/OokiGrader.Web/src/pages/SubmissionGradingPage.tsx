import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
} from "react";
import { Link, useParams } from "../router";
import { Icon } from "../components/Icon";
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  Field,
  InlineAlert,
  LoadingState,
  Modal,
  PageHeader,
  Score,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import {
  ApiError,
  api,
  newIdempotencyKey,
  requestWithMeta,
} from "../lib/api";
import {
  classNames,
  formatDate,
  formatDateTime,
  formatPoints,
} from "../lib/format";
import type {
  SubmissionBulkConfirmResponse,
  SubmissionGradingResult,
  SubmissionGradingSnapshotItem,
  SubmissionGradingWorkspace,
} from "../types";

type ViewerMode = "pdf" | "pages";

interface WorkspaceEnvelope {
  workspace: SubmissionGradingWorkspace;
  etag?: string;
}

interface EditDraft {
  pointsMilli: number;
  outcome: string;
  transcription: string;
  reasonCode: string;
  note: string;
}

interface BulkSnapshot {
  sourceSubmissionRevision: number;
  gradingRunId: string;
  sourceResultSourceRevision: number;
  items: SubmissionGradingSnapshotItem[];
  etag?: string;
  idempotencyKey: string;
}

interface BulkReport {
  confirmed: number;
  skipped: number;
  stale: number;
}

const UNSAVED_MESSAGE =
  "採点の編集内容が保存されていません。変更を破棄して移動しますか？";

export function SubmissionGradingPage() {
  const { submissionId = "" } = useParams();
  const query = useApiQuery<WorkspaceEnvelope>(
    `submission-grading:${submissionId}`,
    async (signal) => {
      const response = await requestWithMeta<SubmissionGradingWorkspace>(
        `/submissions/${encodeURIComponent(submissionId)}/grading-workspace`,
        { signal },
      );
      return { workspace: response.data, etag: response.etag };
    },
    Boolean(submissionId),
  );

  if (query.status === "loading") {
    return (
      <div className="page submission-grading-page">
        <LoadingState label="答案と採点結果を読み込んでいます" />
      </div>
    );
  }
  if (query.status === "error" || !query.data) {
    return (
      <div className="page submission-grading-page">
        <ErrorState error={query.error} onRetry={query.reload} />
      </div>
    );
  }

  return (
    <SubmissionGradingWorkspaceView
      envelope={query.data}
      reload={query.reload}
    />
  );
}

function SubmissionGradingWorkspaceView({
  envelope,
  reload,
}: {
  envelope: WorkspaceEnvelope;
  reload: () => void;
}) {
  const { workspace } = envelope;
  const sortedResults = useMemo(
    () =>
      [...workspace.results].sort(
        (left, right) => left.orderIndex - right.orderIndex,
      ),
    [workspace.results],
  );
  const sortedPages = useMemo(
    () =>
      [...workspace.pages].sort(
        (left, right) => left.pageNumber - right.pageNumber,
      ),
    [workspace.pages],
  );
  const originalPdfUrl = safeSameOriginUrl(
    workspace.originalPdf?.available ? workspace.originalPdf.url : undefined,
  );
  const [viewerMode, setViewerMode] = useState<ViewerMode>(
    originalPdfUrl ? "pdf" : "pages",
  );
  const [selectedResultId, setSelectedResultId] = useState(
    () =>
      sortedResults.find((result) => isUnresolved(result))?.resultId ||
      sortedResults[0]?.resultId ||
      "",
  );
  const selectedResult =
    sortedResults.find((result) => result.resultId === selectedResultId) ||
    sortedResults[0];
  const [selectedPageNumber, setSelectedPageNumber] = useState(
    () => selectedResult?.pageNumbers[0] || sortedPages[0]?.pageNumber || 1,
  );
  const [draft, setDraft] = useState<EditDraft>(() =>
    draftFromResult(selectedResult),
  );
  const [baseline, setBaseline] = useState<EditDraft>(() =>
    draftFromResult(selectedResult),
  );
  const [saving, setSaving] = useState(false);
  const [editError, setEditError] = useState<string>();
  const [editSaved, setEditSaved] = useState(false);
  const editAttemptRef = useRef<
    { signature: string; key: string } | undefined
  >(undefined);
  const [bulkOpen, setBulkOpen] = useState(false);
  const [bulkAcknowledged, setBulkAcknowledged] = useState(false);
  const [bulkSnapshot, setBulkSnapshot] = useState<BulkSnapshot>();
  const [bulkWorking, setBulkWorking] = useState(false);
  const [bulkError, setBulkError] = useState<string>();
  const [bulkReport, setBulkReport] = useState<BulkReport>();

  const dirty = !sameDraft(draft, baseline);
  const dirtyRef = useRef(dirty);
  dirtyRef.current = dirty;

  const finalized =
    workspace.submission.state === "finalized" ||
    Boolean(workspace.submission.finalizedAt);
  const archived = workspace.session.state === "archived";
  const readOnly = finalized || archived;
  const unresolvedCount = workspace.unresolvedSnapshot.length;

  useEffect(() => {
    if (
      selectedResultId &&
      sortedResults.some((result) => result.resultId === selectedResultId)
    ) {
      return;
    }
    setSelectedResultId(
      sortedResults.find((result) => isUnresolved(result))?.resultId ||
        sortedResults[0]?.resultId ||
        "",
    );
  }, [selectedResultId, sortedResults]);

  useEffect(() => {
    const next = draftFromResult(selectedResult);
    setDraft(next);
    setBaseline(next);
    setEditError(undefined);
    setEditSaved(false);
    editAttemptRef.current = undefined;
    const evidencePage = selectedResult?.pageNumbers.find((pageNumber) =>
      sortedPages.some((page) => page.pageNumber === pageNumber),
    );
    setSelectedPageNumber(
      evidencePage || sortedPages[0]?.pageNumber || selectedPageNumber || 1,
    );
    // Page selection intentionally follows a newly selected result revision.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedResult?.resultId, selectedResult?.sourceResultRevision]);

  useEffect(() => {
    if (!originalPdfUrl && viewerMode === "pdf") setViewerMode("pages");
  }, [originalPdfUrl, viewerMode]);

  useEffect(() => {
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      if (!dirtyRef.current) return;
      event.preventDefault();
      event.returnValue = "";
    };
    const handleLinkClick = (event: MouseEvent) => {
      if (!dirtyRef.current || event.defaultPrevented) return;
      const target = event.target;
      if (!(target instanceof Element)) return;
      const anchor = target.closest<HTMLAnchorElement>("a[href]");
      if (!anchor || anchor.target === "_blank" || anchor.hasAttribute("download")) {
        return;
      }
      const destination = new URL(anchor.href, window.location.href);
      if (
        destination.origin !== window.location.origin ||
        destination.href === window.location.href
      ) {
        return;
      }
      if (!window.confirm(UNSAVED_MESSAGE)) {
        event.preventDefault();
        event.stopPropagation();
      }
    };
    window.addEventListener("beforeunload", handleBeforeUnload);
    document.addEventListener("click", handleLinkClick, true);
    return () => {
      window.removeEventListener("beforeunload", handleBeforeUnload);
      document.removeEventListener("click", handleLinkClick, true);
    };
  }, []);

  function chooseResult(result: SubmissionGradingResult) {
    if (result.resultId === selectedResult?.resultId) return;
    if (dirty && !window.confirm(UNSAVED_MESSAGE)) return;
    setSelectedResultId(result.resultId);
  }

  function discardDraft() {
    const next = draftFromResult(selectedResult);
    setDraft(next);
    setBaseline(next);
  }

  function openBulkConfirmation() {
    if (
      !workspace.canBulkConfirm ||
      readOnly ||
      !workspace.gradingRun ||
      workspace.unresolvedSnapshot.length === 0
    ) {
      return;
    }
    if (dirty) {
      if (!window.confirm(UNSAVED_MESSAGE)) return;
      discardDraft();
    }
    setBulkSnapshot({
      sourceSubmissionRevision: workspace.submission.revision,
      gradingRunId: workspace.gradingRun.id,
      sourceResultSourceRevision:
        workspace.gradingRun.resultSourceRevision,
      items: workspace.unresolvedSnapshot.map((item) => ({ ...item })),
      etag: envelope.etag,
      idempotencyKey: newIdempotencyKey(),
    });
    setBulkAcknowledged(false);
    setBulkError(undefined);
    setBulkOpen(true);
  }

  async function saveOverride(event: FormEvent) {
    event.preventDefault();
    if (!selectedResult || saving || readOnly) return;
    const validation = validateDraft(draft, selectedResult);
    if (validation) {
      setEditError(validation);
      return;
    }
    setSaving(true);
    setEditError(undefined);
    setEditSaved(false);
    const body = {
      sourceResultRevision: selectedResult.sourceResultRevision,
      awardedPointsMilli: draft.pointsMilli,
      outcome: draft.outcome,
      // Always send the effective text shown to the teacher. The API treats a
      // missing value as "keep the previous correction" and an empty string as
      // an explicit correction to a blank answer.
      transcriptionCorrection: draft.transcription,
      reasonCode: draft.reasonCode,
      note: draft.note,
    };
    const signature = JSON.stringify(body);
    if (editAttemptRef.current?.signature !== signature) {
      editAttemptRef.current = { signature, key: newIdempotencyKey() };
    }
    try {
      await api.post(
        `/submissions/${encodeURIComponent(workspace.submission.id)}/results/${encodeURIComponent(selectedResult.resultId)}:override`,
        body,
        { idempotencyKey: editAttemptRef.current.key },
      );
      editAttemptRef.current = undefined;
      setBaseline({ ...draft });
      setEditSaved(true);
      reload();
    } catch (reason) {
      setEditError(
        reason instanceof ApiError && reason.status === 412
          ? "別の先生がこの採点を更新しました。再読み込みしてから確認してください。"
          : reason instanceof Error
            ? reason.message
            : "採点を保存できませんでした。",
      );
    } finally {
      setSaving(false);
    }
  }

  async function confirmUnresolved() {
    if (!bulkSnapshot || !bulkAcknowledged || bulkWorking) return;
    setBulkWorking(true);
    setBulkError(undefined);
    try {
      const response = await api.post<SubmissionBulkConfirmResponse>(
        `/submissions/${encodeURIComponent(workspace.submission.id)}/results:confirm-unresolved`,
        {
          sourceSubmissionRevision:
            bulkSnapshot.sourceSubmissionRevision,
          gradingRunId: bulkSnapshot.gradingRunId,
          sourceResultSourceRevision:
            bulkSnapshot.sourceResultSourceRevision,
          items: bulkSnapshot.items,
        },
        {
          idempotencyKey: bulkSnapshot.idempotencyKey,
          etag: bulkSnapshot.etag,
        },
      );
      const staleSkipped = response.skipped.filter((item) =>
        /STALE|REVISION|CHANGED/i.test(item.code),
      ).length;
      setBulkReport({
        confirmed: response.confirmed.length,
        skipped: response.skipped.length - staleSkipped,
        stale: staleSkipped,
      });
      setBulkOpen(false);
      setBulkSnapshot(undefined);
      reload();
    } catch (reason) {
      if (reason instanceof ApiError && reason.status === 412) {
        setBulkReport({
          confirmed: 0,
          skipped: 0,
          stale: bulkSnapshot.items.length,
        });
        setBulkError(
          "確認を開いた後に採点結果が更新されました。最新の状態を読み込んで、もう一度確認してください。",
        );
        setBulkOpen(false);
      } else {
        setBulkError(
          reason instanceof Error
            ? reason.message
            : "一括確認を保存できませんでした。",
        );
      }
    } finally {
      setBulkWorking(false);
    }
  }

  const selectedPage =
    sortedPages.find((page) => page.pageNumber === selectedPageNumber) ||
    sortedPages[0];
  const pdfPageUrl = originalPdfUrl
    ? `${originalPdfUrl}#page=${selectedPageNumber}&zoom=page-width`
    : undefined;
  const possiblePoints = workspace.gradingRun?.possiblePointsMilli ?? 0;
  const earnedPoints = workspace.gradingRun?.earnedPointsMilli ?? 0;
  const noOriginalEvidence =
    !originalPdfUrl &&
    (workspace.submission.scanPayloadState === "scan_deleted" ||
      sortedPages.every((page) => !page.available));

  return (
    <div className="page submission-grading-page">
      <PageHeader
        eyebrow="答案別採点"
        title={workspace.test.title}
        description={[
          formatDate(workspace.session.testDate),
          workspace.test.subject,
          workspace.test.gradeLabel,
          workspace.session.classLabel,
          workspace.test.course,
        ]
          .filter((value) => value && value !== "—")
          .join("・")}
        backAction={
          <Link
            className="back-link"
            to={`/sessions/${encodeURIComponent(workspace.session.id)}`}
          >
            <Icon name="arrowLeft" size={17} />
            テスト実施へ戻る
          </Link>
        }
        actions={
          <>
            <StatusBadge status={workspace.submission.state} />
            <Button
              variant="secondary"
              leadingIcon="check"
              disabled={
                !workspace.canBulkConfirm ||
                readOnly ||
                unresolvedCount === 0 ||
                bulkWorking
              }
              onClick={openBulkConfirmation}
            >
              未確認{unresolvedCount}問を一括確認
            </Button>
          </>
        }
      />

      {readOnly ? (
        <InlineAlert tone="info" title="この答案は読み取り専用です">
          <p>
            {archived
              ? "テスト実施がアーカイブされているため、答案と採点履歴のみ確認できます。"
              : "答案は確定済みです。採点を変更する場合は、先に所定の手順で答案を開き直してください。"}
          </p>
        </InlineAlert>
      ) : null}
      {noOriginalEvidence ? (
        <InlineAlert tone="warning" title="答案画像の保存期間が終了しています">
          <p>
            原本PDFとページ画像は表示できません。採点結果と記録済みの読み取り内容は引き続き確認できます。
            {workspace.submission.scanDeletedAt
              ? ` 削除日時: ${formatDateTime(workspace.submission.scanDeletedAt)}`
              : ""}
          </p>
        </InlineAlert>
      ) : null}
      {bulkReport ? (
        <InlineAlert
          tone={bulkReport.stale > 0 ? "warning" : "success"}
          title="一括確認の結果"
          action={
            bulkReport.stale > 0 ? (
              <Button variant="secondary" size="small" onClick={reload}>
                最新の状態を読み込む
              </Button>
            ) : undefined
          }
        >
          <p>
            確認済み {bulkReport.confirmed}件・対象外 {bulkReport.skipped}件・更新あり {bulkReport.stale}件
          </p>
          {bulkError ? <p>{bulkError}</p> : null}
        </InlineAlert>
      ) : bulkError && !bulkOpen ? (
        <InlineAlert tone="danger">
          <p>{bulkError}</p>
        </InlineAlert>
      ) : null}

      <section className="grading-metadata" aria-label="答案情報">
        <Card>
          <span>生徒</span>
          <strong>{workspace.student?.displayName || "未割り当て"}</strong>
          <small>
            {[
              workspace.student?.studentNumber,
              workspace.student?.schoolClass,
              workspace.student?.gradeLabel,
            ]
              .filter(Boolean)
              .join("・") || "生徒情報なし"}
          </small>
        </Card>
        <Card>
          <span>得点</span>
          <Score
            earned={formatPoints(earnedPoints)}
            possible={formatPoints(possiblePoints)}
          />
          <small>{sortedResults.length}問</small>
        </Card>
        <Card>
          <span>テスト</span>
          <strong>
            {workspace.test.title}
            {workspace.test.templateVersionNumber
              ? ` 第${workspace.test.templateVersionNumber}版`
              : ""}
          </strong>
          <small>
            {[workspace.test.category, workspace.test.course]
              .filter(Boolean)
              .join("・") || "分類未設定"}
          </small>
        </Card>
        <Card>
          <span>答案</span>
          <strong>{workspace.submission.pageCount}ページ</strong>
          <small>{workspace.submission.fileName || "ファイル名なし"}</small>
        </Card>
      </section>

      <div className="submission-grading-layout">
        <Card className="submission-document-card">
          <header className="submission-document-card__header">
            <div>
              <h2>答案</h2>
              <p>
                {selectedResult
                  ? `${selectedResult.displayLabel} の参照ページを表示中`
                  : "答案全体を表示します"}
              </p>
            </div>
            <div
              className="viewer-mode-switch"
              role="group"
              aria-label="答案の表示方法"
            >
              <button
                type="button"
                className={viewerMode === "pdf" ? "is-active" : ""}
                aria-pressed={viewerMode === "pdf"}
                disabled={!originalPdfUrl}
                onClick={() => setViewerMode("pdf")}
              >
                PDF
              </button>
              <button
                type="button"
                className={viewerMode === "pages" ? "is-active" : ""}
                aria-pressed={viewerMode === "pages"}
                onClick={() => setViewerMode("pages")}
              >
                ページ画像
              </button>
            </div>
          </header>

          {viewerMode === "pdf" && pdfPageUrl ? (
            <div className="submission-pdf-viewer">
              <iframe
                src={pdfPageUrl}
                title={`答案PDF（${selectedPageNumber}ページ目）`}
                loading="lazy"
                referrerPolicy="no-referrer"
              />
              <small>
                PDFが表示されない場合は「ページ画像」に切り替えてください。
              </small>
            </div>
          ) : (
            <PageImageViewer
              pages={sortedPages}
              selectedPageNumber={selectedPage?.pageNumber || 1}
              onSelectPage={setSelectedPageNumber}
            />
          )}
        </Card>

        <div className="submission-results-column">
          <Card className="submission-result-list-card">
            <header>
              <div>
                <h2>全問題</h2>
                <p>問題を選ぶと答案の該当ページへ移動します。</p>
              </div>
              <Badge tone={unresolvedCount ? "warning" : "success"}>
                未確認 {unresolvedCount}
              </Badge>
            </header>
            {sortedResults.length ? (
              <div className="submission-result-list" aria-label="採点結果一覧">
                {sortedResults.map((result) => (
                  <button
                    type="button"
                    key={result.resultId}
                    className={classNames(
                      result.resultId === selectedResult?.resultId &&
                        "is-selected",
                    )}
                    aria-pressed={result.resultId === selectedResult?.resultId}
                    onClick={() => chooseResult(result)}
                  >
                    <span className="submission-result-list__label">
                      {result.displayLabel}
                    </span>
                    <span className="submission-result-list__answer">
                      <strong>{result.transcription || "（無解答）"}</strong>
                      <small>{reviewLabel(result)}</small>
                    </span>
                    <Score
                      compact
                      earned={formatPoints(result.awardedPointsMilli)}
                      possible={formatPoints(result.maxPointsMilli)}
                    />
                    <Icon name="chevronRight" size={16} />
                  </button>
                ))}
              </div>
            ) : (
              <EmptyState
                icon="file"
                title="採点結果がありません"
                description="AI採点が完了すると、全問題の結果がここに表示されます。"
              />
            )}
          </Card>

          {selectedResult ? (
            <Card className="submission-result-editor">
              <header>
                <div>
                  <Badge tone="neutral">{selectedResult.displayLabel}</Badge>
                  <h2>{selectedResult.questionText}</h2>
                  <p>
                    正解: {selectedResult.expectedAnswers.join("・") || "採点基準を参照"}
                  </p>
                </div>
                <StatusBadge status={selectedResult.outcome} />
              </header>
              <div className="grading-rule-badges">
                {selectedResult.kanjiRequired ? (
                  <Badge tone="warning">漢字必須</Badge>
                ) : null}
                {selectedResult.requiresCompleteAnswer ? (
                  <Badge tone="neutral">完答</Badge>
                ) : null}
                {selectedResult.answerOrderInsensitive ? (
                  <Badge tone="neutral">順不同</Badge>
                ) : null}
                {selectedResult.reviewRequired ? (
                  <Badge tone={isUnresolved(selectedResult) ? "warning" : "success"}>
                    {isUnresolved(selectedResult) ? "先生の確認待ち" : "確認済み"}
                  </Badge>
                ) : null}
              </div>
              {selectedResult.explanation || selectedResult.reason ? (
                <InlineAlert
                  tone={isUnresolved(selectedResult) ? "warning" : "info"}
                  title="AIの判断"
                >
                  <p>
                    {selectedResult.explanation || selectedResult.reason}
                    {selectedResult.confidenceBasisPoints !== null &&
                    selectedResult.confidenceBasisPoints !== undefined
                      ? `（確信度 ${Math.round(selectedResult.confidenceBasisPoints / 100)}%）`
                      : ""}
                  </p>
                </InlineAlert>
              ) : null}
              {editError ? (
                <InlineAlert
                  tone="danger"
                  action={
                    editError.includes("再読み込み") ? (
                      <Button variant="secondary" size="small" onClick={reload}>
                        再読み込み
                      </Button>
                    ) : undefined
                  }
                >
                  <p>{editError}</p>
                </InlineAlert>
              ) : null}
              {editSaved ? (
                <InlineAlert tone="success">
                  <p>採点を保存しました。</p>
                </InlineAlert>
              ) : null}
              <form onSubmit={saveOverride}>
                <Field
                  label="読み取り結果"
                  htmlFor={`grading-transcription-${selectedResult.resultId}`}
                >
                  <input
                    id={`grading-transcription-${selectedResult.resultId}`}
                    value={draft.transcription}
                    disabled={readOnly || saving}
                    onChange={(event) =>
                      setDraft((current) => ({
                        ...current,
                        transcription: event.target.value,
                      }))
                    }
                  />
                </Field>
                <Field
                  label="点数"
                  htmlFor={`grading-points-${selectedResult.resultId}`}
                  hint={`0〜${formatPoints(selectedResult.maxPointsMilli)}点（${formatPoints(selectedResult.pointIncrementMilli)}点単位）`}
                >
                  <div className="submission-points-input">
                    <input
                      id={`grading-points-${selectedResult.resultId}`}
                      type="number"
                      min={0}
                      max={selectedResult.maxPointsMilli / 1000}
                      step={selectedResult.pointIncrementMilli / 1000}
                      value={draft.pointsMilli / 1000}
                      disabled={readOnly || saving}
                      onChange={(event) =>
                        setDraft((current) => {
                          const pointsMilli = Math.round(
                            Number(event.target.value) * 1000,
                          );
                          return {
                            ...current,
                            pointsMilli,
                            outcome: outcomeForChangedPoints(
                              pointsMilli,
                              current.outcome,
                              selectedResult,
                            ),
                          };
                        })
                      }
                    />
                    <span>点</span>
                  </div>
                </Field>
                <Field
                  label="判定"
                  htmlFor={`grading-outcome-${selectedResult.resultId}`}
                >
                  <select
                    id={`grading-outcome-${selectedResult.resultId}`}
                    value={draft.outcome}
                    disabled={readOnly || saving}
                    onChange={(event) => {
                      const outcome = event.target.value;
                      setDraft((current) => ({
                        ...current,
                        outcome,
                        pointsMilli: pointsForChangedOutcome(
                          outcome,
                          current.pointsMilli,
                          selectedResult,
                        ),
                        transcription:
                          outcome === "blank" ? "" : current.transcription,
                      }));
                    }}
                  >
                    <option value="correct">正解</option>
                    <option
                      value="partial"
                      disabled={
                        selectedResult.requiresCompleteAnswer ||
                        selectedResult.maxPointsMilli <=
                          selectedResult.pointIncrementMilli
                      }
                    >
                      一部正解
                    </option>
                    <option value="incorrect">不正解</option>
                    <option value="blank">無解答</option>
                    <option value="unreadable">判読困難</option>
                  </select>
                </Field>
                <Field
                  label="変更・確認理由"
                  htmlFor={`grading-reason-${selectedResult.resultId}`}
                  required
                >
                  <select
                    id={`grading-reason-${selectedResult.resultId}`}
                    value={draft.reasonCode}
                    disabled={readOnly || saving}
                    onChange={(event) =>
                      setDraft((current) => ({
                        ...current,
                        reasonCode: event.target.value,
                      }))
                    }
                    required
                  >
                    <option value="teacher_judgment">先生が採点を確認</option>
                    <option value="accepted_equivalent">別表記を正解と判断</option>
                    <option value="transcription_corrected">読み取りを修正</option>
                    <option value="partial_credit">部分点</option>
                    <option value="rubric_corrected">採点基準を修正</option>
                    <option value="scan_crop_issue">画像・読み取りの問題</option>
                    <option value="other">その他</option>
                  </select>
                </Field>
                <Field
                  label="メモ"
                  htmlFor={`grading-note-${selectedResult.resultId}`}
                  required={draft.reasonCode === "other"}
                >
                  <textarea
                    id={`grading-note-${selectedResult.resultId}`}
                    rows={2}
                    value={draft.note}
                    disabled={readOnly || saving}
                    required={draft.reasonCode === "other"}
                    onChange={(event) =>
                      setDraft((current) => ({
                        ...current,
                        note: event.target.value,
                      }))
                    }
                  />
                </Field>
                <div className="submission-editor-actions">
                  {dirty ? <Badge tone="warning">未保存の変更</Badge> : <span />}
                  <Button
                    type="submit"
                    disabled={
                      readOnly ||
                      saving ||
                      (draft.reasonCode === "other" && !draft.note.trim())
                    }
                  >
                    {saving ? "保存しています…" : "この採点を保存・確認"}
                  </Button>
                </div>
              </form>
            </Card>
          ) : null}
        </div>
      </div>

      <Modal
        open={bulkOpen}
        onClose={() => !bulkWorking && setBulkOpen(false)}
        title="この答案の未確認項目を一括確認しますか？"
        description="画面を開いた時点の採点結果だけを、変更せず確認済みにします。"
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              disabled={bulkWorking}
              onClick={() => setBulkOpen(false)}
            >
              戻る
            </Button>
            <Button
              disabled={!bulkAcknowledged || bulkWorking}
              onClick={() => void confirmUnresolved()}
            >
              {bulkWorking
                ? "確認を保存しています…"
                : `${bulkSnapshot?.items.length || 0}問を確認済みにする`}
            </Button>
          </>
        }
      >
        {bulkError ? (
          <InlineAlert tone="danger">
            <p>{bulkError}</p>
          </InlineAlert>
        ) : null}
        <InlineAlert tone="warning" title="点数や読み取り内容は変更しません">
          <p>
            対象は現在表示している1人分の答案だけです。別の先生が1件でも更新していた場合は一括確認を中止し、最新の状態を読み込み直します。
          </p>
        </InlineAlert>
        <label className="setting-check grading-bulk-acknowledgment">
          <input
            type="checkbox"
            checked={bulkAcknowledged}
            disabled={bulkWorking}
            onChange={(event) => setBulkAcknowledged(event.target.checked)}
          />
          <span>
            <strong>
              この答案の未確認{bulkSnapshot?.items.length || 0}問を確認しました
            </strong>
            <small>各問題の点数と読み取り内容が妥当であることを確認済みです。</small>
          </span>
        </label>
      </Modal>
    </div>
  );
}

function PageImageViewer({
  pages,
  selectedPageNumber,
  onSelectPage,
}: {
  pages: SubmissionGradingWorkspace["pages"];
  selectedPageNumber: number;
  onSelectPage: (pageNumber: number) => void;
}) {
  const selectedIndex = Math.max(
    0,
    pages.findIndex((page) => page.pageNumber === selectedPageNumber),
  );
  const selectedPage = pages[selectedIndex];
  const selectedContentUrl = safeSameOriginUrl(selectedPage?.contentUrl);

  if (!pages.length) {
    return (
      <EmptyState
        icon="file"
        title="答案ページを表示できません"
        description="画像の保存期間が終了しているか、ページ情報がありません。"
      />
    );
  }

  return (
    <div className="submission-page-viewer">
      <div className="submission-page-viewer__toolbar">
        <Button
          type="button"
          variant="quiet"
          size="small"
          disabled={selectedIndex <= 0}
          onClick={() => {
            const previous = pages[selectedIndex - 1];
            if (previous) onSelectPage(previous.pageNumber);
          }}
        >
          前のページ
        </Button>
        <strong>
          {selectedPage?.pageNumber || 1} / {pages.length}ページ
        </strong>
        <Button
          type="button"
          variant="quiet"
          size="small"
          disabled={selectedIndex >= pages.length - 1}
          onClick={() => {
            const next = pages[selectedIndex + 1];
            if (next) onSelectPage(next.pageNumber);
          }}
        >
          次のページ
        </Button>
      </div>
      <div className="submission-page-viewer__canvas">
        {selectedPage?.available && selectedContentUrl ? (
          <img
            key={selectedContentUrl}
            src={selectedContentUrl}
            alt={`答案の${selectedPage.pageNumber}ページ目`}
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="submission-page-viewer__unavailable">
            <Icon name="file" size={30} />
            <strong>このページの画像は表示できません</strong>
            <span>保存期間が終了していても、採点結果は確認できます。</span>
          </div>
        )}
      </div>
      <div className="submission-page-thumbnails" aria-label="答案ページ">
        {pages.map((page) => {
          const thumbnailUrl = safeSameOriginUrl(page.thumbnailUrl);
          return (
            <button
              type="button"
              key={page.id}
              className={
                page.pageNumber === selectedPage?.pageNumber ? "is-selected" : ""
              }
              aria-label={`${page.pageNumber}ページ目を表示`}
              aria-pressed={page.pageNumber === selectedPage?.pageNumber}
              onClick={() => onSelectPage(page.pageNumber)}
            >
              {page.available && thumbnailUrl ? (
                <img
                  src={thumbnailUrl}
                  alt=""
                  loading="lazy"
                  decoding="async"
                />
              ) : (
                <span aria-hidden="true">
                  <Icon name="file" size={18} />
                </span>
              )}
              <small>{page.pageNumber}</small>
            </button>
          );
        })}
      </div>
    </div>
  );
}

function safeSameOriginUrl(value?: string | null) {
  if (!value) return undefined;
  try {
    const url = new URL(value, window.location.origin);
    if (url.origin !== window.location.origin) return undefined;
    return `${url.pathname}${url.search}`;
  } catch {
    return undefined;
  }
}

function isUnresolved(result: SubmissionGradingResult) {
  if (!result.reviewRequired) return false;
  return !["confirmed", "resolved", "overridden"].includes(
    result.reviewStatus.toLowerCase(),
  );
}

function reviewLabel(result: SubmissionGradingResult) {
  if (!result.reviewRequired) return "AI採点";
  return isUnresolved(result) ? "先生の確認待ち" : "確認済み";
}

function draftFromResult(result?: SubmissionGradingResult): EditDraft {
  return {
    pointsMilli: result?.awardedPointsMilli || 0,
    outcome: result?.outcome || "incorrect",
    transcription: result?.transcription || "",
    reasonCode: "teacher_judgment",
    note: "",
  };
}

function sameDraft(left: EditDraft, right: EditDraft) {
  return (
    left.pointsMilli === right.pointsMilli &&
    left.outcome === right.outcome &&
    left.transcription === right.transcription &&
    left.reasonCode === right.reasonCode &&
    left.note === right.note
  );
}

function validateDraft(
  draft: EditDraft,
  result: SubmissionGradingResult,
) {
  if (!Number.isFinite(draft.pointsMilli)) return "点数を入力してください。";
  if (draft.pointsMilli < 0 || draft.pointsMilli > result.maxPointsMilli) {
    return `点数は0〜${formatPoints(result.maxPointsMilli)}点で入力してください。`;
  }
  if (draft.pointsMilli % result.pointIncrementMilli !== 0) {
    return `${formatPoints(result.pointIncrementMilli)}点単位で入力してください。`;
  }
  if (
    result.requiresCompleteAnswer &&
    draft.pointsMilli !== 0 &&
    draft.pointsMilli !== result.maxPointsMilli
  ) {
    return "完答問題は0点または満点で入力してください。";
  }
  if (draft.outcome === "correct" && draft.pointsMilli !== result.maxPointsMilli) {
    return "「正解」の点数は満点にしてください。";
  }
  if (
    draft.outcome === "partial" &&
    (draft.pointsMilli <= 0 || draft.pointsMilli >= result.maxPointsMilli)
  ) {
    return "「一部正解」の点数は0点より大きく、満点より小さくしてください。";
  }
  if (
    ["incorrect", "blank", "unreadable"].includes(draft.outcome) &&
    draft.pointsMilli !== 0
  ) {
    return "不正解・無解答・判読困難の点数は0点にしてください。";
  }
  if (draft.outcome === "blank" && draft.transcription.trim()) {
    return "無解答にする場合は読み取り結果を空欄にしてください。";
  }
  if (draft.reasonCode === "other" && !draft.note.trim()) {
    return "「その他」を選んだ場合はメモを入力してください。";
  }
  return undefined;
}

function outcomeForChangedPoints(
  pointsMilli: number,
  currentOutcome: string,
  result: SubmissionGradingResult,
) {
  if (pointsMilli >= result.maxPointsMilli) return "correct";
  if (pointsMilli > 0) return "partial";
  if (["blank", "unreadable"].includes(currentOutcome)) return currentOutcome;
  return "incorrect";
}

function pointsForChangedOutcome(
  outcome: string,
  currentPointsMilli: number,
  result: SubmissionGradingResult,
) {
  if (outcome === "correct") return result.maxPointsMilli;
  if (outcome !== "partial") return 0;
  if (
    currentPointsMilli > 0 &&
    currentPointsMilli < result.maxPointsMilli &&
    currentPointsMilli % result.pointIncrementMilli === 0
  ) {
    return currentPointsMilli;
  }
  return result.pointIncrementMilli;
}

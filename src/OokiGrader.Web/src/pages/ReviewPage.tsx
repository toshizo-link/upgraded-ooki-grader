import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
} from "react";
import { Link, useSearchParams } from "../router";
import { useSession } from "../auth/SessionContext";
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
  SearchInput,
  Score,
  StatusBadge,
  Tabs,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { ApiError, api, asPaged, newIdempotencyKey } from "../lib/api";
import {
  classNames,
  formatDate,
  formatPoints,
} from "../lib/format";
import type {
  GradeReviewItem,
  NameCandidate,
  NameReviewItem,
  PagedResponse,
  StudentSummary,
  SubmissionSummary,
} from "../types";

type ReviewTab = "name" | "grading" | "finalize";
type ReviewMode = "question" | "paper";

export function ReviewPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const { hasAnyRole } = useSession();
  const canGrade = hasAnyRole("administrator", "teacher");
  const requested = searchParams.get("tab") as ReviewTab | null;
  const tab: ReviewTab =
    requested === "grading" || requested === "finalize"
      ? canGrade
        ? requested
        : "name"
      : "name";
  const submissionParam = searchParams.get("submission");

  const nameQueue = useApiQuery<PagedResponse<NameReviewItem>>(
    "review-name",
    async (signal) =>
      asPaged(await api.get("/review/name", { pageSize: 100 }, signal)),
    tab === "name",
  );
  const gradeQueue = useApiQuery<PagedResponse<GradeReviewItem>>(
    "review-grading",
    async (signal) =>
      asPaged(await api.get("/review/grading", { pageSize: 100 }, signal)),
    tab === "grading" && canGrade,
  );
  const finalizeQueue = useApiQuery<PagedResponse<SubmissionSummary>>(
    "review-finalize",
    async (signal) =>
      asPaged(
        await api.get(
          "/submissions",
          { state: "readyToFinalize", pageSize: 100 },
          signal,
        ),
      ),
    tab === "finalize" && canGrade,
  );

  const counts = {
    name: nameQueue.data?.totalApproximate ?? nameQueue.data?.items.length,
    grading:
      gradeQueue.data?.totalApproximate ?? gradeQueue.data?.items.length,
    finalize:
      finalizeQueue.data?.totalApproximate ?? finalizeQueue.data?.items.length,
  };

  function setTab(value: ReviewTab) {
    const next = new URLSearchParams(searchParams);
    next.set("tab", value);
    next.delete("submission");
    setSearchParams(next, { replace: true });
  }

  return (
    <div className="page review-page">
      <PageHeader
        eyebrow="確認待ち"
        title="採点待ち・確認"
        description="不確かな項目だけを先生が確認します。判断は履歴に残ります。"
      />
      <Tabs
        value={tab}
        onChange={setTab}
        label="確認内容"
        tabs={[
          { value: "name", label: "生徒名", count: counts.name },
          ...(canGrade
            ? [
                {
                  value: "grading" as const,
                  label: "採点",
                  count: counts.grading,
                },
                {
                  value: "finalize" as const,
                  label: "確定",
                  count: counts.finalize,
                },
              ]
            : []),
        ]}
      />
      {tab === "name" ? (
        <NameReview
          query={nameQueue}
          preferredSubmission={submissionParam || undefined}
        />
      ) : null}
      {tab === "grading" && canGrade ? (
        <GradeReview
          query={gradeQueue}
          preferredSubmission={submissionParam || undefined}
        />
      ) : null}
      {tab === "finalize" && canGrade ? (
        <FinalizeReview
          query={finalizeQueue}
          preferredSubmission={submissionParam || undefined}
        />
      ) : null}
    </div>
  );
}

function NameReview({
  query,
  preferredSubmission,
}: {
  query: ReturnType<typeof useApiQuery<PagedResponse<NameReviewItem>>>;
  preferredSubmission?: string;
}) {
  const [selectedId, setSelectedId] = useState("");
  const [candidateId, setCandidateId] = useState("");
  const [rosterSearch, setRosterSearch] = useState("");
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();
  const [duplicateConflict, setDuplicateConflict] = useState<{
    studentId: string;
    existingSubmissionId?: string;
    existingAttemptNumber?: number;
    nextAttemptNumber?: number;
  }>();
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const items = query.data?.items || [];
    if (!items.length) return;
    const preferred = items.find(
      (item) => item.submissionId === preferredSubmission,
    );
    if (!items.some((item) => item.id === selectedId)) {
      setSelectedId(preferred?.id || items[0]?.id || "");
    }
  }, [preferredSubmission, query.data, selectedId]);

  const selected = query.data?.items.find((item) => item.id === selectedId);
  useEffect(() => {
    setCandidateId(selected?.candidates[0]?.studentId || "");
    setRosterSearch("");
    setError(undefined);
  }, [selected?.id, selected?.candidates]);

  const roster = useApiQuery<PagedResponse<StudentSummary>>(
    `name-review-search:${rosterSearch}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/students",
          { search: rosterSearch, status: "active", pageSize: 10 },
          signal,
        ),
      ),
    rosterSearch.trim().length > 0,
  );

  const candidates = useMemo(() => {
    if (!rosterSearch.trim()) return selected?.candidates || [];
    return (roster.data?.items || []).map(
      (student): NameCandidate => ({
        studentId: student.id,
        displayName: student.displayName,
        kana:
          student.kana ||
          [student.familyNameKana, student.givenNameKana]
            .filter(Boolean)
            .join(" "),
        studentNumber: student.studentNumber,
        classLabel: student.classLabel,
        evidence: ["名簿検索"],
      }),
    );
  }, [roster.data, rosterSearch, selected?.candidates]);

  useEffect(() => {
    function handleKeyboard(event: KeyboardEvent) {
      const target = event.target as HTMLElement;
      if (
        ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName) ||
        target.isContentEditable
      ) {
        return;
      }
      if (event.key === "/") {
        event.preventDefault();
        searchRef.current?.focus();
      } else if (/^[1-5]$/.test(event.key)) {
        const candidate = candidates[Number(event.key) - 1];
        if (candidate) setCandidateId(candidate.studentId);
      } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        const current = Math.max(
          0,
          candidates.findIndex((item) => item.studentId === candidateId),
        );
        const next =
          event.key === "ArrowDown"
            ? Math.min(candidates.length - 1, current + 1)
            : Math.max(0, current - 1);
        if (candidates[next]) setCandidateId(candidates[next].studentId);
      } else if (event.key === "Enter" && candidateId && !working) {
        event.preventDefault();
        void assign(candidateId);
      }
    }
    document.addEventListener("keydown", handleKeyboard);
    return () => document.removeEventListener("keydown", handleKeyboard);
  });

  async function completeAction(
    action: "assignStudent" | "markUnidentified",
    body: object,
  ) {
    if (!selected || working) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        `/submissions/${encodeURIComponent(selected.submissionId)}:${action}`,
        body,
        { idempotencyKey: newIdempotencyKey() },
      );
      query.reload();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : "割り当てを保存できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function assign(
    studentId: string,
    duplicateResolution?: "additionalAttempt" | "replaceCanonical",
  ) {
    if (!selected || working) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        `/submissions/${encodeURIComponent(selected.submissionId)}:assignStudent`,
        {
          studentId,
          sourceRevision: selected.sourceRevision,
          reasonCode: duplicateResolution
            ? "teacher_resolved_duplicate"
            : "teacher_confirmed_handwriting",
          note: "",
          duplicateResolution,
          attemptNumber:
            duplicateResolution === "additionalAttempt"
              ? duplicateConflict?.nextAttemptNumber
              : undefined,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      setDuplicateConflict(undefined);
      query.reload();
    } catch (reason) {
      if (
        reason instanceof ApiError &&
        reason.problem.code === "CANONICAL_SUBMISSION_DUPLICATE"
      ) {
        setDuplicateConflict({
          studentId,
          existingSubmissionId: reason.problem.existingSubmissionId,
          existingAttemptNumber: reason.problem.existingAttemptNumber,
          nextAttemptNumber: reason.problem.nextAttemptNumber,
        });
      } else {
        setError(
          reason instanceof Error
            ? reason.message
            : "割り当てを保存できませんでした。",
        );
      }
    } finally {
      setWorking(false);
    }
  }

  if (query.status === "loading") {
    return <LoadingState label="生徒名の確認待ちを読み込んでいます" />;
  }
  if (query.status === "error") {
    return <ErrorState error={query.error} onRetry={query.reload} />;
  }
  if (!query.data?.items.length || !selected) {
    return (
      <Card>
        <EmptyState
          icon="check"
          title="生徒名の確認待ちはありません"
          description="自動割り当て基準を満たさなかった答案があると、ここに表示されます。"
        />
      </Card>
    );
  }

  return (
    <>
      <div className="review-layout">
      <aside className="review-queue-list" aria-label="確認待ち答案">
        <div className="review-queue-list__header">
          <strong>{query.data.items.length}件の確認待ち</strong>
          <small>答案を選択</small>
        </div>
        {query.data.items.map((item, index) => (
          <button
            type="button"
            className={classNames(item.id === selected.id && "is-selected")}
            aria-pressed={item.id === selected.id}
            key={item.id}
            onClick={() => setSelectedId(item.id)}
          >
            <span>{index + 1}</span>
            <div>
              <strong>{item.transcription || "判読できない氏名"}</strong>
              <small>{item.candidates.length}名の候補</small>
            </div>
            <Icon name="chevronRight" size={17} />
          </button>
        ))}
      </aside>
      <section className="name-review-workspace">
        {error ? (
          <InlineAlert tone="danger">
            <p>{error}</p>
          </InlineAlert>
        ) : null}
        <div className="review-workspace-heading">
          <div>
            <span>答案から読み取った氏名</span>
            <h2>{selected.transcription || "判読できませんでした"}</h2>
          </div>
          <Badge tone="warning">確認が必要</Badge>
        </div>
        <div className="name-review-grid">
          <div className="crop-panel">
            <span className="crop-panel__label">答案全体（1ページ目）</span>
            <div className="name-crop">
              {selected.nameCropUrl ? (
                <img src={selected.nameCropUrl} alt="答案の1ページ目" />
              ) : (
                <span>答案ページを表示できません</span>
              )}
            </div>
            {selected.qualityWarnings?.map((warning) => (
              <InlineAlert tone="warning" key={warning}>
                <p>{warning}</p>
              </InlineAlert>
            ))}
          </div>
          <div className="candidate-panel">
            <label className="search-input">
              <span className="sr-only">在籍中の生徒を検索</span>
              <Icon name="search" size={18} />
              <input
                ref={searchRef}
                type="search"
                value={rosterSearch}
                onChange={(event) => {
                  const value = event.target.value;
                  setRosterSearch(value);
                  setCandidateId(
                    value.trim()
                      ? ""
                      : selected.candidates[0]?.studentId || "",
                  );
                }}
                placeholder="名簿全体を検索（/）"
              />
            </label>
            {rosterSearch.trim() && roster.status === "loading" ? (
              <LoadingState compact label="名簿を検索しています" />
            ) : rosterSearch.trim() && roster.status === "error" ? (
              <ErrorState error={roster.error} onRetry={roster.reload} compact />
            ) : candidates.length ? (
              <div className="candidate-list" role="radiogroup" aria-label="生徒候補">
                {candidates.slice(0, 10).map((candidate, index) => (
                  <label
                    className={classNames(
                      candidateId === candidate.studentId && "is-selected",
                    )}
                    key={candidate.studentId}
                  >
                    <input
                      type="radio"
                      name="student-candidate"
                      value={candidate.studentId}
                      checked={candidateId === candidate.studentId}
                      onChange={() => setCandidateId(candidate.studentId)}
                    />
                    <span className="candidate-list__rank">
                      {index < 5 ? index + 1 : "・"}
                    </span>
                    <span className="candidate-list__copy">
                      <strong>{candidate.displayName}</strong>
                      <span>
                        {candidate.kana || "カナ未設定"}・
                        {candidate.studentNumber || "番号未設定"}
                      </span>
                      <small>
                        {[candidate.classLabel, ...(candidate.evidence || [])]
                          .filter(Boolean)
                          .join("・") || "在籍中"}
                      </small>
                    </span>
                    {candidate.confidenceLabel ? (
                      <Badge tone="neutral">{candidate.confidenceLabel}</Badge>
                    ) : null}
                  </label>
                ))}
              </div>
            ) : (
              <EmptyState
                icon="search"
                title="候補が見つかりません"
                description="氏名や生徒番号を変えて検索してください。"
              />
            )}
          </div>
        </div>
        <div className="review-footer">
          <div className="review-footer__secondary">
            <Button
              variant="quiet"
              disabled={working}
              onClick={() =>
                void completeAction("markUnidentified", {
                  sourceRevision: selected.sourceRevision,
                  status: "unidentified",
                })
              }
            >
              判読できない
            </Button>
            <Button
              variant="quiet"
              disabled={working}
              onClick={() =>
                void completeAction("markUnidentified", {
                  sourceRevision: selected.sourceRevision,
                  status: "nonStudentSample",
                })
              }
            >
              生徒の答案ではない
            </Button>
          </div>
          <Button
            size="large"
            disabled={!candidateId || working}
            onClick={() => void assign(candidateId)}
          >
            {working ? "割り当てています…" : "この生徒に割り当てる"}
          </Button>
        </div>
        <p className="keyboard-help">
          <kbd>1</kbd>〜<kbd>5</kbd> 候補を選択　<kbd>↑</kbd>
          <kbd>↓</kbd> 移動　<kbd>Enter</kbd> 決定　<kbd>/</kbd> 検索
        </p>
      </section>
      </div>
      <Modal
        open={Boolean(duplicateConflict)}
        onClose={() => !working && setDuplicateConflict(undefined)}
        title="同じ生徒の答案が既にあります"
        description="点数や進捗を二重に数えないよう、この答案の扱いを選んでください。"
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              disabled={working}
              onClick={() => setDuplicateConflict(undefined)}
            >
              生徒を選び直す
            </Button>
            <Button
              variant="quiet"
              disabled={working}
              onClick={() =>
                duplicateConflict &&
                void assign(
                  duplicateConflict.studentId,
                  "additionalAttempt",
                )
              }
            >
              第{duplicateConflict?.nextAttemptNumber ?? 2}回として登録
            </Button>
            <Button
              disabled={working}
              onClick={() =>
                duplicateConflict &&
                void assign(
                  duplicateConflict.studentId,
                  "replaceCanonical",
                )
              }
            >
              この答案を代表にする
            </Button>
          </>
        }
      >
        <InlineAlert tone="warning">
          <p>
            既存答案
            {duplicateConflict?.existingAttemptNumber
              ? `（第${duplicateConflict.existingAttemptNumber}回）`
              : ""}
            が代表答案として登録されています。
          </p>
        </InlineAlert>
        <p>
          「第{duplicateConflict?.nextAttemptNumber ?? 2}
          回として登録」は既存答案を進捗に残し、この答案を別受験として保存します。「この答案を代表にする」は既存答案を受験回へ移し、この答案を進捗に使用します。
        </p>
      </Modal>
    </>
  );
}

function GradeReview({
  query,
  preferredSubmission,
}: {
  query: ReturnType<typeof useApiQuery<PagedResponse<GradeReviewItem>>>;
  preferredSubmission?: string;
}) {
  const [mode, setMode] = useState<ReviewMode>("question");
  const [selectedId, setSelectedId] = useState("");
  const [points, setPoints] = useState(0);
  const [transcription, setTranscription] = useState("");
  const [reason, setReason] = useState("accepted_equivalent");
  const [note, setNote] = useState("");
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    const items = query.data?.items || [];
    const preferred = items.find(
      (item) => item.submissionId === preferredSubmission,
    );
    if (!items.some((item) => item.id === selectedId)) {
      setSelectedId(preferred?.id || items[0]?.id || "");
    }
  }, [preferredSubmission, query.data, selectedId]);
  const selected = query.data?.items.find((item) => item.id === selectedId);
  useEffect(() => {
    if (!selected) return;
    setPoints(selected.proposedPointsMilli / 1000);
    setTranscription(selected.transcription || "");
    setReason("accepted_equivalent");
    setNote("");
    setError(undefined);
  }, [selected]);

  async function saveOverride(event: FormEvent) {
    event.preventDefault();
    if (!selected) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        `/submissions/${encodeURIComponent(selected.submissionId)}/results/${encodeURIComponent(selected.resultId)}:override`,
        {
          sourceResultRevision: selected.sourceResultRevision,
          awardedPointsMilli: Math.round(points * 1000),
          outcome:
            points <= 0
              ? "incorrect"
              : points >= selected.maxPointsMilli / 1000
                ? "correct"
                : "partial",
          transcriptionCorrection:
            transcription !== selected.transcription ? transcription : undefined,
          reasonCode: reason,
          note,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      query.reload();
    } catch (reasonValue) {
      setError(
        reasonValue instanceof ApiError && reasonValue.status === 412
          ? "別の先生がこの答案を更新しました。最新の内容を読み込んでください。"
          : reasonValue instanceof Error
            ? reasonValue.message
            : "採点を保存できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  if (query.status === "loading") {
    return <LoadingState label="採点の確認待ちを読み込んでいます" />;
  }
  if (query.status === "error") {
    return <ErrorState error={query.error} onRetry={query.reload} />;
  }
  if (!query.data?.items.length || !selected) {
    return (
      <Card>
        <EmptyState
          icon="check"
          title="採点の確認待ちはありません"
          description="判読困難、部分点、漢字判定など先生の判断が必要な答案が表示されます。"
        />
      </Card>
    );
  }

  return (
    <>
      <div className="review-mode-switch">
        <span>確認方法</span>
        <div>
          <button
            type="button"
            className={mode === "question" ? "is-active" : ""}
            aria-pressed={mode === "question"}
            onClick={() => setMode("question")}
          >
            問題ごと
          </button>
          <button
            type="button"
            className={mode === "paper" ? "is-active" : ""}
            aria-pressed={mode === "paper"}
            onClick={() => setMode("paper")}
          >
            答案ごと
          </button>
        </div>
        <small>
          {mode === "question"
            ? "同じ問題を続けて確認します"
            : "1人分をまとめて確認します"}
        </small>
      </div>
      <div className="review-layout review-layout--grading">
        <aside className="review-queue-list" aria-label="採点確認待ち">
          <div className="review-queue-list__header">
            <strong>{query.data.items.length}件の確認待ち</strong>
            <small>{mode === "question" ? "問題順" : "答案順"}</small>
          </div>
          {query.data.items.map((item) => (
            <button
              type="button"
              className={item.id === selected.id ? "is-selected" : ""}
              aria-pressed={item.id === selected.id}
              key={item.id}
              onClick={() => setSelectedId(item.id)}
            >
              <span>{item.questionLabel}</span>
              <div>
                <strong>
                  {mode === "question"
                    ? item.transcription || "判読困難"
                    : item.studentDisplayName || "未割り当て"}
                </strong>
                <small>
                  {mode === "question"
                    ? item.studentDisplayName || "匿名答案"
                    : item.questionLabel}
                </small>
              </div>
              <Icon name="chevronRight" size={17} />
            </button>
          ))}
        </aside>
        <section className="grade-review-workspace">
          <header className="grade-context">
            <div>
              <Badge tone="neutral">{selected.questionLabel}</Badge>
              <h2>{selected.questionText}</h2>
              <p>
                正解:{" "}
                <strong>
                  {selected.expectedAnswers?.join("・") || "採点基準を参照"}
                </strong>
              </p>
            </div>
            <div>
              <span>配点</span>
              <strong>{formatPoints(selected.maxPointsMilli)}点</strong>
              {selected.kanjiRequired ? (
                <Badge tone="warning">漢字必須</Badge>
              ) : null}
            </div>
          </header>
          {error ? (
            <InlineAlert tone="danger">
              <p>{error}</p>
            </InlineAlert>
          ) : null}
          {selected.warning ? (
            <InlineAlert tone="warning" title="確認が必要な理由">
              <p>{selected.warning}</p>
            </InlineAlert>
          ) : null}
          {selected.qualityWarnings?.map((warning) => (
            <InlineAlert tone="warning" key={warning}>
              <p>{warning}</p>
            </InlineAlert>
          ))}
          <div className="grade-answer-card">
            <div className="grade-answer-card__crop">
              <span>答案全体（1ページ目）</span>
              {selected.answerCropUrl ? (
                <img src={selected.answerCropUrl} alt="答案の1ページ目" />
              ) : (
                <div>答案画像を表示できません</div>
              )}
            </div>
            <form onSubmit={saveOverride}>
              <Field label="読み取り結果" htmlFor="grade-transcription">
                <input
                  id="grade-transcription"
                  value={transcription}
                  onChange={(event) => setTranscription(event.target.value)}
                />
              </Field>
              <div className="proposed-grade">
                <span>提案</span>
                <StatusBadge status={selected.proposedOutcome} />
                <Score
                  compact
                  earned={formatPoints(selected.proposedPointsMilli)}
                  possible={formatPoints(selected.maxPointsMilli)}
                />
                {selected.reason ? <small>{selected.reason}</small> : null}
              </div>
              <fieldset>
                <legend>点数</legend>
                <div className="quick-score-buttons">
                  {Array.from(
                    new Set([
                      0,
                      Math.round(
                        selected.maxPointsMilli /
                          2 /
                          selected.pointIncrementMilli,
                      ) *
                        selected.pointIncrementMilli /
                        1000,
                      selected.maxPointsMilli / 1000,
                    ]),
                  ).map((value) => (
                    <button
                      type="button"
                      className={points === value ? "is-selected" : ""}
                      aria-pressed={points === value}
                      onClick={() => setPoints(value)}
                      key={value}
                    >
                      {value}点
                    </button>
                  ))}
                  <label>
                    <span className="sr-only">任意の点数</span>
                    <input
                      type="number"
                      min={0}
                      max={selected.maxPointsMilli / 1000}
                      step={selected.pointIncrementMilli / 1000}
                      value={points}
                      onChange={(event) => setPoints(Number(event.target.value))}
                    />
                    <span>点</span>
                  </label>
                </div>
              </fieldset>
              <Field label="変更理由" htmlFor="override-reason" required>
                <select
                  id="override-reason"
                  value={reason}
                  onChange={(event) => setReason(event.target.value)}
                  required
                >
                  <option value="accepted_equivalent">別表記を正解と判断</option>
                  <option value="transcription_corrected">読み取りを修正</option>
                  <option value="partial_credit">部分点</option>
                  <option value="rubric_corrected">採点基準を修正</option>
                  <option value="scan_crop_issue">画像・読み取りの問題</option>
                  <option value="teacher_judgment">先生の判断</option>
                  <option value="other">その他</option>
                </select>
              </Field>
              <Field
                label="メモ"
                htmlFor="override-note"
                required={reason === "other"}
              >
                <textarea
                  id="override-note"
                  rows={2}
                  value={note}
                  onChange={(event) => setNote(event.target.value)}
                  required={reason === "other"}
                />
              </Field>
              <div className="provisional-total">
                <span>この問題の確定点</span>
                <strong>
                  {points} / {selected.maxPointsMilli / 1000}点
                </strong>
              </div>
              <Button
                type="submit"
                size="large"
                disabled={
                  working ||
                  points < 0 ||
                  points > selected.maxPointsMilli / 1000 ||
                  (reason === "other" && !note.trim())
                }
              >
                {working ? "保存しています…" : "この採点を確定して次へ"}
              </Button>
            </form>
          </div>
        </section>
      </div>
    </>
  );
}

interface FinalizationCheck {
  key: string;
  label: string;
  passed: boolean;
  detail?: string;
}

interface SubmissionForFinalize extends SubmissionSummary {
  testTitle?: string;
  testDate?: string;
  finalizationChecks?: FinalizationCheck[];
}

function FinalizeReview({
  query,
  preferredSubmission,
}: {
  query: ReturnType<typeof useApiQuery<PagedResponse<SubmissionSummary>>>;
  preferredSubmission?: string;
}) {
  const [selectedId, setSelectedId] = useState("");
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    const items = query.data?.items || [];
    if (!items.some((item) => item.id === selectedId)) {
      const preferred = items.find((item) => item.id === preferredSubmission);
      setSelectedId(preferred?.id || items[0]?.id || "");
    }
  }, [preferredSubmission, query.data, selectedId]);
  const detail = useApiQuery<SubmissionForFinalize>(
    `finalize-detail:${selectedId}`,
    (signal) =>
      api.get(
        `/submissions/${encodeURIComponent(selectedId)}`,
        undefined,
        signal,
      ),
    Boolean(selectedId),
  );

  async function finalize() {
    if (!selectedId) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        `/submissions/${encodeURIComponent(selectedId)}:finalize`,
        { sourceRevision: detail.data?.revision },
        { idempotencyKey: newIdempotencyKey() },
      );
      setConfirmOpen(false);
      query.reload();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : "答案を確定できませんでした。",
      );
      setConfirmOpen(false);
    } finally {
      setWorking(false);
    }
  }

  if (query.status === "loading") {
    return <LoadingState label="確定できる答案を読み込んでいます" />;
  }
  if (query.status === "error") {
    return <ErrorState error={query.error} onRetry={query.reload} />;
  }
  if (!query.data?.items.length) {
    return (
      <Card>
        <EmptyState
          icon="check"
          title="確定待ちの答案はありません"
          description="生徒名と採点の確認が終わった答案が表示されます。"
        />
      </Card>
    );
  }

  return (
    <div className="finalize-grid">
      <Card>
        <div className="card__header">
          <div>
            <h2>確定できる答案</h2>
            <p>必要な確認がすべて終わっています。</p>
          </div>
          <Badge tone="success">{query.data.items.length}件</Badge>
        </div>
        <div className="finalize-list">
          {query.data.items.map((item) => (
            <button
              type="button"
              key={item.id}
              className={item.id === selectedId ? "is-selected" : ""}
              aria-pressed={item.id === selectedId}
              onClick={() => setSelectedId(item.id)}
            >
              <span className="student-initial" aria-hidden="true">
                {Array.from(item.studentDisplayName || "未")[0]}
              </span>
              <span>
                <strong>{item.studentDisplayName || "未割り当て"}</strong>
                <small>{item.fileName || "答案"}</small>
              </span>
              <Score
                compact
                earned={formatPoints(item.totalEarnedPointsMilli)}
                possible={formatPoints(item.totalPossiblePointsMilli)}
              />
              <Icon name="chevronRight" />
            </button>
          ))}
        </div>
      </Card>
      <Card className="finalize-check-card">
        {detail.status === "loading" ? (
          <LoadingState />
        ) : detail.status === "error" ? (
          <ErrorState error={detail.error} onRetry={detail.reload} compact />
        ) : detail.data ? (
          <>
            {error ? (
              <InlineAlert tone="danger">
                <p>{error}</p>
              </InlineAlert>
            ) : null}
            <div className="finalize-person">
              <div>
                <span>確定する答案</span>
                <h2>{detail.data.studentDisplayName || "未割り当て"}</h2>
                <p>
                  {detail.data.testTitle || detail.data.fileName}・
                  {formatDate(detail.data.testDate)}
                </p>
              </div>
              <Score
                earned={formatPoints(detail.data.totalEarnedPointsMilli)}
                possible={formatPoints(detail.data.totalPossiblePointsMilli)}
              />
            </div>
            <h3>確定前の確認</h3>
            {detail.data.finalizationChecks?.length ? (
              <ul className="finalize-checklist">
                {detail.data.finalizationChecks.map((check) => (
                  <li
                    className={check.passed ? "is-passed" : "is-blocked"}
                    key={check.key}
                  >
                    <span>
                      <Icon
                        name={check.passed ? "check" : "alert"}
                        size={18}
                      />
                    </span>
                    <div>
                      <strong>{check.label}</strong>
                      {check.detail ? <small>{check.detail}</small> : null}
                    </div>
                  </li>
                ))}
              </ul>
            ) : (
              <InlineAlert tone="success" title="確定できます">
                <p>
                  生徒割り当て、重複、必須確認、点数計算は確定時にサーバーで再検証されます。
                </p>
              </InlineAlert>
            )}
            <Button
              size="large"
              onClick={() => setConfirmOpen(true)}
              disabled={detail.data.finalizationChecks?.some(
                (check) => !check.passed,
              )}
            >
              この答案を確定
            </Button>
          </>
        ) : null}
      </Card>
      <Modal
        open={confirmOpen}
        onClose={() => !working && setConfirmOpen(false)}
        title="答案を確定しますか？"
        description="確定後は学習推移と帳票に反映されます。"
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setConfirmOpen(false)}
              disabled={working}
            >
              戻る
            </Button>
            <Button onClick={() => void finalize()} disabled={working}>
              {working ? "確定しています…" : "答案を確定"}
            </Button>
          </>
        }
      >
        <p>
          変更が必要になった場合は、理由を記録して答案を開き直すことができます。
        </p>
      </Modal>
    </div>
  );
}

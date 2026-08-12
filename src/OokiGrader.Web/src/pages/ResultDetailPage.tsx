import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "../router";
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
  Score,
  StatusBadge,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { api, asPaged, newIdempotencyKey } from "../lib/api";
import {
  formatDate,
  formatDateTime,
  formatPercentageBasisPoints,
  formatPoints,
} from "../lib/format";
import type {
  ExportStatus,
  PagedResponse,
  ResultDetail,
  ResultQuestion,
  RuntimeCapabilities,
  StudentSummary,
} from "../types";

export function ResultDetailPage() {
  const { submissionId = "" } = useParams();
  const navigate = useNavigate();
  const { hasAnyRole } = useSession();
  const canCorrect = hasAnyRole("administrator", "teacher");
  const capabilities = useApiQuery<RuntimeCapabilities>(
    "runtime-capabilities",
    (signal) => api.get("/capabilities", undefined, signal),
  );
  const result = useApiQuery<ResultDetail>(
    `result:${submissionId}`,
    (signal) =>
      api.get(
        `/results/${encodeURIComponent(submissionId)}`,
        undefined,
        signal,
      ),
    Boolean(submissionId),
  );
  const [exportOpen, setExportOpen] = useState(false);
  const [exportStatus, setExportStatus] = useState<ExportStatus>();
  const [exportWorking, setExportWorking] = useState(false);
  const [exportError, setExportError] = useState<string>();
  const [reopenOpen, setReopenOpen] = useState(false);
  const [reopenReason, setReopenReason] = useState("teacher_judgment");
  const [reopenNote, setReopenNote] = useState("");
  const [reopenWorking, setReopenWorking] = useState(false);
  const [reopenError, setReopenError] = useState<string>();
  const [assignmentOpen, setAssignmentOpen] = useState(false);
  const [assignmentSearch, setAssignmentSearch] = useState("");
  const [assignmentStudentId, setAssignmentStudentId] = useState("");
  const [assignmentReason, setAssignmentReason] =
    useState("mistaken_identity");
  const [assignmentNote, setAssignmentNote] = useState("");
  const [assignmentWorking, setAssignmentWorking] = useState(false);
  const [assignmentError, setAssignmentError] = useState<string>();
  const students = useApiQuery<PagedResponse<StudentSummary>>(
    `result-assignment-students:${assignmentSearch}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/students",
          {
            status: "active",
            search: assignmentSearch || undefined,
            pageSize: 200,
          },
          signal,
        ),
      ),
    assignmentOpen && canCorrect,
  );

  useEffect(() => {
    if (assignmentOpen) {
      setAssignmentStudentId(result.data?.student?.id || "");
      setAssignmentError(undefined);
    }
  }, [assignmentOpen, result.data?.student?.id]);

  useEffect(() => {
    if (
      !exportStatus ||
      ["verified", "failed"].includes(exportStatus.state)
    ) {
      return;
    }
    const timer = window.setInterval(async () => {
      try {
        const current = await api.get<ExportStatus>(
          `/exports/${encodeURIComponent(exportStatus.id)}`,
        );
        setExportStatus(current);
      } catch {
        // A transient polling failure does not discard the durable export job.
      }
    }, 2000);
    return () => window.clearInterval(timer);
  }, [exportStatus]);

  async function createExport() {
    setExportWorking(true);
    setExportError(undefined);
    try {
      const created = await api.post<ExportStatus>(
        `/results/${encodeURIComponent(submissionId)}/exports`,
        {
          includeScan: false,
          resultRevision: result.data?.resultRevision,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      setExportStatus(created);
    } catch (reason) {
      setExportError(
        reason instanceof Error ? reason.message : "PDFを作成できませんでした。",
      );
    } finally {
      setExportWorking(false);
    }
  }

  async function reopen(event: FormEvent) {
    event.preventDefault();
    setReopenWorking(true);
    setReopenError(undefined);
    try {
      await api.post(
        `/submissions/${encodeURIComponent(submissionId)}:reopen`,
        {
          reasonCode: reopenReason,
          note: reopenNote,
          sourceRevision: result.data?.resultRevision,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      setReopenOpen(false);
      navigate(`/submissions/${encodeURIComponent(submissionId)}/grading`);
    } catch (reason) {
      setReopenError(
        reason instanceof Error
          ? reason.message
          : "答案を開き直せませんでした。",
      );
    } finally {
      setReopenWorking(false);
    }
  }

  async function reassignStudent(event: FormEvent) {
    event.preventDefault();
    if (!assignmentStudentId) return;
    setAssignmentWorking(true);
    setAssignmentError(undefined);
    try {
      await api.post(
        `/submissions/${encodeURIComponent(submissionId)}:assignStudent`,
        {
          studentId: assignmentStudentId,
          sourceRevision: result.data?.resultRevision,
          reasonCode: assignmentReason,
          note: assignmentNote,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      setAssignmentOpen(false);
      result.reload();
    } catch (reason) {
      setAssignmentError(
        reason instanceof Error
          ? reason.message
          : "生徒の割り当てを変更できませんでした。",
      );
    } finally {
      setAssignmentWorking(false);
    }
  }

  if (result.status === "loading") {
    return (
      <div className="page">
        <LoadingState label="採点結果を読み込んでいます" />
      </div>
    );
  }
  if (result.status === "error" || !result.data) {
    return (
      <div className="page">
        <ErrorState error={result.error} onRetry={result.reload} />
      </div>
    );
  }

  const data = result.data;
  const student = data.student ?? {
    id: "",
    displayName: "生徒未特定",
    studentNumber: "",
  };
  return (
    <div className="page result-detail-page">
      <PageHeader
        eyebrow={`${formatDate(data.testDate)}・${data.testTitle}`}
        title={
          data.student
            ? `${student.displayName}さんの結果`
            : `${student.displayName}の結果`
        }
        description={`第${data.templateVersionNumber || "—"}版の採点基準・結果改訂 ${data.resultRevision}`}
        backAction={
          <Link className="back-link" to="/reports">
            <Icon name="arrowLeft" size={17} />
            帳票一覧へ
          </Link>
        }
        actions={
          <>
            {canCorrect ? (
              <>
                <Link
                  className="button button--secondary button--medium"
                  to={`/submissions/${encodeURIComponent(submissionId)}/grading`}
                >
                  <Icon name="file" size={18} />
                  <span>答案全体を見る</span>
                </Link>
                <Button
                  variant="secondary"
                  onClick={() => setAssignmentOpen(true)}
                >
                  生徒を変更
                </Button>
                <Button
                  variant="secondary"
                  leadingIcon="edit"
                  onClick={() => setReopenOpen(true)}
                >
                  採点を修正
                </Button>
              </>
            ) : null}
            <Button
              leadingIcon="download"
              onClick={() => setExportOpen(true)}
              disabled={
                !canCorrect ||
                capabilities.data?.reports.pdfExport !== true
              }
              title={
                !canCorrect
                  ? "PDFの作成には先生または管理者の権限が必要です"
                  : capabilities.data?.reports.pdfExport === false
                    ? "PDF帳票は管理者設定で無効です"
                    : undefined
              }
            >
              結果PDF
            </Button>
          </>
        }
      />

      {!data.scanAvailable ? (
        <InlineAlert tone="info" title="答案画像は削除されています">
          <p>
            保存期間または容量上限により、答案画像は
            {formatDate(data.scanDeletedAt)}
            に削除されました。採点結果は保持されています。
          </p>
        </InlineAlert>
      ) : null}

      {canCorrect && capabilities.status === "error" ? (
        <InlineAlert
          tone="warning"
          title="結果PDFの利用状況を確認できません"
          action={
            <Button
              type="button"
              variant="secondary"
              size="small"
              leadingIcon="retry"
              onClick={capabilities.reload}
            >
              再読み込み
            </Button>
          }
        >
          <p>採点結果は確認できます。PDFを作成する前に再読み込みしてください。</p>
        </InlineAlert>
      ) : null}

      <Card className="result-hero">
        <div className="result-hero__identity">
          <span>{student.displayName}</span>
          <h2>{data.testTitle}</h2>
          <p>
            {formatDate(data.testDate)}・生徒番号{" "}
            {student.studentNumber || "—"}
          </p>
        </div>
        <div className="result-hero__score">
          <span>得点</span>
          <Score
            earned={formatPoints(data.earnedPointsMilli)}
            possible={formatPoints(data.possiblePointsMilli)}
          />
          <strong>
            {formatPercentageBasisPoints(data.percentageBasisPoints)}
          </strong>
        </div>
        <div className="result-hero__status">
          <StatusBadge status={data.status} />
          <small>確定日時 {formatDateTime(data.finalizedAt)}</small>
        </div>
      </Card>

      <Card>
        <div className="card__header">
          <div>
            <h2>問題ごとの結果</h2>
            <p>答案画像がなくても、問題文と読み取り結果は保持されます。</p>
          </div>
          <Badge tone="neutral">{data.questions.length}問</Badge>
        </div>
        {data.questions.length ? (
          <div className="result-question-list">
            {data.questions.map((question) => (
              <ResultQuestionRow
                question={question}
                scanAvailable={data.scanAvailable}
                key={question.id}
              />
            ))}
          </div>
        ) : (
          <EmptyState
            icon="file"
            title="問題ごとの結果がありません"
            description="結果データを再確認してください。"
          />
        )}
      </Card>

      <Modal
        open={exportOpen}
        onClose={() => !exportWorking && setExportOpen(false)}
        title="結果PDFを作成"
        description="出力内容を確認してください。答案画像は含まれません。"
        size="medium"
        footer={
          exportStatus?.state === "verified" ? (
            <>
              <Button
                variant="secondary"
                onClick={() => setExportOpen(false)}
              >
                閉じる
              </Button>
              <a
                className="button button--primary button--medium"
                href={
                  exportStatus.fileUrl ||
                  `/api/v1/exports/${encodeURIComponent(exportStatus.id)}/file`
                }
                download
              >
                <Icon name="download" size={18} />
                <span>PDFをダウンロード</span>
              </a>
            </>
          ) : (
            <>
              <Button
                variant="secondary"
                onClick={() => setExportOpen(false)}
                disabled={exportWorking}
              >
                キャンセル
              </Button>
              <Button
                onClick={() => void createExport()}
                disabled={
                  exportWorking ||
                  Boolean(
                    exportStatus &&
                      !["failed", "verified"].includes(exportStatus.state),
                  )
                }
              >
                {exportWorking
                  ? "受け付けています…"
                  : exportStatus &&
                      !["failed", "verified"].includes(exportStatus.state)
                    ? "作成中"
                    : "PDFを作成"}
              </Button>
            </>
          )
        }
      >
        {exportError || exportStatus?.state === "failed" ? (
          <InlineAlert tone="danger" title="PDFを作成できませんでした">
            <p>{exportError || "管理者に処理状況を確認してください。"}</p>
          </InlineAlert>
        ) : exportStatus &&
          !["failed", "verified"].includes(exportStatus.state) ? (
          <div className="export-progress" role="status">
            <span className="spinner spinner--large" />
            <h3>結果PDFを作成しています</h3>
            <p>この画面を閉じても処理は続きます。</p>
            <StatusBadge status={exportStatus.state} />
          </div>
        ) : exportStatus?.state === "verified" ? (
          <div className="export-progress export-progress--done">
            <span>
              <Icon name="check" size={30} />
            </span>
            <h3>PDFを確認しました</h3>
            <p>日本語フォントを埋め込んだ結果PDFをダウンロードできます。</p>
          </div>
        ) : (
          <>
            <ul className="export-field-list">
              <li>
                <Icon name="check" size={17} />
                学校名・生徒名・テスト名・実施日
              </li>
              <li>
                <Icon name="check" size={17} />
                合計点・得点率・問題文・解答・配点
              </li>
              <li>
                <Icon name="check" size={17} />
                現在の訂正済み採点結果
              </li>
              <li className="is-excluded">
                <Icon name="close" size={17} />
                答案画像・内部の確信度・職員向けメモ
              </li>
            </ul>
            <InlineAlert tone="info">
              <p>作成後のPDFは、答案画像とは別の保存方針で管理されます。</p>
            </InlineAlert>
          </>
        )}
      </Modal>

      <Modal
        open={assignmentOpen}
        onClose={() => !assignmentWorking && setAssignmentOpen(false)}
        title="割り当てる生徒を変更"
        description="確定済みの得点は維持され、変更前後の生徒IDと理由が監査履歴に残ります。"
        size="small"
      >
        <form onSubmit={reassignStudent}>
          {assignmentError ? (
            <InlineAlert tone="danger">
              <p>{assignmentError}</p>
            </InlineAlert>
          ) : null}
          {students.status === "loading" ? (
            <LoadingState compact label="生徒名簿を読み込んでいます" />
          ) : students.status === "error" ? (
            <ErrorState error={students.error} onRetry={students.reload} compact />
          ) : null}
          <Field label="名簿を検索" htmlFor="assignment-search">
            <input
              id="assignment-search"
              type="search"
              value={assignmentSearch}
              onChange={(event) => setAssignmentSearch(event.target.value)}
              placeholder="氏名・生徒番号"
            />
          </Field>
          <Field label="生徒" htmlFor="assignment-student" required>
            <select
              id="assignment-student"
              value={assignmentStudentId}
              onChange={(event) => setAssignmentStudentId(event.target.value)}
              disabled={
                students.status === "loading" || students.status === "error"
              }
              required
            >
              <option value="">選択してください</option>
              {result.data.student &&
              !students.data?.items.some(
                (item) => item.id === result.data?.student?.id,
              ) ? (
                <option value={result.data.student.id}>
                  {result.data.student.displayName}（
                  {result.data.student.studentNumber}）
                </option>
              ) : null}
              {students.data?.items.map((item) => (
                <option value={item.id} key={item.id}>
                  {item.displayName}（{item.studentNumber}）
                </option>
              ))}
            </select>
          </Field>
          <Field label="変更理由" htmlFor="assignment-reason" required>
            <select
              id="assignment-reason"
              value={assignmentReason}
              onChange={(event) => setAssignmentReason(event.target.value)}
            >
              <option value="mistaken_identity">生徒の取り違え</option>
              <option value="roster_correction">名簿情報の確認</option>
              <option value="teacher_correction">先生による訂正</option>
              <option value="other">その他</option>
            </select>
          </Field>
          <Field
            label="メモ"
            htmlFor="assignment-note"
            required={assignmentReason === "other"}
          >
            <textarea
              id="assignment-note"
              rows={3}
              value={assignmentNote}
              onChange={(event) => setAssignmentNote(event.target.value)}
              required={assignmentReason === "other"}
            />
          </Field>
          <div className="form-actions">
            <Button
              type="button"
              variant="secondary"
              onClick={() => setAssignmentOpen(false)}
              disabled={assignmentWorking}
            >
              キャンセル
            </Button>
            <Button
              type="submit"
              disabled={
                assignmentWorking ||
                students.status === "loading" ||
                students.status === "error" ||
                !assignmentStudentId ||
                (assignmentReason === "other" && !assignmentNote.trim())
              }
            >
              {assignmentWorking ? "変更しています…" : "割り当てを変更"}
            </Button>
          </div>
        </form>
      </Modal>

      <Modal
        open={reopenOpen}
        onClose={() => !reopenWorking && setReopenOpen(false)}
        title="確定済みの答案を開き直す"
        description="変更理由は監査履歴に記録され、以前の結果も残ります。"
        size="small"
      >
        <form onSubmit={reopen}>
          {reopenError ? (
            <InlineAlert tone="danger">
              <p>{reopenError}</p>
            </InlineAlert>
          ) : null}
          <Field label="理由" htmlFor="reopen-reason" required>
            <select
              id="reopen-reason"
              value={reopenReason}
              onChange={(event) => setReopenReason(event.target.value)}
            >
              <option value="accepted_equivalent">別表記の見直し</option>
              <option value="transcription_corrected">読み取りの修正</option>
              <option value="rubric_corrected">採点基準の確認</option>
              <option value="teacher_judgment">先生の判断</option>
              <option value="other">その他</option>
            </select>
          </Field>
          <Field
            label="メモ"
            htmlFor="reopen-note"
            required={reopenReason === "other"}
          >
            <textarea
              id="reopen-note"
              rows={3}
              value={reopenNote}
              onChange={(event) => setReopenNote(event.target.value)}
              required={reopenReason === "other"}
            />
          </Field>
          <div className="form-actions">
            <Button
              type="button"
              variant="secondary"
              onClick={() => setReopenOpen(false)}
            >
              キャンセル
            </Button>
            <Button
              type="submit"
              disabled={
                reopenWorking ||
                (reopenReason === "other" && !reopenNote.trim())
              }
            >
              {reopenWorking ? "開き直しています…" : "開き直して採点へ"}
            </Button>
          </div>
        </form>
      </Modal>
    </div>
  );
}

function ResultQuestionRow({
  question,
  scanAvailable,
}: {
  question: ResultQuestion;
  scanAvailable: boolean;
}) {
  return (
    <article className="result-question">
      <header>
        <span className="result-question__label">{question.displayLabel}</span>
        <div>
          <h3>{question.questionText}</h3>
          {question.expectedAnswer ? (
            <p>
              正解: <strong>{question.expectedAnswer}</strong>
            </p>
          ) : null}
        </div>
        <div className="result-question__score">
          <StatusBadge status={question.outcome} />
          <Score
            compact
            earned={formatPoints(question.awardedPointsMilli)}
            possible={formatPoints(question.maxPointsMilli)}
          />
        </div>
      </header>
      <div className="result-question__answer">
        <div>
          <span>読み取った解答</span>
          <strong>{question.transcription || "（無解答）"}</strong>
          {question.overridden ? <Badge tone="accent">先生が訂正</Badge> : null}
          {question.kanjiRuleOutcome ? (
            <Badge tone="warning">漢字ルール適用</Badge>
          ) : null}
        </div>
        {scanAvailable && question.cropAvailable && question.cropUrl ? (
          <img src={question.cropUrl} alt={`${question.displayLabel}の答案画像`} />
        ) : (
          <span className="crop-unavailable">
            <Icon name="file" size={17} />
            画像なし
          </span>
        )}
      </div>
      {question.reason ? (
        <p className="result-question__reason">{question.reason}</p>
      ) : null}
    </article>
  );
}

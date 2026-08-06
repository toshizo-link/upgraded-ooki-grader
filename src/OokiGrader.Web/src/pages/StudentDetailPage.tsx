import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useParams, useSearchParams } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon } from "../components/Icon";
import { ProgressChart } from "../components/ProgressChart";
import {
  StudentForm,
  type StudentFormValues,
} from "../components/StudentForm";
import {
  Avatar,
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
  Tabs,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { ApiError, api, asPaged, newIdempotencyKey } from "../lib/api";
import {
  formatDate,
  formatDateTime,
  formatPercentageBasisPoints,
  formatPoints,
  toDateInput,
} from "../lib/format";
import type {
  PagedResponse,
  ProgressPoint,
  StudentAlias,
  StudentDetail,
  StudentProgress,
} from "../types";

type DetailTab = "profile" | "aliases" | "progress" | "results" | "history";

interface StudentResultSummary extends ProgressPoint {
  status?: string;
}

interface AuditEvent {
  id: string;
  action: string;
  localDisplayTime?: string;
  timestamp?: string;
  actorDisplayName?: string;
  summary?: string;
}

function defaultProgressFrom() {
  const today = new Date(`${toDateInput()}T00:00:00+09:00`);
  const day = today.getDate();
  today.setDate(1);
  today.setMonth(today.getMonth() - 3);
  const lastDay = new Date(
    today.getFullYear(),
    today.getMonth() + 1,
    0,
  ).getDate();
  today.setDate(Math.min(day, lastDay));
  return toDateInput(today);
}

export function StudentDetailPage() {
  const { studentId = "" } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const { hasAnyRole } = useSession();
  const canEdit = hasAnyRole("administrator", "teacher");
  const canViewProgress = hasAnyRole(
    "administrator",
    "teacher",
    "readOnlyReviewer",
  );
  const canViewHistory = hasAnyRole("administrator");
  const requestedTab = searchParams.get("tab") as DetailTab | null;
  const allowedTabs: DetailTab[] = [
    "profile",
    "aliases",
    ...(canViewProgress ? (["progress", "results"] as DetailTab[]) : []),
    ...(canViewHistory ? (["history"] as DetailTab[]) : []),
  ];
  const tab: DetailTab =
    requestedTab && allowedTabs.includes(requestedTab)
      ? requestedTab
      : "profile";
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string>();
  const [deactivateOpen, setDeactivateOpen] = useState(false);
  const [actionError, setActionError] = useState<string>();

  const detail = useApiQuery<StudentDetail>(
    `student:${studentId}`,
    (signal) =>
      api.get(`/students/${encodeURIComponent(studentId)}`, undefined, signal),
    Boolean(studentId),
  );

  function selectTab(nextTab: DetailTab) {
    const next = new URLSearchParams(searchParams);
    next.set("tab", nextTab);
    setSearchParams(next, { replace: true });
  }

  async function updateStudent(values: StudentFormValues) {
    setSaving(true);
    setSaveError(undefined);
    try {
      await api.patch<StudentDetail>(
        `/students/${encodeURIComponent(studentId)}`,
        values,
        {
          etag: detail.data?.revision
            ? `"rev-${detail.data.revision}"`
            : undefined,
        },
      );
      setEditing(false);
      detail.reload();
    } catch (reason) {
      if (reason instanceof ApiError && reason.status === 412) {
        setSaveError(
          "別の職員がこの生徒を更新しました。再読み込みして内容を確認してください。",
        );
      } else {
        setSaveError(
          reason instanceof Error
            ? reason.message
            : "変更を保存できませんでした。",
        );
      }
    } finally {
      setSaving(false);
    }
  }

  async function toggleActive() {
    if (!detail.data) return;
    setSaving(true);
    setActionError(undefined);
    const isInactive =
      detail.data.active === false ||
      detail.data.enrollmentStatus === "inactive";
    try {
      await api.post<void>(
        `/students/${encodeURIComponent(studentId)}:${isInactive ? "reactivate" : "deactivate"}`,
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      setDeactivateOpen(false);
      detail.reload();
    } catch (reason) {
      setActionError(
        reason instanceof Error
          ? reason.message
          : "在籍状態を変更できませんでした。",
      );
    } finally {
      setSaving(false);
    }
  }

  if (detail.status === "loading") {
    return (
      <div className="page">
        <LoadingState label="生徒情報を読み込んでいます" />
      </div>
    );
  }

  if (detail.status === "error" || !detail.data) {
    return (
      <div className="page">
        <PageHeader title="生徒情報" />
        <ErrorState error={detail.error} onRetry={detail.reload} />
      </div>
    );
  }

  const student = detail.data;
  const isInactive =
    student.active === false || student.enrollmentStatus === "inactive";
  const tabs: Array<{ value: DetailTab; label: string }> = [
    { value: "profile", label: "基本情報" },
    { value: "aliases", label: "別名・表記" },
    ...(canViewProgress
      ? [
          { value: "progress" as const, label: "学習推移" },
          { value: "results" as const, label: "テスト結果" },
        ]
      : []),
    ...(canViewHistory
      ? [{ value: "history" as const, label: "変更履歴" }]
      : []),
  ];

  return (
    <div className="page">
      <PageHeader
        eyebrow={`生徒番号 ${student.studentNumber}`}
        title={student.displayName}
        description={
          [
            [student.familyNameKana, student.givenNameKana]
              .filter(Boolean)
              .join(" "),
            student.gradeLabel,
            student.classLabel,
            student.course,
          ]
            .filter(Boolean)
            .join("・") || "所属情報は未設定です"
        }
        backAction={
          <Link className="back-link" to="/students">
            <Icon name="arrowLeft" size={17} />
            生徒一覧へ
          </Link>
        }
        actions={
          <div className="student-header-actions">
            <StatusBadge status={isInactive ? "retired" : "active"} />
            {canEdit && tab === "profile" ? (
              <Button
                variant="secondary"
                leadingIcon="edit"
                onClick={() => setEditing(true)}
              >
                編集
              </Button>
            ) : null}
          </div>
        }
      />

      <Tabs
        value={tab}
        onChange={selectTab}
        tabs={tabs}
        label={`${student.displayName}の情報`}
      />

      {tab === "profile" ? (
        <ProfileTab
          student={student}
          canEdit={canEdit}
          onEdit={() => setEditing(true)}
          onToggleActive={() => {
            setActionError(undefined);
            setDeactivateOpen(true);
          }}
        />
      ) : null}
      {tab === "aliases" ? (
        <AliasesTab
          studentId={studentId}
          canEdit={canEdit}
          initialAliases={student.aliases}
        />
      ) : null}
      {tab === "progress" && canViewProgress ? (
        <ProgressTab studentId={studentId} />
      ) : null}
      {tab === "results" && canViewProgress ? (
        <ResultsTab studentId={studentId} />
      ) : null}
      {tab === "history" && canViewHistory ? (
        <HistoryTab studentId={studentId} />
      ) : null}

      <Modal
        open={editing}
        onClose={() => !saving && setEditing(false)}
        title="基本情報を編集"
        description="氏名や生徒番号の変更は、以後の答案照合に反映されます。"
        size="large"
      >
        <StudentForm
          initial={student}
          onSubmit={updateStudent}
          onCancel={() => setEditing(false)}
          submitting={saving}
          error={saveError}
        />
      </Modal>

      <Modal
        open={deactivateOpen}
        onClose={() => !saving && setDeactivateOpen(false)}
        title={isInactive ? "在籍中に戻しますか？" : "在籍終了にしますか？"}
        description={
          isInactive
            ? "この生徒を新しい答案の照合候補に戻します。"
            : "過去の採点結果は残りますが、新しい答案の自動照合候補から外れます。"
        }
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setDeactivateOpen(false)}
              disabled={saving}
            >
              キャンセル
            </Button>
            <Button
              variant={isInactive ? "primary" : "danger"}
              onClick={() => void toggleActive()}
              disabled={saving}
            >
              {saving
                ? "変更しています…"
                : isInactive
                  ? "在籍中に戻す"
                  : "在籍終了にする"}
            </Button>
          </>
        }
      >
        {actionError ? (
          <InlineAlert tone="danger">
            <p>{actionError}</p>
          </InlineAlert>
        ) : (
          <p>
            対象: <strong>{student.displayName}</strong>（
            {student.studentNumber}）
          </p>
        )}
      </Modal>
    </div>
  );
}

function ProfileTab({
  student,
  canEdit,
  onEdit,
  onToggleActive,
}: {
  student: StudentDetail;
  canEdit: boolean;
  onEdit: () => void;
  onToggleActive: () => void;
}) {
  const isInactive =
    student.active === false || student.enrollmentStatus === "inactive";
  return (
    <div className="detail-grid">
      <Card className="profile-card">
        <div className="profile-card__identity">
          <Avatar name={student.displayName} size="large" />
          <div>
            <h2>{student.displayName}</h2>
            <p>
              {[student.familyNameKana, student.givenNameKana]
                .filter(Boolean)
                .join(" ") || "カナ未設定"}
            </p>
          </div>
        </div>
        <dl className="definition-grid">
          <div>
            <dt>生徒番号</dt>
            <dd>{student.studentNumber}</dd>
          </div>
          <div>
            <dt>在籍状態</dt>
            <dd>{isInactive ? "在籍終了" : "在籍中"}</dd>
          </div>
          <div>
            <dt>学年</dt>
            <dd>{student.gradeLabel || "—"}</dd>
          </div>
          <div>
            <dt>クラス</dt>
            <dd>{student.classLabel || "—"}</dd>
          </div>
          <div>
            <dt>コース</dt>
            <dd>{student.course || "—"}</dd>
          </div>
          <div>
            <dt>更新日時</dt>
            <dd>{formatDateTime(student.updatedAt)}</dd>
          </div>
        </dl>
        {student.notes ? (
          <div className="teacher-note">
            <span>
              <Icon name="lock" size={16} />
              職員向けメモ
            </span>
            <p>{student.notes}</p>
          </div>
        ) : null}
      </Card>
      <aside>
        <Card className="action-card">
          <h2>生徒情報の管理</h2>
          <p>照合に使う情報や在籍状態を変更できます。</p>
          {canEdit ? (
            <>
              <Button variant="secondary" leadingIcon="edit" onClick={onEdit}>
                基本情報を編集
              </Button>
              <button
                type="button"
                className="text-danger-button"
                onClick={onToggleActive}
              >
                {isInactive ? "在籍中に戻す" : "在籍終了にする"}
              </button>
            </>
          ) : (
            <InlineAlert tone="info">
              <p>このアカウントは閲覧のみ可能です。</p>
            </InlineAlert>
          )}
        </Card>
      </aside>
    </div>
  );
}

function AliasesTab({
  studentId,
  canEdit,
  initialAliases,
}: {
  studentId: string;
  canEdit: boolean;
  initialAliases?: StudentAlias[];
}) {
  const aliases = useApiQuery<PagedResponse<StudentAlias>>(
    `student-aliases:${studentId}`,
    async (signal) =>
      asPaged(
        await api.get(
          `/students/${encodeURIComponent(studentId)}/aliases`,
          undefined,
          signal,
        ),
      ),
  );
  const [value, setValue] = useState("");
  const [aliasType, setAliasType] = useState("handwritten");
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();
  const items = aliases.data?.items || initialAliases || [];

  async function addAlias(event: FormEvent) {
    event.preventDefault();
    if (!value.trim()) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        `/students/${encodeURIComponent(studentId)}/aliases`,
        { text: value.trim(), aliasType },
        { idempotencyKey: newIdempotencyKey() },
      );
      setValue("");
      aliases.reload();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : "別名を追加できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function removeAlias(alias: StudentAlias) {
    if (!window.confirm(`「${alias.text}」を別名から削除しますか？`)) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.delete(
        `/students/${encodeURIComponent(studentId)}/aliases/${encodeURIComponent(alias.id)}`,
      );
      aliases.reload();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : "別名を削除できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  return (
    <div className="detail-grid">
      <Card>
        <div className="card__header">
          <div>
            <h2>登録済みの別名・表記</h2>
            <p>旧姓、空白の違い、ローマ字、手書きで多い表記を登録します。</p>
          </div>
        </div>
        {error ? (
          <InlineAlert tone="danger">
            <p>{error}</p>
          </InlineAlert>
        ) : null}
        {aliases.status === "loading" && !initialAliases ? (
          <LoadingState compact />
        ) : aliases.status === "error" && !initialAliases ? (
          <ErrorState error={aliases.error} onRetry={aliases.reload} compact />
        ) : items.length ? (
          <ul className="alias-list">
            {items.map((alias) => (
              <li key={alias.id}>
                <span className="alias-glyph" aria-hidden="true">
                  表
                </span>
                <div>
                  <strong>{alias.text}</strong>
                  <small>{aliasTypeLabel(alias.aliasType)}</small>
                </div>
                {canEdit ? (
                  <button
                    type="button"
                    aria-label={`${alias.text}を削除`}
                    onClick={() => void removeAlias(alias)}
                    disabled={working}
                  >
                    <Icon name="trash" size={17} />
                  </button>
                ) : null}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState
            icon="kanji"
            title="別名は登録されていません"
            description="通常の氏名とカナは、別名がなくても照合に使われます。"
          />
        )}
      </Card>
      {canEdit ? (
        <Card className="action-card">
          <h2>別名を追加</h2>
          <form onSubmit={addAlias}>
            <Field label="表記" htmlFor="alias-text" required>
              <input
                id="alias-text"
                value={value}
                onChange={(event) => setValue(event.target.value)}
                placeholder="例：大木花子"
                required
              />
            </Field>
            <Field label="種類" htmlFor="alias-type">
              <select
                id="alias-type"
                value={aliasType}
                onChange={(event) => setAliasType(event.target.value)}
              >
                <option value="handwritten">手書きで多い表記</option>
                <option value="oldSurname">旧姓</option>
                <option value="spacing">空白の違い</option>
                <option value="romanization">ローマ字</option>
                <option value="other">その他</option>
              </select>
            </Field>
            <Button type="submit" disabled={working || !value.trim()}>
              {working ? "追加しています…" : "別名を追加"}
            </Button>
          </form>
        </Card>
      ) : null}
    </div>
  );
}

function aliasTypeLabel(type?: string) {
  const labels: Record<string, string> = {
    handwritten: "手書きで多い表記",
    oldSurname: "旧姓",
    spacing: "空白の違い",
    romanization: "ローマ字",
    other: "その他",
  };
  return labels[type || ""] || "別名";
}

function ProgressTab({ studentId }: { studentId: string }) {
  const [from, setFrom] = useState(defaultProgressFrom);
  const [to, setTo] = useState(toDateInput);
  const [subject, setSubject] = useState("");
  const progress = useApiQuery<StudentProgress>(
    `progress:${studentId}:${from}:${to}:${subject}`,
    (signal) =>
      api.get(
        `/students/${encodeURIComponent(studentId)}/progress`,
        { from, to, subject: subject || undefined },
        signal,
      ),
  );

  function applyPreset(months: number | "all") {
    if (months === "all") setFrom("2000-01-01");
    else {
      const date = new Date(`${to}T00:00:00+09:00`);
      date.setMonth(date.getMonth() - months);
      setFrom(toDateInput(date));
    }
  }

  return (
    <div className="stack">
      <Card>
        <div className="progress-controls">
          <div className="date-range">
            <Field label="開始日" htmlFor="progress-from">
              <input
                id="progress-from"
                type="date"
                value={from}
                max={to}
                onChange={(event) => setFrom(event.target.value)}
              />
            </Field>
            <span aria-hidden="true">〜</span>
            <Field label="終了日" htmlFor="progress-to">
              <input
                id="progress-to"
                type="date"
                value={to}
                min={from}
                onChange={(event) => setTo(event.target.value)}
              />
            </Field>
          </div>
          <div className="preset-buttons" aria-label="期間のプリセット">
            <button type="button" onClick={() => applyPreset(1)}>
              1か月
            </button>
            <button type="button" onClick={() => applyPreset(3)}>
              3か月
            </button>
            <button type="button" onClick={() => applyPreset(6)}>
              6か月
            </button>
            <button type="button" onClick={() => applyPreset("all")}>
              すべて
            </button>
          </div>
          <label className="compact-select">
            <span>教科</span>
            <select
              value={subject}
              onChange={(event) => setSubject(event.target.value)}
            >
              <option value="">すべて</option>
              <option value="国語">国語</option>
              <option value="数学">数学</option>
              <option value="英語">英語</option>
              <option value="理科">理科</option>
              <option value="社会">社会</option>
            </select>
          </label>
        </div>
      </Card>
      <Card>
        <div className="card__header">
          <div>
            <h2>得点率の推移</h2>
            <p>
              テストごとの難易度は異なります。このグラフは学習効果を断定するものではありません。
            </p>
          </div>
          {progress.data ? (
            <Badge tone="neutral">{progress.data.series.length}件</Badge>
          ) : null}
        </div>
        {progress.status === "loading" ? (
          <LoadingState label="学習推移を集計しています" />
        ) : progress.status === "error" ? (
          <ErrorState error={progress.error} onRetry={progress.reload} />
        ) : progress.data?.series.length ? (
          <>
            {progress.data.series.length === 1 ? (
              <InlineAlert tone="info">
                <p>期間内のテストは1件です。傾向線は表示していません。</p>
              </InlineAlert>
            ) : null}
            <ProgressChart series={progress.data.series} />
            <ProgressTable series={progress.data.series} />
          </>
        ) : (
          <EmptyState
            icon="chart"
            title="この期間の確定済みテストはありません"
            description="期間や教科を変更するか、答案の確定後にもう一度確認してください。"
          />
        )}
      </Card>
    </div>
  );
}

function ProgressTable({ series }: { series: ProgressPoint[] }) {
  return (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            <th>実施日</th>
            <th>テスト</th>
            <th>得点</th>
            <th>得点率</th>
            <th>正解</th>
            <th>一部正解</th>
            <th>不正解</th>
            <th>無解答</th>
          </tr>
        </thead>
        <tbody>
          {series.map((point) => (
            <tr key={`${point.submissionId}-${point.resultRevision}`}>
              <td>{formatDate(point.testDate)}</td>
              <td>
                <Link to={`/results/${encodeURIComponent(point.submissionId)}`}>
                  {point.testTitle}
                </Link>
              </td>
              <td>
                <Score
                  compact
                  earned={formatPoints(point.earnedPointsMilli)}
                  possible={formatPoints(point.possiblePointsMilli)}
                />
              </td>
              <td>
                {formatPercentageBasisPoints(point.percentageBasisPoints)}
              </td>
              <td>{point.correct}</td>
              <td>{point.partial}</td>
              <td>{point.incorrect}</td>
              <td>{point.blank}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ResultsTab({ studentId }: { studentId: string }) {
  const results = useApiQuery<PagedResponse<StudentResultSummary>>(
    `student-results:${studentId}`,
    async (signal) =>
      asPaged(
        await api.get(
          `/students/${encodeURIComponent(studentId)}/results`,
          { pageSize: 100 },
          signal,
        ),
      ),
  );
  return (
    <Card>
      <div className="card__header">
        <div>
          <h2>確定済みのテスト結果</h2>
          <p>新しい実施日から順に表示しています。</p>
        </div>
      </div>
      {results.status === "loading" ? (
        <LoadingState />
      ) : results.status === "error" ? (
        <ErrorState error={results.error} onRetry={results.reload} />
      ) : results.data?.items.length ? (
        <ProgressTable series={results.data.items} />
      ) : (
        <EmptyState
          icon="reports"
          title="確定済みの結果はありません"
          description="答案を確定すると、ここから詳細を開けます。"
        />
      )}
    </Card>
  );
}

function HistoryTab({ studentId }: { studentId: string }) {
  const history = useApiQuery<PagedResponse<AuditEvent>>(
    `student-history:${studentId}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/admin/audit-events",
          { objectType: "student", objectId: studentId, pageSize: 100 },
          signal,
        ),
      ),
  );
  return (
    <Card>
      <div className="card__header">
        <div>
          <h2>変更履歴</h2>
          <p>この生徒に関する監査記録です。</p>
        </div>
      </div>
      {history.status === "loading" ? (
        <LoadingState />
      ) : history.status === "error" ? (
        <ErrorState error={history.error} onRetry={history.reload} />
      ) : history.data?.items.length ? (
        <ol className="timeline">
          {history.data.items.map((event) => (
            <li key={event.id}>
              <span className="timeline__marker" />
              <div>
                <strong>{auditActionLabel(event.action)}</strong>
                <p>{event.summary || "変更が記録されました。"}</p>
                <small>
                  {formatDateTime(
                    event.localDisplayTime || event.timestamp,
                  )}{" "}
                  {event.actorDisplayName
                    ? `・${event.actorDisplayName}`
                    : ""}
                </small>
              </div>
            </li>
          ))}
        </ol>
      ) : (
        <EmptyState
          icon="clock"
          title="表示できる変更履歴はありません"
          description="変更が記録されると、ここに表示されます。"
        />
      )}
    </Card>
  );
}

function auditActionLabel(action: string) {
  const labels: Record<string, string> = {
    "student.created": "生徒を登録",
    "student.updated": "基本情報を更新",
    "student.deactivated": "在籍終了に変更",
    "student.reactivated": "在籍中に変更",
    "student.alias_created": "別名を追加",
    "student.alias_deleted": "別名を削除",
  };
  return labels[action] || "生徒情報を変更";
}

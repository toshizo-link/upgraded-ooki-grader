import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate } from "../router";
import { useSession } from "../auth/SessionContext";
import { Icon } from "../components/Icon";
import {
  ActiveFilterSummary,
  facetSuggestions,
  FilterTextInput,
  ListPagination,
  ListSortControls,
} from "../components/ListControls";
import {
  Button,
  Card,
  EmptyState,
  ErrorState,
  Field,
  InlineAlert,
  Modal,
  PageHeader,
  SearchInput,
  SkeletonRows,
  StatusBadge,
} from "../components/ui";
import {
  StudentForm,
  type StudentFormValues,
} from "../components/StudentForm";
import { useApiQuery } from "../hooks/useApiQuery";
import { useListQueryState } from "../hooks/useListQueryState";
import { ApiError, api, asPaged, newIdempotencyKey } from "../lib/api";
import { formatDate, formatDateTime } from "../lib/format";
import type { PagedResponse, StudentSummary } from "../types";

const STUDENT_SORTS = [
  { value: "studentNumber", label: "生徒番号", defaultDirection: "asc" },
  { value: "name", label: "氏名", defaultDirection: "asc" },
  { value: "updatedAt", label: "更新日時", defaultDirection: "desc" },
] as const;

const STUDENT_QUERY_OPTIONS = {
  allowedSorts: [
    "studentNumber",
    "-studentNumber",
    "name",
    "-name",
    "updatedAt",
    "-updatedAt",
  ],
  defaultSort: "studentNumber",
  enumParams: { status: ["active", "inactive", "all"] },
  textParams: ["class", "course", "grade"],
  defaultPageSize: 50,
} as const;

const STUDENT_FILTER_KEYS = ["q", "status", "class", "course", "grade"] as const;

export function StudentsPage() {
  const list = useListQueryState(STUDENT_QUERY_OPTIONS);
  const { searchParams } = list;
  const navigate = useNavigate();
  const { hasAnyRole } = useSession();
  const canEdit = hasAnyRole("administrator", "teacher");
  const [createOpen, setCreateOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string>();
  const activeFilter = searchParams.get("status") || "active";
  const classFilter = searchParams.get("class") || "";
  const courseFilter = searchParams.get("course") || "";
  const gradeFilter = searchParams.get("grade") || "";

  const queryKey = searchParams.toString();
  const students = useApiQuery<PagedResponse<StudentSummary>>(
    `students:${queryKey}`,
    async (signal) =>
      asPaged(
        await api.get(
          "/students",
          {
            search: searchParams.get("q"),
            status: activeFilter === "all" ? undefined : activeFilter,
            class: classFilter || undefined,
            course: courseFilter || undefined,
            grade: gradeFilter || undefined,
            sort: list.sort,
            cursor: list.cursor,
            pageSize: list.pageSize,
            includeFacets: true,
          },
          signal,
        ),
      ),
  );

  const classes = facetSuggestions(
    students.data?.facets,
    "classes",
    (students.data?.items || []).map((student) => student.classLabel),
  );
  const courses = facetSuggestions(
    students.data?.facets,
    "courses",
    (students.data?.items || []).map((student) => student.course),
  );
  const grades = facetSuggestions(
    students.data?.facets,
    "grades",
    (students.data?.items || []).map((student) => student.gradeLabel),
  );

  async function createStudent(values: StudentFormValues) {
    setCreating(true);
    setCreateError(undefined);
    try {
      const created = await api.post<StudentSummary>("/students", values, {
        idempotencyKey: newIdempotencyKey(),
      });
      setCreateOpen(false);
      students.reload();
      navigate(`/students/${encodeURIComponent(created.id)}`);
    } catch (reason) {
      setCreateError(
        reason instanceof ApiError
          ? reason.problem.errors?.[0]?.message || reason.message
          : "生徒を保存できませんでした。",
      );
    } finally {
      setCreating(false);
    }
  }

  const activeFilters = [
    searchParams.get("q")
      ? { key: "q", label: "検索", value: `「${searchParams.get("q")}」` }
      : undefined,
    activeFilter !== "all"
      ? {
          key: "status",
          label: "在籍",
          value: activeFilter === "inactive" ? "在籍終了" : "在籍中",
        }
      : undefined,
    classFilter ? { key: "class", label: "クラス", value: classFilter } : undefined,
    courseFilter ? { key: "course", label: "コース", value: courseFilter } : undefined,
    gradeFilter ? { key: "grade", label: "学年", value: gradeFilter } : undefined,
  ].filter((value): value is { key: string; label: string; value: string } => Boolean(value));

  return (
    <div className="page">
      <PageHeader
        eyebrow="生徒台帳"
        title="生徒"
        description="氏名・カナ・生徒番号・別名から検索できます。"
        actions={
          canEdit ? (
            <>
              <Button
                variant="secondary"
                leadingIcon="upload"
                onClick={() => setImportOpen(true)}
              >
                CSVから取り込む
              </Button>
              <Button
                leadingIcon="plus"
                onClick={() => {
                  setCreateError(undefined);
                  setCreateOpen(true);
                }}
              >
                生徒を追加
              </Button>
            </>
          ) : undefined
        }
      />

      <Card>
        <div className="list-toolbar">
          <SearchInput
            value={list.search}
            onChange={list.setSearch}
            placeholder="生徒番号・氏名・カナ・別名で検索"
            label="生徒を検索"
          />
          <ListSortControls
            value={list.sort}
            options={STUDENT_SORTS}
            defaultValue="studentNumber"
            onChange={(value) => list.updateParam("sort", value)}
          />
          {students.data ? (
            <span className="result-count">
              約{students.data.totalApproximate ?? students.data.items.length}名
            </span>
          ) : null}
        </div>

        <div className="list-filter-panel" aria-label="生徒の絞り込み">
          <label className="filter-field">
            <span>在籍状態</span>
            <select
              value={activeFilter}
              onChange={(event) => list.updateParam("status", event.target.value)}
            >
              <option value="active">在籍中</option>
              <option value="inactive">在籍終了</option>
              <option value="all">すべて</option>
            </select>
          </label>
          <FilterTextInput
            label="クラス"
            value={classFilter}
            suggestions={classes}
            onCommit={(value) => list.updateParam("class", value)}
          />
          <FilterTextInput
            label="コース"
            value={courseFilter}
            suggestions={courses}
            onCommit={(value) => list.updateParam("course", value)}
          />
          <FilterTextInput
            label="学年"
            value={gradeFilter}
            suggestions={grades}
            onCommit={(value) => list.updateParam("grade", value)}
          />
        </div>
        <ActiveFilterSummary
          filters={activeFilters}
          onClear={() => list.clearFilters(STUDENT_FILTER_KEYS, { status: "all" })}
        />

        {students.status === "loading" ? (
          <SkeletonRows rows={7} />
        ) : students.status === "error" ? (
          <ErrorState error={students.error} onRetry={students.reload} />
        ) : students.data?.items.length ? (
          <div className="table-scroll">
            <table className="student-table">
              <thead>
                <tr>
                  <th>生徒番号</th>
                  <th>氏名</th>
                  <th>カナ</th>
                  <th>学年・クラス</th>
                  <th>コース</th>
                  <th>在籍</th>
                  <th>最終テスト</th>
                  <th>
                    <span className="sr-only">詳細</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {students.data.items.map((student) => {
                  const active =
                    student.active ??
                    student.enrollmentStatus !== "inactive";
                  return (
                    <tr key={student.id}>
                      <td className="tabular">{student.studentNumber}</td>
                      <td>
                        <Link
                          className="table-name-link"
                          to={`/students/${encodeURIComponent(student.id)}`}
                        >
                          <span className="student-initial" aria-hidden="true">
                            {Array.from(student.displayName)[0] || "生"}
                          </span>
                          <strong>{student.displayName}</strong>
                        </Link>
                      </td>
                      <td>
                        {student.kana ||
                          [student.familyNameKana, student.givenNameKana]
                            .filter(Boolean)
                            .join(" ") ||
                          "—"}
                      </td>
                      <td>
                        {[student.gradeLabel, student.classLabel]
                          .filter(Boolean)
                          .join("・") || "—"}
                      </td>
                      <td>{student.course || "—"}</td>
                      <td>
                        <StatusBadge status={active ? "active" : "retired"} />
                      </td>
                      <td>{formatDate(student.lastFinalizedTestDate)}</td>
                      <td className="table-action">
                        <Link
                          aria-label={`${student.displayName}の詳細を開く`}
                          to={`/students/${encodeURIComponent(student.id)}`}
                        >
                          <Icon name="chevronRight" size={18} />
                        </Link>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        ) : (
          <EmptyState
            icon="students"
            title={
              activeFilters.length
                ? "条件に一致する生徒がいません"
                : "生徒がまだ登録されていません"
            }
            description={
              activeFilters.length
                ? "検索語や在籍状態、クラスなどの条件を変更してください。"
                : "個別に追加するか、CSVファイルからまとめて取り込めます。"
            }
          />
        )}
        <ListPagination
          page={list.page}
          pageSize={list.pageSize}
          itemCount={students.data?.items.length || 0}
          totalApproximate={students.data?.totalApproximate}
          hasNext={list.canNavigateNext(students.data?.nextCursor)}
          nextBlockedReason={
            students.data?.nextCursor && !list.canNavigateNext(students.data.nextCursor)
              ? "これ以上は絞り込みを追加するか、1ページの件数を増やしてください。"
              : undefined
          }
          canGoPrevious={list.canGoPrevious}
          onNext={() => list.nextPage(students.data?.nextCursor)}
          onPrevious={list.previousPage}
          onPageSizeChange={list.setPageSize}
        />
      </Card>

      <Modal
        open={createOpen}
        onClose={() => !creating && setCreateOpen(false)}
        title="生徒を追加"
        description="答案の氏名照合に使う情報を入力してください。"
        size="large"
      >
        <StudentForm
          onSubmit={createStudent}
          onCancel={() => setCreateOpen(false)}
          submitting={creating}
          submitLabel="生徒を追加"
          error={createError}
        />
      </Modal>

      <RosterImportDialog
        open={importOpen}
        onClose={() => setImportOpen(false)}
        onApplied={() => {
          setImportOpen(false);
          students.reload();
        }}
      />
    </div>
  );
}

interface RosterImportPreview {
  importId: string;
  fileName?: string;
  detectedEncoding?: string;
  headers?: string[];
  sampleRows?: Array<Record<string, string>>;
  createCount?: number;
  updateCount?: number;
  skipCount?: number;
  errorCount?: number;
  errors?: Array<{ row: number; message: string }>;
  createdAt?: string;
}

function RosterImportDialog({
  open,
  onClose,
  onApplied,
}: {
  open: boolean;
  onClose: () => void;
  onApplied: () => void;
}) {
  const [file, setFile] = useState<File>();
  const [encoding, setEncoding] = useState("auto");
  const [strategy, setStrategy] = useState("create-update-skip");
  const [preview, setPreview] = useState<RosterImportPreview>();
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();
  const [applied, setApplied] = useState<{
    created: number;
    updated: number;
    skipped: number;
  }>();

  useEffect(() => {
    if (open) return;
    setFile(undefined);
    setEncoding("auto");
    setStrategy("create-update-skip");
    setPreview(undefined);
    setStep(1);
    setError(undefined);
    setApplied(undefined);
  }, [open]);

  async function stage(event: FormEvent) {
    event.preventDefault();
    if (!file) return;
    setWorking(true);
    setError(undefined);
    try {
      const form = new FormData();
      form.append("file", file);
      form.append("encoding", encoding);
      const staged = await api.post<RosterImportPreview>(
        "/roster-imports",
        form,
        { idempotencyKey: newIdempotencyKey() },
      );
      const full = await api.get<RosterImportPreview>(
        `/roster-imports/${encodeURIComponent(staged.importId)}`,
      );
      setPreview(full);
      setStep(2);
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "CSVファイルを確認できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function applyImport() {
    if (!preview) return;
    setWorking(true);
    setError(undefined);
    try {
      const result = await api.post<{
        created: number;
        updated: number;
        skipped: number;
      }>(
        `/roster-imports/${encodeURIComponent(preview.importId)}:apply`,
        { strategy },
        { idempotencyKey: newIdempotencyKey() },
      );
      setApplied(result);
      setStep(3);
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "生徒名簿を適用できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={() => !working && onClose()}
      title="CSVから生徒を取り込む"
      description={`手順 ${step} / 3`}
      size="large"
      footer={
        step === 2 ? (
          <>
            <Button
              variant="secondary"
              onClick={() => setStep(1)}
              disabled={working}
            >
              戻る
            </Button>
            <Button
              onClick={() => void applyImport()}
              disabled={working || Boolean(preview?.errorCount)}
            >
              {working
                ? "適用しています…"
                : `${preview?.createCount || 0}名を追加、${preview?.updateCount || 0}名を更新`}
            </Button>
          </>
        ) : step === 3 ? (
          <Button onClick={onApplied}>閉じる</Button>
        ) : undefined
      }
    >
      <ol className="stepper" aria-label="CSV取り込み手順">
        {["ファイル", "確認", "完了"].map((label, index) => (
          <li
            key={label}
            className={
              index + 1 === step
                ? "is-current"
                : index + 1 < step
                  ? "is-complete"
                  : ""
            }
          >
            <span>{index + 1 < step ? <Icon name="check" /> : index + 1}</span>
            {label}
          </li>
        ))}
      </ol>
      {error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
      {step === 1 ? (
        <form className="import-form" onSubmit={stage}>
          <Field
            label="生徒名簿CSV"
            htmlFor="roster-file"
            required
            hint="UTF-8（BOM付き）またはShift_JISに対応しています。"
          >
            <input
              id="roster-file"
              type="file"
              accept=".csv,text/csv"
              required
              onChange={(event) => setFile(event.target.files?.[0])}
            />
          </Field>
          <Field label="文字コード" htmlFor="roster-encoding">
            <select
              id="roster-encoding"
              value={encoding}
              onChange={(event) => setEncoding(event.target.value)}
            >
              <option value="auto">自動判定（推奨）</option>
              <option value="utf-8-bom">UTF-8（BOM付き）</option>
              <option value="shift-jis">Shift_JIS</option>
            </select>
          </Field>
          <InlineAlert tone="info">
            <p>
              ファイル全体を検証してから適用します。エラーがある行だけを黙って取り込むことはありません。
            </p>
          </InlineAlert>
          <div className="form-actions">
            <Button type="button" variant="secondary" onClick={onClose}>
              キャンセル
            </Button>
            <Button type="submit" disabled={!file || working}>
              {working ? "検証しています…" : "内容を確認"}
            </Button>
          </div>
        </form>
      ) : null}
      {step === 2 && preview ? (
        <div className="import-preview">
          <div className="import-summary">
            <div>
              <span>追加</span>
              <strong>{preview.createCount || 0}名</strong>
            </div>
            <div>
              <span>更新</span>
              <strong>{preview.updateCount || 0}名</strong>
            </div>
            <div>
              <span>スキップ</span>
              <strong>{preview.skipCount || 0}名</strong>
            </div>
            <div className={preview.errorCount ? "has-error" : ""}>
              <span>エラー</span>
              <strong>{preview.errorCount || 0}件</strong>
            </div>
          </div>
          <div className="form-grid form-grid--2">
            <Field label="更新方法" htmlFor="import-strategy">
              <select
                id="import-strategy"
                value={strategy}
                onChange={(event) => setStrategy(event.target.value)}
              >
                <option value="create-update-skip">
                  新規追加・生徒番号で更新・空行をスキップ
                </option>
                <option value="create-only">新規生徒だけ追加</option>
                <option value="update-only">既存生徒だけ更新</option>
              </select>
            </Field>
            <div className="preview-meta">
              <span>判定した文字コード</span>
              <strong>{preview.detectedEncoding || "自動判定"}</strong>
              <small>{formatDateTime(preview.createdAt)}</small>
            </div>
          </div>
          {preview.errors?.length ? (
            <InlineAlert tone="danger" title="適用前に修正してください">
              <ul>
                {preview.errors.slice(0, 5).map((item) => (
                  <li key={`${item.row}-${item.message}`}>
                    {item.row}行目: {item.message}
                  </li>
                ))}
              </ul>
              <a
                href={`/api/v1/roster-imports/${encodeURIComponent(preview.importId)}/errors.csv`}
              >
                エラー一覧をダウンロード
              </a>
            </InlineAlert>
          ) : (
            <InlineAlert tone="success">
              <p>すべての行を適用できます。</p>
            </InlineAlert>
          )}
        </div>
      ) : null}
      {step === 3 && applied ? (
        <div className="import-complete">
          <span className="import-complete__icon">
            <Icon name="check" size={32} />
          </span>
          <h3>生徒名簿を取り込みました</h3>
          <p>
            {applied.created}名を追加、{applied.updated}名を更新、
            {applied.skipped}名をスキップしました。
          </p>
        </div>
      ) : null}
    </Modal>
  );
}

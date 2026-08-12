import { useEffect, useMemo, useState } from "react";
import { Icon } from "../components/Icon";
import {
  Button,
  Card,
  ErrorState,
  Field,
  InlineAlert,
  LoadingState,
  PageHeader,
} from "../components/ui";
import { ApiError } from "../lib/api";
import {
  answerStyleLabel,
  gradeLabel,
  pageRangeLabel,
  rememberTemplateGenerationBatchId,
  templateGenerationApi,
  testTypeLabel,
  warningMessage,
} from "../lib/templateGeneration";
import { Link, useParams } from "../router";
import type {
  CreatedTemplateLink,
  TemplateGenerationBatch,
  TemplateGenerationGradeLevel,
  TemplateGenerationUnit,
  TemplateGenerationWarning,
} from "../types";
import { useApiQuery } from "../hooks/useApiQuery";

type SelectableGrade = Exclude<TemplateGenerationGradeLevel, "unknown">;

interface UnitDraft {
  baseTestName: string;
  grade: SelectableGrade | "";
}

const GRADES: SelectableGrade[] = [
  "grade1",
  "grade2",
  "grade3",
  "grade4",
  "grade5",
  "grade6",
];

export function TemplateGenerationFinalCheckPage() {
  const { batchId = "" } = useParams<{ batchId: string }>();
  const batchQuery = useApiQuery<TemplateGenerationBatch>(
    `template-generation-final-check:${batchId}`,
    (signal) => templateGenerationApi.getBatch(batchId, signal),
    Boolean(batchId),
  );
  const [drafts, setDrafts] = useState<Record<string, UnitDraft>>({});
  const [dirtyUnits, setDirtyUnits] = useState<Set<string>>(new Set());
  const [bulkGrade, setBulkGrade] = useState<SelectableGrade | "">("");
  const [savingKey, setSavingKey] = useState<string>();
  const [confirming, setConfirming] = useState(false);
  const [confirmedBatch, setConfirmedBatch] = useState<TemplateGenerationBatch>();
  const [rowConflict, setRowConflict] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const batch = confirmedBatch ?? batchQuery.data;

  useEffect(() => {
    if (batchId) rememberTemplateGenerationBatchId(batchId);
  }, [batchId]);

  useEffect(() => {
    if (!batch) return;
    setDrafts((current) => {
      const next = { ...current };
      for (const unit of batch.units) {
        if (next[unit.id]) continue;
        next[unit.id] = {
          baseTestName: initialUnitName(unit),
          grade: selectableGrade(unit.resolvedGrade),
        };
      }
      return next;
    });
  }, [batch]);

  const validation = useMemo(
    () =>
      batch
        ? validateFinalCheck(
            batch,
            drafts,
            dirtyUnits,
            rowConflict,
          )
        : { confirmable: false, reasons: [] as string[], duplicateNames: [] },
    [batch, dirtyUnits, drafts, rowConflict],
  );

  function updateUnitDraft(unitId: string, changes: Partial<UnitDraft>) {
    setDrafts((current) => ({
      ...current,
      [unitId]: { ...current[unitId], ...changes } as UnitDraft,
    }));
    setDirtyUnits((current) => new Set(current).add(unitId));
  }

  function applyBulkGrade() {
    if (!batch || !bulkGrade) return;
    const targets = batch.units.filter(
      (unit) =>
        !isResolvedGrade(unit.resolvedGrade) &&
        !hasWarning(unit, "GRADE_CONFLICT") &&
        !hasWarning(unit, "FILENAME_GRADE_CONFLICT"),
    );
    setDrafts((current) => {
      const next = { ...current };
      for (const unit of targets) {
        next[unit.id] = {
          ...(next[unit.id] ?? {
            baseTestName: initialUnitName(unit),
            grade: "",
          }),
          grade: bulkGrade,
        };
      }
      return next;
    });
    setDirtyUnits((current) => {
      const next = new Set(current);
      targets.forEach((unit) => next.add(unit.id));
      return next;
    });
  }

  async function saveUnit(unit: TemplateGenerationUnit) {
    if (!batch || savingKey) return;
    const draft = drafts[unit.id];
    if (!draft?.grade || (batch.testType === "other" && !draft.baseTestName.trim())) {
      return;
    }
    setSavingKey(`unit:${unit.id}`);
    setActionError(undefined);
    try {
      await templateGenerationApi.updateUnit(batch.batchId, unit.id, {
        ...(batch.testType === "other"
          ? { baseTestName: draft.baseTestName.trim() }
          : {}),
        resolvedGrade: draft.grade,
        gradeConfirmedByUser:
          hasWarning(unit, "GRADE_CONFLICT") ||
          hasWarning(unit, "FILENAME_GRADE_CONFLICT"),
        expectedRowVersion: unit.rowVersion,
      });
      setDirtyUnits((current) => {
        const next = new Set(current);
        next.delete(unit.id);
        return next;
      });
      batchQuery.reload();
    } catch (reason) {
      handleMutationError(reason);
    } finally {
      setSavingKey(undefined);
    }
  }

  async function confirmTemplates() {
    if (!batch || !validation.confirmable || confirming) return;
    setConfirming(true);
    setActionError(undefined);
    try {
      const result = await templateGenerationApi.confirmBatch(
        batch.batchId,
        batch.rowVersion,
      );
      setConfirmedBatch(result);
    } catch (reason) {
      handleMutationError(reason);
    } finally {
      setConfirming(false);
    }
  }

  function handleMutationError(reason: unknown) {
    if (isRowVersionConflict(reason)) {
      setRowConflict(true);
      setActionError(warningMessage("STALE_ROW_VERSION"));
      return;
    }
    setActionError(mutationErrorMessage(reason));
  }

  function reloadAfterConflict() {
    setDrafts({});
    setDirtyUnits(new Set());
    setRowConflict(false);
    setActionError(undefined);
    setConfirmedBatch(undefined);
    batchQuery.reload();
  }

  if (!batch && batchQuery.status === "loading") {
    return <LoadingState label="最終確認を読み込んでいます" />;
  }

  if (!batch) {
    return (
      <div className="page">
        <ErrorState error={batchQuery.error} onRetry={batchQuery.reload} />
      </div>
    );
  }

  const createdTemplates = createdTemplateLinks(batch);
  const readOnly = batch.status !== "needsFinalCheck";
  const bulkGradeTargets = batch.units.filter(
    (unit) =>
      !isResolvedGrade(unit.resolvedGrade) &&
      !hasWarning(unit, "GRADE_CONFLICT") &&
      !hasWarning(unit, "FILENAME_GRADE_CONFLICT"),
  );

  return (
    <div className="page template-generation-page">
      <PageHeader
        eyebrow="テンプレート生成"
        title={batch.status === "completed" ? "テンプレートを作成しました" : "生成結果の最終確認"}
        description="学年と生成内容を確認します。HOP・STEP・クラス分けテストの名前は確定情報から自動で統一します。"
        backAction={
          <Link
            className="back-link"
            to={`/templates/generation/${encodeURIComponent(batch.batchId)}`}
          >
            <Icon name="arrowLeft" size={17} />
            生成状況へ戻る
          </Link>
        }
      />

      {actionError ? (
        <InlineAlert
          tone="danger"
          title={rowConflict ? "最新の内容を読み直してください" : "変更を保存できませんでした"}
          action={
            rowConflict ? (
              <Button size="small" onClick={reloadAfterConflict}>
                最新の内容を読み込む
              </Button>
            ) : undefined
          }
        >
          <p>{actionError}</p>
        </InlineAlert>
      ) : null}

      {batch.status === "completed" ? (
        <InlineAlert tone="success" title="すべてのテンプレートを作成しました">
          <p>STEPの各バリエーションも、それぞれ独立したテンプレートです。</p>
        </InlineAlert>
      ) : batch.status !== "needsFinalCheck" ? (
        <InlineAlert tone="warning" title="まだ最終確認できません">
          <p>すべての生成が成功してから、この画面で確認してください。</p>
        </InlineAlert>
      ) : null}

      <Card className="template-final-summary-card">
        <dl className="template-plan-summary">
          <div>
            <dt>試験タイプ</dt>
            <dd>{testTypeLabel(batch.testType)}</dd>
          </div>
          <div>
            <dt>教科</dt>
            <dd>{batch.subject}</dd>
          </div>
          {batch.testType === "other" ? (
            <div>
              <dt>問題形式</dt>
              <dd>{answerStyleLabel(batch.answerStyle)}</dd>
            </div>
          ) : null}
          <div>
            <dt>元PDF</dt>
            <dd>{batch.sourceDisplayName || "アップロードしたPDF"}</dd>
          </div>
          <div>
            <dt>ページ数</dt>
            <dd>{batch.sourcePageCount}ページ</dd>
          </div>
          <div>
            <dt>作成予定</dt>
            <dd>{batch.expectedUnitCount}件</dd>
          </div>
        </dl>
      </Card>

      {!readOnly && bulkGradeTargets.length ? (
        <Card className="template-final-bulk-card">
          <div>
            <strong>未設定の学年をまとめて入力</strong>
            <small>学年が競合している項目には適用しません。</small>
          </div>
          <select
            aria-label="まとめて適用する学年"
            value={bulkGrade}
            onChange={(event) => setBulkGrade(event.target.value as SelectableGrade | "")}
          >
            <option value="">学年を選択</option>
            {GRADES.map((grade) => (
              <option value={grade} key={grade}>
                {gradeLabel(grade)}
              </option>
            ))}
          </select>
          <Button
            size="small"
            variant="secondary"
            disabled={!bulkGrade}
            onClick={applyBulkGrade}
          >
            未設定のすべてにこの学年を適用
          </Button>
        </Card>
      ) : null}

      {batch.testType === "step" ? (
        <div className="template-step-set-list">
          {stepSetIndexes(batch.units).map((setIndex) => {
            const units = batch.units.filter((unit) => unit.stepSetIndex === setIndex);
            return (
              <Card className="template-step-set-card" key={setIndex}>
                <div>
                  <strong>STEPセット {setIndex}</strong>
                  <small>
                    {units.map(pageRangeLabel).join("・")} を、教科・学年・固定枝番から命名します。
                  </small>
                </div>
                <div className="template-step-name-preview">
                  {units.map((unit) => (
                    <span key={unit.id}>
                      <b>{deterministicKnownName(
                        batch,
                        unit,
                        drafts[unit.id]?.grade ?? selectableGrade(unit.resolvedGrade),
                      )}</b>
                    </span>
                  ))}
                </div>
              </Card>
            );
          })}
        </div>
      ) : null}

      <div className="template-final-unit-list">
        {batch.units.map((unit) => {
          const draft = drafts[unit.id] ?? {
            baseTestName: initialUnitName(unit),
            grade: selectableGrade(unit.resolvedGrade),
          };
          const gradeMissing =
            !isResolvedGrade(unit.resolvedGrade) &&
            (hasWarning(unit, "GRADE_REQUIRED") ||
              (!isResolvedGrade(unit.filenameGrade) &&
                !isResolvedGrade(unit.paperGrade)));
          const gradeConflict = hasWarning(unit, "GRADE_CONFLICT") ||
            hasWarning(unit, "FILENAME_GRADE_CONFLICT");
          const warnings = unitWarnings(unit, batch.testType);
          const finalName = batch.testType === "other"
            ? draft.baseTestName.trim() || "テスト名未設定"
            : deterministicKnownName(batch, unit, draft.grade);

          return (
            <Card className="template-final-unit-card" key={unit.id}>
              <header>
                <div>
                  <span>テンプレート {unit.sequence}</span>
                  <h2>{finalName}</h2>
                </div>
                <div>
                  <strong>{pageRangeLabel(unit)}</strong>
                  {unit.stepSetIndex ? (
                    <small>
                      セット {unit.stepSetIndex}・枝番
                      {unit.deterministicSuffix || `-${unit.stepVariationIndex}`}
                      （固定）
                    </small>
                  ) : null}
                </div>
              </header>

              <div className="template-final-unit-grid">
                {batch.testType === "other" ? (
                  <Field
                    label="テスト名"
                    htmlFor={`unit-name-${unit.id}`}
                    required
                    hint={
                      unit.printedTestName
                        ? `用紙から読み取った名前：${unit.printedTestName}`
                        : "用紙から名前を読み取れませんでした。"
                    }
                    error={!draft.baseTestName.trim() ? "テスト名を入力してください。" : undefined}
                  >
                    <input
                      id={`unit-name-${unit.id}`}
                      aria-label="テスト名"
                      value={draft.baseTestName}
                      disabled={readOnly || Boolean(savingKey)}
                      onChange={(event) =>
                        updateUnitDraft(unit.id, { baseTestName: event.target.value })
                      }
                    />
                  </Field>
                ) : (
                  <div className="template-final-readonly-field">
                    <span>最終テンプレート名</span>
                    <strong>{finalName}</strong>
                    <small>教科・学年・分割番号から自動で決まり、変更できません。</small>
                  </div>
                )}

                <Field
                  label="学年"
                  htmlFor={`unit-grade-${unit.id}`}
                  required
                  error={!draft.grade ? "学年を選択してください。" : undefined}
                >
                  <select
                    id={`unit-grade-${unit.id}`}
                    aria-label="学年"
                    value={draft.grade}
                    disabled={readOnly || Boolean(savingKey)}
                    onChange={(event) =>
                      updateUnitDraft(unit.id, {
                        grade: event.target.value as SelectableGrade | "",
                      })
                    }
                  >
                    <option value="">選択してください</option>
                    {GRADES.map((grade) => (
                      <option value={grade} key={grade}>
                        {gradeLabel(grade)}
                      </option>
                    ))}
                  </select>
                </Field>
              </div>

              <dl className="template-final-evidence">
                <div>
                  <dt>ファイル名の学年</dt>
                  <dd>{gradeLabel(unit.filenameGrade)}</dd>
                </div>
                <div>
                  <dt>用紙の学年</dt>
                  <dd>{gradeLabel(unit.paperGrade)}</dd>
                </div>
                <div>
                  <dt>抽出した問題</dt>
                  <dd>
                    {unit.questionCount !== undefined
                      ? `${unit.questionCount}問`
                      : "—"}
                  </dd>
                </div>
                <div>
                  <dt>ページ向き</dt>
                  <dd>{orientationSummary(unit)}</dd>
                </div>
              </dl>

              {gradeMissing ? (
                <InlineAlert tone="warning" title="学年の確認が必要です">
                  <p>
                    学年がファイル名またはテスト用紙から確認できませんでした。学年を選択してください。
                  </p>
                </InlineAlert>
              ) : null}
              {gradeConflict ? (
                <InlineAlert tone="warning" title="学年が一致しません">
                  <p>
                    ファイル名は{gradeLabel(unit.filenameGrade)}、テスト用紙は
                    {gradeLabel(unit.paperGrade)}です。正しい学年を選択してください。
                  </p>
                </InlineAlert>
              ) : null}
              {warnings.length ? (
                <ul className="template-final-warning-list">
                  {warnings.map((warning) => (
                    <li key={`${unit.id}-${warning.code}`}>
                      {warning.message || warningMessage(warning.code)}
                    </li>
                  ))}
                </ul>
              ) : null}

              {!readOnly ? (
                <div className="template-final-unit-actions">
                  <span>
                    {dirtyUnits.has(unit.id) ? "未保存の変更があります" : "保存済み"}
                  </span>
                  <Button
                    size="small"
                    variant="secondary"
                    disabled={
                      !dirtyUnits.has(unit.id) ||
                      !draft.grade ||
                      (batch.testType === "other" && !draft.baseTestName.trim()) ||
                      Boolean(savingKey)
                    }
                    onClick={() => void saveUnit(unit)}
                  >
                    {savingKey === `unit:${unit.id}` ? "保存しています" : "変更を保存"}
                  </Button>
                </div>
              ) : null}
            </Card>
          );
        })}
      </div>

      {batch.status !== "completed" ? (
        <Card className="template-final-confirm-card">
          <div>
            <strong>すべての内容を確認してください</strong>
            {validation.reasons.length ? (
              <ul>
                {validation.reasons.map((reason) => (
                  <li key={reason}>{reason}</li>
                ))}
              </ul>
            ) : (
              <p>{batch.expectedUnitCount}件を独立したテンプレートとして作成できます。</p>
            )}
          </div>
          <Button
            size="large"
            leadingIcon="check"
            disabled={!validation.confirmable || confirming}
            onClick={() => void confirmTemplates()}
          >
            {confirming ? "テンプレートを作成しています" : "確認してテンプレートを作成"}
          </Button>
        </Card>
      ) : null}

      {batch.status === "completed" && createdTemplates.length ? (
        <Card className="template-created-links">
          <h2>作成したテンプレート</h2>
          <div>
            {createdTemplates.map((template) => (
              <Link
                className="button button--secondary button--medium"
                key={`${template.templateId}-${template.versionId}`}
                to={`/templates/${encodeURIComponent(template.templateId)}/versions/${encodeURIComponent(template.versionId)}`}
              >
                <span>{template.title}</span>
                <Icon name="arrowRight" size={17} />
              </Link>
            ))}
          </div>
        </Card>
      ) : null}
    </div>
  );
}

export function validateFinalCheck(
  batch: TemplateGenerationBatch,
  drafts: Record<string, UnitDraft>,
  dirtyUnits: ReadonlySet<string>,
  rowConflict: boolean,
) {
  const reasons: string[] = [];
  const finalNames = batch.units.map((unit) => {
    if (batch.testType !== "other") {
      if (!drafts[unit.id]?.grade) return "";
      return deterministicKnownName(
        batch,
        unit,
        drafts[unit.id]?.grade || "",
      );
    }
    return drafts[unit.id]?.baseTestName.trim() || "";
  });
  const duplicates = finalNames.filter(
    (name, index) => name && finalNames.indexOf(name) !== index,
  );

  if (batch.status !== "needsFinalCheck") {
    reasons.push("生成が完了して最終確認になるまでお待ちください。");
  }
  if (finalNames.some((name) => !name)) {
    reasons.push("すべてのテスト名を入力してください。");
  }
  if (batch.units.some((unit) => !drafts[unit.id]?.grade)) {
    reasons.push("すべての学年を選択してください。");
  }
  if (duplicates.length) {
    reasons.push("重複しているテンプレート名を変更してください。");
  }
  if (dirtyUnits.size) {
    reasons.push("未保存の変更を保存してください。");
  }
  if (rowConflict) {
    reasons.push("最新の内容を読み直してください。");
  }
  if (batch.failedUnitCount || batch.units.some((unit) => unit.status === "failed")) {
    reasons.push("生成に失敗した項目を再試行してください。");
  }
  if (hasBlockingWarnings(batch)) {
    reasons.push("確認が必要な警告を解決してください。");
  }
  if (batch.finalCheckReady === false) {
    reasons.push("サーバー側の最終確認条件を満たしていません。");
  }

  return {
    confirmable: reasons.length === 0,
    reasons: Array.from(new Set(reasons)),
    duplicateNames: Array.from(new Set(duplicates)),
  };
}

function initialUnitName(unit: TemplateGenerationUnit) {
  return (
    unit.confirmedBaseTestName ||
    unit.userConfirmedBaseName ||
    unit.finalTemplateName ||
    unit.printedTestName ||
    ""
  );
}

function deterministicKnownName(
  batch: TemplateGenerationBatch,
  unit: TemplateGenerationUnit,
  grade: SelectableGrade | "",
) {
  const gradeNumber = /^grade([1-6])$/u.exec(grade)?.[1];
  const prefix = `${batch.subject}${gradeNumber ? `${gradeNumber}年` : "（学年未設定）"}`;
  switch (batch.testType) {
    case "hop":
      return `${prefix}HOP${unit.sequence}`;
    case "step":
      return `${prefix}STEPセット${unit.stepSetIndex ?? "?"}-${unit.stepVariationIndex ?? "?"}`;
    case "classPlacement":
      return `${prefix}クラス分けテスト`;
    case "other":
      return initialUnitName(unit) || "テスト名未設定";
  }
}

function stepSetIndexes(units: TemplateGenerationUnit[]) {
  return Array.from(
    new Set(
      units
        .map((unit) => unit.stepSetIndex)
        .filter((value): value is number => typeof value === "number"),
    ),
  ).sort((left, right) => left - right);
}

function selectableGrade(
  value?: TemplateGenerationGradeLevel | null,
): SelectableGrade | "" {
  return value && value !== "unknown" ? value : "";
}

function isResolvedGrade(value?: TemplateGenerationGradeLevel | null) {
  return Boolean(value && value !== "unknown");
}

function hasWarning(unit: TemplateGenerationUnit, code: string) {
  return [...(unit.warnings ?? []), ...(unit.blockingWarnings ?? [])].some(
    (warning) => (typeof warning === "string" ? warning : warning.code) === code,
  );
}

function unitWarnings(
  unit: TemplateGenerationUnit,
  testType: TemplateGenerationBatch["testType"],
) {
  const ignoredNameWarnings = testType === "other"
    ? []
    : [
        "TEST_NAME_REQUIRED",
        "STEP_NAME_MISMATCH",
        "STEP_NAME_ALREADY_SUFFIXED",
        "DUPLICATE_TEMPLATE_NAME",
      ];
  const warnings = [...(unit.warnings ?? []), ...(unit.blockingWarnings ?? [])]
    .map<TemplateGenerationWarning>((warning) =>
      typeof warning === "string"
        ? { code: warning, severity: "blocking" }
        : warning,
    )
    .filter(
      (warning) =>
        ![
          "GRADE_REQUIRED",
          "GRADE_CONFLICT",
          "FILENAME_GRADE_CONFLICT",
          ...ignoredNameWarnings,
        ].includes(warning.code),
    );
  return Array.from(
    new Map(warnings.map((warning) => [warning.code, warning])).values(),
  );
}

function orientationSummary(unit: TemplateGenerationUnit) {
  if (unit.orientationCorrectionSummary) return unit.orientationCorrectionSummary;
  const rotations = unit.appliedRotations?.filter(
    (rotation) => rotation.clockwiseDegrees !== 0,
  );
  if (!rotations?.length) return "補正なし";
  return `${rotations.length}ページを自動補正`;
}

function hasBlockingWarnings(batch: TemplateGenerationBatch) {
  const ignoredNameWarnings = batch.testType === "other"
    ? new Set<string>()
    : new Set([
        "TEST_NAME_REQUIRED",
        "STEP_NAME_MISMATCH",
        "STEP_NAME_ALREADY_SUFFIXED",
        "DUPLICATE_TEMPLATE_NAME",
      ]);
  const warnings = [
    ...(batch.warnings ?? []),
    ...(batch.blockingWarnings ?? []),
    ...batch.units.flatMap((unit) => [
      ...(unit.warnings ?? []),
      ...(unit.blockingWarnings ?? []),
    ]),
  ];
  return warnings.some((warning) => {
    const code = typeof warning === "string" ? warning : warning.code;
    if (code === "ORIENTATION_CORRECTED" || ignoredNameWarnings.has(code)) {
      return false;
    }
    return typeof warning === "string" || warning.severity === "blocking";
  });
}

function isRowVersionConflict(reason: unknown) {
  return (
    reason instanceof ApiError &&
    (reason.status === 409 || reason.problem.code === "STALE_ROW_VERSION")
  );
}

function mutationErrorMessage(reason: unknown) {
  if (reason instanceof ApiError) {
    const code = reason.problem.code || reason.problem.errors?.[0]?.code;
    return code ? warningMessage(code) : reason.message;
  }
  return reason instanceof Error ? reason.message : "変更を保存できませんでした。";
}

function createdTemplateLinks(batch: TemplateGenerationBatch): CreatedTemplateLink[] {
  if (batch.createdTemplates?.length) return batch.createdTemplates;
  return batch.units
    .filter((unit) => unit.createdTemplateId && unit.createdTemplateVersionId)
    .map((unit) => ({
      templateId: unit.createdTemplateId as string,
      versionId: unit.createdTemplateVersionId as string,
      title: unit.finalTemplateName || `テンプレート ${unit.sequence}`,
    }));
}

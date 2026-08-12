import { api, ApiError, newIdempotencyKey } from "./api";
import type {
  CreateTemplateGenerationBatchRequest,
  CreatedTemplateLink,
  TemplateGenerationAnswerStyle,
  TemplateGenerationBatch,
  TemplateGenerationBatchSummary,
  TemplateGenerationBatchStatus,
  TemplateGenerationGradeLevel,
  TemplateGenerationSubject,
  TemplateGenerationTestType,
  TemplateGenerationUnit,
  TemplateGenerationUnitStatus,
  TemplateGenerationWarning,
  ResumableTemplateGenerationBatchList,
} from "../types";

const BATCHES_PATH = "/template-generation-batches";
const RESUMABLE_BATCH_LIMIT = 20;
const RECENT_BATCH_STORAGE_KEY =
  "ooki-grader:template-generation:recent-batch-ids:v1";

type JsonObject = Record<string, unknown>;

export interface UpdateTemplateGenerationUnitRequest {
  baseTestName?: string;
  resolvedGrade?: Exclude<TemplateGenerationGradeLevel, "unknown">;
  gradeConfirmedByUser?: boolean;
  teacherNote?: string;
  expectedRowVersion: number;
}

export interface UpdateTemplateGenerationStepSetRequest {
  baseTestName: string;
  expectedUnitRowVersions: Record<string, number>;
}

export const templateGenerationApi = {
  async createBatch(
    request: CreateTemplateGenerationBatchRequest,
    signal?: AbortSignal,
  ) {
    const response = await api.post<unknown>(BATCHES_PATH, request, {
      idempotencyKey: newIdempotencyKey(),
      signal,
    });
    const batch = normalizeTemplateGenerationBatch(response);
    rememberTemplateGenerationBatchId(batch.batchId);
    return batch;
  },

  async getBatch(batchId: string, signal?: AbortSignal) {
    const response = await api.get<unknown>(
      `${BATCHES_PATH}/${encodeURIComponent(batchId)}`,
      undefined,
      signal,
    );
    const batch = normalizeTemplateGenerationBatch(response);
    if (isResumableBatchStatus(batch.status)) {
      rememberTemplateGenerationBatchId(batch.batchId);
    } else {
      forgetTemplateGenerationBatchId(batch.batchId);
    }
    return batch;
  },

  async listResumableBatches(
    signal?: AbortSignal,
  ): Promise<ResumableTemplateGenerationBatchList> {
    let serverItems: TemplateGenerationBatchSummary[] = [];
    let browserRecoveryOnly = false;

    try {
      const response = await api.get<unknown>(
        `${BATCHES_PATH}/resumable`,
        { limit: RESUMABLE_BATCH_LIMIT },
        signal,
      );
      const raw = isObject(response) ? response : {};
      serverItems = (Array.isArray(raw.items) ? raw.items : [])
        .map(normalizeTemplateGenerationBatchSummary)
        .filter(
          (item): item is TemplateGenerationBatchSummary =>
            item !== undefined && isResumableBatchStatus(item.status),
        );
      // remember() prepends, so visit oldest-first to retain server ordering.
      [...serverItems]
        .reverse()
        .forEach((item) => rememberTemplateGenerationBatchId(item.id));
    } catch (reason) {
      if (signal?.aborted || isAbortError(reason)) throw reason;
      if (
        reason instanceof ApiError &&
        [404, 405].includes(reason.status)
      ) {
        // Older hosts do not expose the list endpoint. Recover safe, bounded
        // IDs remembered by this browser through the existing detail endpoint.
        browserRecoveryOnly = true;
      } else {
        // Authentication, authorization, server, and unexpected network errors
        // must stay visible so teachers can retry instead of seeing a false
        // "nothing in progress" state.
        throw reason;
      }
    }

    const byId = new Map(serverItems.map((item) => [item.id, item]));
    const missingIds = recentTemplateGenerationBatchIds().filter(
      (id) => !byId.has(id),
    );
    await Promise.all(
      missingIds.map(async (id) => {
        try {
          const batch = await templateGenerationApi.getBatch(id, signal);
          if (isResumableBatchStatus(batch.status)) {
            byId.set(id, summaryFromBatch(batch));
          } else {
            forgetTemplateGenerationBatchId(id);
          }
        } catch (reason) {
          if (reason instanceof ApiError && [403, 404].includes(reason.status)) {
            // The batch was deleted or belongs to another signed-in teacher.
            forgetTemplateGenerationBatchId(id);
            return;
          }
          throw reason;
        }
      }),
    );
    if (signal?.aborted) throw new DOMException("Aborted", "AbortError");

    const recoveredItems = missingIds
      .map((id) => byId.get(id))
      .filter(
        (item): item is TemplateGenerationBatchSummary => Boolean(item),
      );
    return {
      items: [...serverItems, ...recoveredItems].slice(
        0,
        RESUMABLE_BATCH_LIMIT,
      ),
      limit: RESUMABLE_BATCH_LIMIT,
      browserRecoveryOnly,
    };
  },

  startGeneration(batchId: string, expectedRowVersion: number) {
    rememberTemplateGenerationBatchId(batchId);
    return api.post(
      `${BATCHES_PATH}/${encodeURIComponent(batchId)}/generate`,
      { expectedRowVersion },
      { idempotencyKey: newIdempotencyKey() },
    );
  },

  retryFailedUnits(batchId: string, expectedRowVersion: number) {
    return api.post(
      `${BATCHES_PATH}/${encodeURIComponent(batchId)}/retry`,
      { expectedRowVersion },
      { idempotencyKey: newIdempotencyKey() },
    );
  },

  async cancelBatch(
    batchId: string,
    expectedRowVersion: number,
    idempotencyKey: string = newIdempotencyKey(),
  ) {
    const response = await api.post(
      `${BATCHES_PATH}/${encodeURIComponent(batchId)}/cancel`,
      { expectedRowVersion },
      { idempotencyKey },
    );
    forgetTemplateGenerationBatchId(batchId);
    return response;
  },

  updateUnit(
    batchId: string,
    unitId: string,
    request: UpdateTemplateGenerationUnitRequest,
  ) {
    return api.patch(
      `${BATCHES_PATH}/${encodeURIComponent(batchId)}/units/${encodeURIComponent(unitId)}`,
      request,
      { idempotencyKey: newIdempotencyKey() },
    );
  },

  updateStepSet(
    batchId: string,
    setIndex: number,
    request: UpdateTemplateGenerationStepSetRequest,
  ) {
    return api.patch(
      `${BATCHES_PATH}/${encodeURIComponent(batchId)}/step-sets/${setIndex}`,
      request,
      { idempotencyKey: newIdempotencyKey() },
    );
  },

  async confirmBatch(batchId: string, expectedRowVersion: number) {
    const response = await api.post<unknown>(
      `${BATCHES_PATH}/${encodeURIComponent(batchId)}/confirm`,
      { expectedRowVersion },
      { idempotencyKey: newIdempotencyKey() },
    );
    if (isObject(response) && (response.status || response.batchId)) {
      const batch = normalizeTemplateGenerationBatch(response);
      if (isResumableBatchStatus(batch.status)) {
        rememberTemplateGenerationBatchId(batchId);
      } else {
        forgetTemplateGenerationBatchId(batchId);
      }
      return batch;
    }
    return templateGenerationApi.getBatch(batchId);
  },
};

export function normalizeTemplateGenerationBatch(
  value: unknown,
): TemplateGenerationBatch {
  const raw = isObject(value) ? value : {};
  const rawUnits = Array.isArray(raw.units) ? raw.units : [];
  const units = rawUnits.map(normalizeUnit);
  const created = arrayOfObjects(raw.createdTemplates ?? raw.templates).map(
    normalizeCreatedTemplate,
  );
  const batchId = stringValue(raw.batchId ?? raw.id);
  if (!batchId) {
    throw new Error("テンプレート生成バッチの識別子がありません。");
  }

  return {
    batchId,
    status: normalizeBatchStatus(raw.status),
    testType: normalizeTestType(raw.testType),
    subject: normalizeSubject(raw.subject),
    answerStyle: normalizeAnswerStyle(raw.answerStyle),
    promptSystem: normalizePromptSystem(raw.promptSystem),
    sourceId: optionalString(raw.sourceId),
    sourceDisplayName: optionalString(
      raw.sourceDisplayName ?? raw.sourceFileName ?? raw.fileName,
    ),
    sourcePageCount: numberValue(raw.sourcePageCount ?? raw.pageCount),
    expectedUnitCount: numberValue(raw.expectedUnitCount, units.length),
    completedUnitCount: numberValue(
      raw.completedUnitCount,
      units.filter((unit) =>
        ["extracted", "confirmed"].includes(unit.status),
      ).length,
    ),
    failedUnitCount: numberValue(
      raw.failedUnitCount,
      units.filter((unit) => unit.status === "failed").length,
    ),
    units,
    finalCheckReady:
      typeof raw.finalCheckReady === "boolean"
        ? raw.finalCheckReady
        : undefined,
    warnings: normalizeWarnings(raw.warnings),
    blockingWarnings: normalizeWarnings(
      raw.blockingWarnings,
      "blocking",
    ),
    createdTemplates: created.length ? created : undefined,
    lastErrorCode: optionalString(raw.lastErrorCode),
    createdAt: optionalString(raw.createdAt),
    updatedAt: optionalString(raw.updatedAt),
    completedAt: optionalString(raw.completedAt),
    rowVersion: numberValue(raw.rowVersion ?? raw.revision),
  };
}

function normalizeTemplateGenerationBatchSummary(
  value: unknown,
): TemplateGenerationBatchSummary | undefined {
  const raw = isObject(value) ? value : {};
  const id = stringValue(raw.id ?? raw.batchId);
  if (!id) return undefined;
  return {
    id,
    status: normalizeBatchStatus(raw.status),
    testType: normalizeTestType(raw.testType),
    subject: normalizeSubject(raw.subject),
    answerStyle: normalizeAnswerStyle(raw.answerStyle),
    sourcePageCount: numberValue(raw.sourcePageCount ?? raw.pageCount),
    expectedUnitCount: numberValue(raw.expectedUnitCount),
    completedUnitCount: numberValue(raw.completedUnitCount),
    failedUnitCount: numberValue(raw.failedUnitCount),
    lastErrorCode: optionalString(raw.lastErrorCode),
    createdAt: optionalString(raw.createdAt),
    updatedAt: optionalString(raw.updatedAt),
    completedAt: optionalString(raw.completedAt),
    rowVersion: numberValue(raw.rowVersion ?? raw.revision),
    detailUrl: optionalString(raw.detailUrl),
  };
}

function summaryFromBatch(
  batch: TemplateGenerationBatch,
): TemplateGenerationBatchSummary {
  return {
    id: batch.batchId,
    status: batch.status,
    testType: batch.testType,
    subject: batch.subject,
    answerStyle: batch.answerStyle,
    sourcePageCount: batch.sourcePageCount,
    expectedUnitCount: batch.expectedUnitCount,
    completedUnitCount: batch.completedUnitCount ?? 0,
    failedUnitCount: batch.failedUnitCount ?? 0,
    lastErrorCode: batch.lastErrorCode,
    createdAt: batch.createdAt,
    updatedAt: batch.updatedAt,
    completedAt: batch.completedAt,
    rowVersion: batch.rowVersion,
  };
}

function normalizeUnit(value: unknown, index: number): TemplateGenerationUnit {
  const raw = isObject(value) ? value : {};
  const sequence = numberValue(raw.sequence, index + 1);
  const appliedRotations = arrayOfObjects(
    raw.appliedRotations ?? raw.rotations,
  ).map((rotation) => ({
    pageId: optionalString(rotation.pageId),
    pageNumber: optionalNumber(rotation.pageNumber),
    clockwiseDegrees: normalizeRotation(
      rotation.clockwiseDegrees ?? rotation.clockwiseDegreesToUpright,
    ),
  }));

  return {
    id: stringValue(raw.id ?? raw.unitId, String(sequence)),
    sequence,
    status: normalizeUnitStatus(raw.status),
    firstPage: numberValue(raw.firstPage, 1),
    lastPage: numberValue(raw.lastPage ?? raw.firstPage, 1),
    stepSetIndex: optionalNumber(raw.stepSetIndex),
    stepVariationIndex: optionalNumber(raw.stepVariationIndex),
    suffix: optionalString(raw.suffix),
    deterministicSuffix: optionalString(
      raw.deterministicSuffix ?? raw.suffix,
    ),
    printedTestName: optionalString(raw.printedTestName),
    userConfirmedBaseName: optionalString(raw.userConfirmedBaseName),
    confirmedBaseTestName: optionalString(
      raw.confirmedBaseTestName ?? raw.userConfirmedBaseName,
    ),
    finalTemplateName: optionalString(raw.finalTemplateName),
    filenameGrade: normalizeGrade(raw.filenameGrade),
    paperGrade: normalizeGrade(raw.paperGrade),
    resolvedGrade: normalizeGrade(raw.resolvedGrade),
    gradeEvidence: optionalString(raw.gradeEvidence),
    gradeConfirmedByUser:
      typeof raw.gradeConfirmedByUser === "boolean"
        ? raw.gradeConfirmedByUser
        : undefined,
    questionCount: optionalNumber(
      raw.questionCount ?? raw.extractedQuestionCount,
    ),
    orientationAttemptCount: optionalNumber(raw.orientationAttemptCount),
    appliedRotations: appliedRotations.length ? appliedRotations : undefined,
    orientationCorrectionSummary: optionalString(
      raw.orientationCorrectionSummary,
    ),
    warnings: normalizeWarnings(raw.warnings),
    blockingWarnings: normalizeWarnings(
      raw.blockingWarnings,
      "blocking",
    ),
    createdTemplateId: optionalString(raw.createdTemplateId),
    createdTemplateVersionId: optionalString(raw.createdTemplateVersionId),
    rowVersion: numberValue(raw.rowVersion ?? raw.revision),
  };
}

function normalizeCreatedTemplate(raw: JsonObject): CreatedTemplateLink {
  return {
    templateId: stringValue(raw.templateId ?? raw.id),
    versionId: stringValue(raw.versionId ?? raw.templateVersionId),
    title: stringValue(raw.title ?? raw.templateName, "作成したテンプレート"),
  };
}

function normalizeWarnings(
  value: unknown,
  fallbackSeverity: TemplateGenerationWarning["severity"] = "warning",
): TemplateGenerationWarning[] | undefined {
  if (!Array.isArray(value) || !value.length) return undefined;
  return value.map((warning) => {
    if (typeof warning === "string") {
      return {
        code: warning,
        severity:
          warning === "ORIENTATION_CORRECTED"
            ? "information"
            : fallbackSeverity,
      };
    }
    const raw = isObject(warning) ? warning : {};
    return {
      code: stringValue(raw.code, "TEMPLATE_EXTRACTION_FAILED"),
      severity: normalizeWarningSeverity(raw.severity, fallbackSeverity),
      message: optionalString(raw.message),
    };
  });
}

export function testTypeLabel(value: TemplateGenerationTestType) {
  switch (value) {
    case "hop":
      return "HOP";
    case "step":
      return "STEP";
    case "classPlacement":
      return "クラス分けテスト";
    case "other":
      return "その他";
  }
}

export function answerStyleLabel(value?: TemplateGenerationAnswerStyle | null) {
  return value === "fillBlank" ? "穴埋め" : "通常";
}

export function gradeLabel(value?: TemplateGenerationGradeLevel | null) {
  if (!value || value === "unknown") return "未設定";
  const match = /^grade([1-6])$/u.exec(value);
  return match ? `${match[1]}年生` : "未設定";
}

export function pageRangeLabel(
  unit: Pick<TemplateGenerationUnit, "firstPage" | "lastPage">,
) {
  return unit.firstPage === unit.lastPage
    ? `${unit.firstPage}ページ`
    : `${unit.firstPage}〜${unit.lastPage}ページ`;
}

export function deterministicPlanMessage(
  testType: TemplateGenerationTestType,
  unitCount: number,
) {
  switch (testType) {
    case "hop":
      return `1ページごとに分割し、${unitCount}件のテンプレートを生成します。`;
    case "step":
      return `2ページごとに分割し、3件を1セットとして -1 / -2 / -3 を付けます。${unitCount}件のテンプレートを生成します。`;
    case "classPlacement":
    case "other":
      return "PDF全体から1件のテンプレートを生成します。";
  }
}

export function warningMessage(code: string) {
  const messages: Record<string, string> = {
    STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX:
      "STEPのPDFは、ページ数が6の倍数である必要があります。",
    PDF_PAGE_COUNT_INVALID: "PDFに読み取れるページがありません。",
    ORIENTATION_RESPONSE_INVALID:
      "ページの向きの判定結果を確認できませんでした。",
    ORIENTATION_RETRY_EXHAUSTED:
      "向きを補正した後も用紙を読み取れませんでした。修正したPDFを再アップロードしてください。",
    TEST_NAME_REQUIRED: "テスト名を入力してください。",
    STEP_NAME_MISMATCH:
      "同じSTEPセットでテスト名が一致しません。共通の基本名を確認してください。",
    STEP_NAME_ALREADY_SUFFIXED:
      "読み取った名前に枝番が含まれています。枝番を除いた基本名を確認してください。",
    GRADE_REQUIRED: "学年を選択してください。",
    GRADE_CONFLICT:
      "ファイル名とテスト用紙の学年が一致しません。正しい学年を選択してください。",
    FILENAME_GRADE_CONFLICT:
      "ファイル名に複数の学年が含まれています。正しい学年を選択してください。",
    DUPLICATE_TEMPLATE_NAME:
      "同じ名前のテンプレートがあります。テスト名を変更してください。",
    KNOWN_TEST_NAME_IMMUTABLE:
      "HOP、STEP、クラス分けテストの名前は、教科・学年・分割番号から自動で決まります。",
    TEMPLATE_EXTRACTION_FAILED:
      "テンプレートの読み取りに失敗しました。",
    TEMPLATE_DRAFT_INVALID:
      "AIが生成した下書きの形式を確認できませんでした。入力内容ではなく生成結果の問題です。失敗した項目だけ再試行してください。",
    SOURCE_CHANGED:
      "計画後に元PDFが変更されました。もう一度アップロードしてください。",
    STALE_ROW_VERSION:
      "別の画面で内容が更新されました。最新の内容を読み直してください。",
    ORIENTATION_CORRECTED: "ページの向きを自動で補正しました。",
  };
  return messages[code] || "確認が必要な項目があります。";
}

export function unitStatusLabel(status: TemplateGenerationUnitStatus) {
  switch (status) {
    case "pending":
      return "待機中";
    case "queued":
      return "生成待ち";
    case "generating":
      return "テンプレートを生成しています";
    case "rotating":
      return "ページの向きを補正しています";
    case "retryingAfterRotation":
      return "補正後のテンプレートを生成しています";
    case "extracted":
      return "最終確認の準備完了";
    case "failed":
      return "生成に失敗";
    case "confirmed":
      return "テンプレート作成済み";
  }
}

export function isActiveBatchStatus(status: TemplateGenerationBatchStatus) {
  return ["validating", "generating", "confirming"].includes(status);
}

export function isResumableBatchStatus(
  status: TemplateGenerationBatchStatus,
) {
  return [
    "draft",
    "validating",
    "generating",
    "needsFinalCheck",
    "confirming",
    "failed",
  ].includes(status);
}

/**
 * Remembers only opaque batch IDs and a last-seen timestamp. Never persist
 * filenames, subjects, teacher identifiers, extracted content, or AI output.
 */
export function rememberTemplateGenerationBatchId(batchId: string) {
  if (!isSafeBatchId(batchId)) return;
  const now = new Date().toISOString();
  const entries = readRecentBatchEntries().filter((entry) => entry.id !== batchId);
  entries.unshift({ id: batchId, seenAt: now });
  writeRecentBatchEntries(entries.slice(0, RESUMABLE_BATCH_LIMIT));
}

export function forgetTemplateGenerationBatchId(batchId: string) {
  if (!isSafeBatchId(batchId)) return;
  writeRecentBatchEntries(
    readRecentBatchEntries().filter((entry) => entry.id !== batchId),
  );
}

export function recentTemplateGenerationBatchIds() {
  return readRecentBatchEntries().map((entry) => entry.id);
}

interface RecentBatchEntry {
  id: string;
  seenAt: string;
}

function readRecentBatchEntries(): RecentBatchEntry[] {
  try {
    const raw = window.localStorage.getItem(RECENT_BATCH_STORAGE_KEY);
    if (!raw) return [];
    const values = JSON.parse(raw) as unknown;
    if (!Array.isArray(values)) return [];
    const seen = new Set<string>();
    return values
      .map((value): RecentBatchEntry | undefined => {
        if (typeof value === "string") {
          return isSafeBatchId(value)
            ? { id: value, seenAt: "" }
            : undefined;
        }
        const entry = isObject(value) ? value : {};
        const id = stringValue(entry.id);
        return isSafeBatchId(id)
          ? { id, seenAt: optionalString(entry.seenAt) || "" }
          : undefined;
      })
      .filter((entry): entry is RecentBatchEntry => {
        if (!entry || seen.has(entry.id)) return false;
        seen.add(entry.id);
        return true;
      })
      .slice(0, RESUMABLE_BATCH_LIMIT);
  } catch {
    return [];
  }
}

function writeRecentBatchEntries(entries: RecentBatchEntry[]) {
  try {
    if (entries.length) {
      window.localStorage.setItem(
        RECENT_BATCH_STORAGE_KEY,
        JSON.stringify(entries),
      );
    } else {
      window.localStorage.removeItem(RECENT_BATCH_STORAGE_KEY);
    }
  } catch {
    // Storage can be unavailable in private/restricted browsing. The durable
    // server list remains authoritative when available.
  }
}

function isSafeBatchId(value: string) {
  return /^[A-Za-z0-9_-]{1,128}$/u.test(value);
}

function isAbortError(reason: unknown) {
  return reason instanceof DOMException && reason.name === "AbortError";
}

function normalizeBatchStatus(value: unknown): TemplateGenerationBatchStatus {
  const normalized = stringValue(value, "draft").replace(/_([a-z])/gu, (_, c) =>
    String(c).toUpperCase(),
  );
  const values: TemplateGenerationBatchStatus[] = [
    "draft",
    "validating",
    "generating",
    "needsFinalCheck",
    "confirming",
    "completed",
    "failed",
    "cancelled",
  ];
  return values.includes(normalized as TemplateGenerationBatchStatus)
    ? (normalized as TemplateGenerationBatchStatus)
    : "failed";
}

function normalizeUnitStatus(value: unknown): TemplateGenerationUnitStatus {
  const normalized = stringValue(value, "pending").replace(/_([a-z])/gu, (_, c) =>
    String(c).toUpperCase(),
  );
  const values: TemplateGenerationUnitStatus[] = [
    "pending",
    "queued",
    "generating",
    "rotating",
    "retryingAfterRotation",
    "extracted",
    "failed",
    "confirmed",
  ];
  return values.includes(normalized as TemplateGenerationUnitStatus)
    ? (normalized as TemplateGenerationUnitStatus)
    : "failed";
}

function normalizeTestType(value: unknown): TemplateGenerationTestType {
  const normalized = stringValue(value);
  return ["hop", "step", "classPlacement", "other"].includes(normalized)
    ? (normalized as TemplateGenerationTestType)
    : "other";
}

function normalizeSubject(value: unknown): TemplateGenerationSubject {
  const normalized = stringValue(value);
  return ["算数", "国語", "理科", "社会"].includes(normalized)
    ? (normalized as TemplateGenerationSubject)
    : "算数";
}

function normalizeAnswerStyle(
  value: unknown,
): TemplateGenerationAnswerStyle | null | undefined {
  if (value === null) return null;
  const normalized = stringValue(value);
  return ["normal", "fillBlank"].includes(normalized)
    ? (normalized as TemplateGenerationAnswerStyle)
    : undefined;
}

function normalizePromptSystem(
  value: unknown,
): TemplateGenerationBatch["promptSystem"] {
  const normalized = stringValue(value);
  return ["standard", "classPlacement", "fillBlank"].includes(normalized)
    ? (normalized as TemplateGenerationBatch["promptSystem"])
    : "standard";
}

function normalizeGrade(
  value: unknown,
): TemplateGenerationGradeLevel | null | undefined {
  if (value === null) return null;
  const normalized = stringValue(value);
  return [
    "unknown",
    "grade1",
    "grade2",
    "grade3",
    "grade4",
    "grade5",
    "grade6",
  ].includes(normalized)
    ? (normalized as TemplateGenerationGradeLevel)
    : undefined;
}

function normalizeWarningSeverity(
  value: unknown,
  fallback: TemplateGenerationWarning["severity"],
): TemplateGenerationWarning["severity"] {
  const normalized = stringValue(value);
  return ["information", "warning", "blocking"].includes(normalized)
    ? (normalized as TemplateGenerationWarning["severity"])
    : fallback;
}

function normalizeRotation(value: unknown): 0 | 90 | 180 | 270 {
  const number = numberValue(value);
  return [0, 90, 180, 270].includes(number)
    ? (number as 0 | 90 | 180 | 270)
    : 0;
}

function isObject(value: unknown): value is JsonObject {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function arrayOfObjects(value: unknown): JsonObject[] {
  return Array.isArray(value) ? value.filter(isObject) : [];
}

function stringValue(value: unknown, fallback = "") {
  return typeof value === "string" ? value : fallback;
}

function optionalString(value: unknown) {
  return typeof value === "string" ? value : undefined;
}

function numberValue(value: unknown, fallback = 0) {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function optionalNumber(value: unknown) {
  return typeof value === "number" && Number.isFinite(value)
    ? value
    : undefined;
}

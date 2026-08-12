export type StaffRole =
  | "administrator"
  | "teacher"
  | "scanOperator"
  | "readOnlyReviewer";

export type AiProvider = "geminiDirect" | "openRouter";

export interface SessionUser {
  id: string;
  username: string;
  displayName: string;
  roles: StaffRole[];
  schoolName?: string;
  environmentName?: string;
  sessionExpiresAt?: string;
  mustChangePassword?: boolean;
}

export interface PagedResponse<T> {
  items: T[];
  nextCursor: string | null;
  totalApproximate?: number;
  /** Optional server-authoritative values for open exact-match filters. */
  facets?:
    | Record<
        string,
        Array<string | { value: string; label?: string; count?: number }>
      >
    | null;
}

export interface FieldProblem {
  field?: string;
  code?: string;
  message: string;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  code?: string;
  detail?: string;
  instance?: string;
  correlationId?: string;
  errors?: FieldProblem[];
  uploadId?: string;
  existingSubmissionId?: string;
  existingAttemptNumber?: number;
  nextAttemptNumber?: number;
  allowedActions?: string[];
  allowedResolutions?: string[];
}

export type SubmissionState =
  | "uploading"
  | "validating"
  | "preprocessing"
  | "awaiting_ai"
  | "awaitingAi"
  | "gemini_batch_running"
  | "geminiBatchRunning"
  | "openrouter_queued"
  | "openRouterQueued"
  | "budget_blocked"
  | "budgetBlocked"
  | "needs_attention"
  | "needsAttention"
  | "needs_name_review"
  | "needsNameReview"
  | "needs_grade_review"
  | "needsGradeReview"
  | "ready_for_review"
  | "readyForReview"
  | "ready_to_finalize"
  | "readyToFinalize"
  | "finalized"
  | "failed"
  | "scan_deleted"
  | "scanDeleted"
  | string;

export interface ReviewCounts {
  needsNameReview: number;
  needsGradeReview: number;
  readyToFinalize: number;
  inProgress?: number;
  finalizedToday?: number;
}

export interface StudentSummary {
  id: string;
  studentNumber: string;
  displayName: string;
  familyName?: string;
  givenName?: string;
  familyNameKana?: string;
  givenNameKana?: string;
  kana?: string;
  gradeLabel?: string;
  classLabel?: string;
  course?: string;
  enrollmentStatus?: "active" | "inactive" | string;
  active?: boolean;
  lastFinalizedTestDate?: string | null;
  revision?: number;
}

export interface StudentAlias {
  id: string;
  text: string;
  aliasType?: string;
  normalizedText?: string;
}

export interface StudentDetail extends StudentSummary {
  schoolLabel?: string;
  notes?: string;
  aliases?: StudentAlias[];
  createdAt?: string;
  updatedAt?: string;
}

export interface ProgressPoint {
  submissionId: string;
  testDate: string;
  testTitle: string;
  earnedPointsMilli: number;
  possiblePointsMilli: number;
  percentageBasisPoints: number;
  correct: number;
  partial: number;
  incorrect: number;
  blank: number;
  resultRevision: number;
}

export interface StudentProgress {
  student: Pick<StudentSummary, "id" | "displayName">;
  range: {
    from: string;
    to: string;
    timeZone: string;
  };
  series: ProgressPoint[];
}

export type TemplateLifecycle = "draft" | "active" | "retired" | "archived" | string;

export interface TemplateSummary {
  id: string;
  title: string;
  subject?: string;
  category?: string;
  gradeLabel?: string;
  course?: string;
  testType?: "hop" | "step" | "classPlacement" | "other" | string;
  lifecycleState: TemplateLifecycle;
  activeVersionId?: string | null;
  activeVersionNumber?: number | null;
  questionCount?: number;
  totalPointsMilli?: number;
  defaultPointsMilli?: number;
  updatedAt?: string;
  revision?: number;
}

export interface AnswerVariant {
  id?: string;
  text: string;
  variantType?: "canonical" | "accepted" | "explicitException" | string;
  provenance?: "provided_model_answer" | "ai_proposed" | "teacher_entered" | string;
  teacherVerified?: boolean;
  sourceFileReferenceId?: string;
  sourcePageNumber?: number;
  sourceRegionId?: string;
  revision?: number;
}

export interface PageRegion {
  pageNumber: number;
  xMillionths: number;
  yMillionths: number;
  widthMillionths: number;
  heightMillionths: number;
  rotationDegrees?: number;
}

export interface TemplateQuestion {
  id: string;
  displayLabel: string;
  order: number;
  questionText: string;
  questionType: string;
  gradingMode: string;
  maxPointsMilli: number;
  pointIncrementMilli: number;
  allowNonKanji: boolean;
  requiresCompleteAnswer: boolean;
  answerOrderInsensitive: boolean;
  acceptedAnswers: AnswerVariant[];
  canonicalAnswer?: string;
  rubric?: string;
  teacherNote?: string;
  requiresReviewAlways: boolean;
  answerRegion?: PageRegion;
  questionRegion?: PageRegion;
  proposalState?: "proposed" | "accepted" | "edited" | "ignored";
  answerProvenance?: "provided_model_answer" | "ai_proposed" | "teacher_entered";
  proposalConfidence?: string;
  warnings?: string[];
  teacherVerified?: boolean;
  revision?: number;
}

export interface TemplatePage {
  id: string;
  pageNumber: number;
  thumbnailUrl?: string;
  imageUrl?: string;
  sourceRole?:
    | "blankTest"
    | "containsModelAnswers"
    | "containsNonModelAnswers"
    | "separateAnswerKey";
}

export interface TemplateSource {
  id: string;
  sourceRole:
    | "blankTest"
    | "containsModelAnswers"
    | "containsNonModelAnswers"
    | "separateAnswerKey";
  displayName: string;
  uploadId?: string;
  contentUrl?: string;
  mimeType?: string;
}

export interface TemplateVersionDetail {
  id: string;
  templateId: string;
  versionNumber: number;
  state: "draft" | "published" | string;
  title?: string;
  totalPointsMilli?: number;
  defaultPointsMilli?: number;
  questions?: TemplateQuestion[];
  pages?: TemplatePage[];
  sources?: TemplateSource[];
  blockingWarnings?: string[];
  nonBlockingWarnings?: string[];
  updatedAt?: string;
  revision?: number;
}

export interface ValidationIssue {
  code: string;
  message: string;
  questionId?: string;
  blocking: boolean;
}

export interface TemplateValidation {
  valid: boolean;
  pageCount: number;
  questionCount: number;
  totalPointsMilli: number;
  kanjiRequiredCount: number;
  alwaysReviewCount: number;
  issues: ValidationIssue[];
}

export interface TestSessionSummary {
  id: string;
  name?: string | null;
  sessionName?: string | null;
  title?: string | null;
  templateId: string;
  templateVersionId: string;
  templateTitle?: string | null;
  templateVersionNumber?: number;
  subject?: string | null;
  gradeLabel?: string | null;
  category?: string | null;
  testDate: string;
  classLabel?: string | null;
  course?: string | null;
  templateCourse?: string | null;
  priority: "economy" | "expedite";
  state: "draft" | "open" | "closed" | "archived" | string;
  creationSource?: "template_publish" | "manual" | string;
  expectedStudentCount?: number;
  submissionCount?: number;
  finalizedCount?: number;
  attentionCount?: number;
  /** Number of ordered one-page scans that form one student's submission. */
  expectedSubmissionPageCount?: number | null;
  revision?: number;
}

export interface SubmissionSummary {
  id: string;
  testSessionId?: string;
  fileName?: string;
  studentId?: string | null;
  studentDisplayName?: string | null;
  studentNumber?: string | null;
  state: SubmissionState;
  scanPayloadState?: "scan_available" | "deletion_pending" | "scan_deleted";
  scanDeletedAt?: string;
  scanDeletionReason?: string;
  pageCount?: number;
  progressPercent?: number;
  qualityWarnings?: string[];
  attemptNumber?: number;
  canonicalForSession?: boolean;
  totalEarnedPointsMilli?: number;
  totalPossiblePointsMilli?: number;
  exportState?: string;
  uploadedAt?: string;
  updatedAt?: string;
  revision?: number;
}

export interface UploadCreateResponse {
  uploadId: string;
  state: string;
  offset: number;
  maxChunkBytes: number;
  expiresAt: string;
  chunkUrl: string;
}

export interface UploadFinalizeResponse {
  uploadId: string;
  state: string;
  submissionId?: string;
  orderedScanItemId?: string;
  jobId?: string;
  statusUrl?: string;
  pageCount?: number;
  rowVersion?: number;
  revision?: number;
}

export type OrderedScanBatchStatus =
  | "draft"
  | "processing"
  | "needsReview"
  | "completed"
  | "failed"
  | "cancelled"
  | "expired"
  | string;

export interface OrderedScanBatchItem {
  id: string;
  uploadId?: string | null;
  clientItemId: string;
  fileName: string;
  inputOrdinal: number;
  status: string;
  detectedTemplatePageNumber?: number | null;
  classificationConfidenceBasisPoints?: number | null;
  groupOrdinal?: number | null;
  submissionId?: string | null;
  submissionPageNumber?: number | null;
  issueCode?: string | null;
  rowVersion: number;
}

export interface OrderedScanBatchIssue {
  code: string;
  message: string;
  inputOrdinal?: number | null;
  groupOrdinal?: number | null;
}

export interface OrderedScanBatchGroup {
  groupOrdinal: number;
  status: string;
  itemIds: string[];
  submissionId?: string | null;
}

export interface OrderedScanBatchDetail {
  id: string;
  testSessionId: string;
  expectedPageCount: number;
  status: OrderedScanBatchStatus;
  assemblyPolicyVersion: string;
  planHash?: string | null;
  lastErrorCode?: string | null;
  rowVersion: number;
  expiresAt: string;
  itemCount: number;
  items: OrderedScanBatchItem[];
  groups: OrderedScanBatchGroup[];
  submissionIds: string[];
  issues: OrderedScanBatchIssue[];
}

export interface CreateOrderedScanBatchRequest {
  items: Array<
    Pick<OrderedScanBatchItem, "clientItemId" | "fileName" | "inputOrdinal">
  >;
}

export type TemplateGenerationTestType =
  | "hop"
  | "step"
  | "classPlacement"
  | "other";

export type TemplateGenerationSubject = "算数" | "国語" | "理科" | "社会";

export type TemplateGenerationAnswerStyle = "normal" | "fillBlank";

export type TemplateGenerationPromptSystem =
  | "standard"
  | "classPlacement"
  | "fillBlank";

export type TemplateGenerationGradeLevel =
  | "unknown"
  | "grade1"
  | "grade2"
  | "grade3"
  | "grade4"
  | "grade5"
  | "grade6";

export type TemplateGenerationBatchStatus =
  | "draft"
  | "validating"
  | "generating"
  | "needsFinalCheck"
  | "confirming"
  | "completed"
  | "failed"
  | "cancelled";

export type TemplateGenerationUnitStatus =
  | "pending"
  | "queued"
  | "generating"
  | "rotating"
  | "retryingAfterRotation"
  | "extracted"
  | "failed"
  | "confirmed";

export type TemplateGenerationWarningSeverity =
  | "information"
  | "warning"
  | "blocking";

export interface TemplateGenerationWarning {
  code: string;
  severity: TemplateGenerationWarningSeverity;
  message?: string;
}

export interface AppliedTemplatePageRotation {
  pageId?: string;
  pageNumber?: number;
  clockwiseDegrees: 0 | 90 | 180 | 270;
}

export interface TemplateGenerationUnit {
  id: string;
  sequence: number;
  status: TemplateGenerationUnitStatus;
  firstPage: number;
  lastPage: number;
  stepSetIndex?: number | null;
  stepVariationIndex?: number | null;
  suffix?: string | null;
  deterministicSuffix?: string | null;
  printedTestName?: string | null;
  userConfirmedBaseName?: string | null;
  confirmedBaseTestName?: string | null;
  finalTemplateName?: string | null;
  filenameGrade?: TemplateGenerationGradeLevel | null;
  paperGrade?: TemplateGenerationGradeLevel | null;
  resolvedGrade?: TemplateGenerationGradeLevel | null;
  gradeEvidence?: string | null;
  gradeConfirmedByUser?: boolean;
  questionCount?: number;
  orientationAttemptCount?: number;
  appliedRotations?: AppliedTemplatePageRotation[];
  orientationCorrectionSummary?: string | null;
  warnings?: TemplateGenerationWarning[];
  blockingWarnings?: Array<TemplateGenerationWarning | string>;
  createdTemplateId?: string | null;
  createdTemplateVersionId?: string | null;
  rowVersion: number;
}

export interface CreatedTemplateLink {
  templateId: string;
  versionId: string;
  title: string;
}

export interface TemplateGenerationBatch {
  batchId: string;
  status: TemplateGenerationBatchStatus;
  testType: TemplateGenerationTestType;
  subject: TemplateGenerationSubject;
  answerStyle?: TemplateGenerationAnswerStyle | null;
  promptSystem: TemplateGenerationPromptSystem;
  sourceId?: string;
  sourceDisplayName?: string | null;
  sourcePageCount: number;
  expectedUnitCount: number;
  completedUnitCount?: number;
  failedUnitCount?: number;
  units: TemplateGenerationUnit[];
  finalCheckReady?: boolean;
  warnings?: TemplateGenerationWarning[];
  blockingWarnings?: Array<TemplateGenerationWarning | string>;
  createdTemplates?: CreatedTemplateLink[];
  lastErrorCode?: string | null;
  createdAt?: string | null;
  updatedAt?: string | null;
  completedAt?: string | null;
  rowVersion: number;
}

/**
 * PII-minimized row returned by the resumable generation list. The source
 * filename and upload identifier deliberately stay on the detail endpoint.
 */
export interface TemplateGenerationBatchSummary {
  id: string;
  status: TemplateGenerationBatchStatus;
  testType: TemplateGenerationTestType;
  subject: TemplateGenerationSubject;
  answerStyle?: TemplateGenerationAnswerStyle | null;
  sourcePageCount: number;
  expectedUnitCount: number;
  completedUnitCount: number;
  failedUnitCount: number;
  lastErrorCode?: string | null;
  createdAt?: string | null;
  updatedAt?: string | null;
  completedAt?: string | null;
  rowVersion: number;
  detailUrl?: string | null;
}

export interface ResumableTemplateGenerationBatchList {
  items: TemplateGenerationBatchSummary[];
  limit: number;
  /** True when the host list endpoint was unavailable and browser recovery was used. */
  browserRecoveryOnly?: boolean;
}

export interface CreateTemplateGenerationBatchRequest {
  sourceId: string;
  testType: TemplateGenerationTestType;
  subject: TemplateGenerationSubject;
  answerStyle: TemplateGenerationAnswerStyle | null;
  expectedSourceRowVersion?: number;
}

export interface NameCandidate {
  studentId: string;
  displayName: string;
  kana?: string;
  studentNumber?: string;
  classLabel?: string;
  rank?: number;
  evidence?: string[];
  confidenceLabel?: string;
}

export interface NameReviewItem {
  id: string;
  submissionId: string;
  sourceRevision: number;
  transcription?: string;
  nameCropUrl?: string;
  studentNumberCropUrl?: string;
  candidates: NameCandidate[];
  qualityWarnings?: string[];
}

export interface GradeReviewItem {
  id: string;
  submissionId: string;
  resultId: string;
  sourceResultRevision: number;
  studentDisplayName?: string;
  testTitle: string;
  testDate?: string;
  questionId: string;
  questionLabel: string;
  questionText: string;
  expectedAnswers?: string[];
  transcription?: string;
  answerCropUrl?: string;
  proposedOutcome: string;
  proposedPointsMilli: number;
  maxPointsMilli: number;
  pointIncrementMilli: number;
  reason?: string;
  kanjiRequired?: boolean;
  warning?: string;
  qualityWarnings?: string[];
}

export interface SubmissionGradingWorkspace {
  submission: {
    id: string;
    state: SubmissionState;
    revision: number;
    fileName?: string | null;
    uploadedAt?: string | null;
    pageCount: number;
    scanPayloadState?: string | null;
    scanDeletedAt?: string | null;
    scanDeletionReason?: string | null;
    finalizedAt?: string | null;
  };
  session: {
    id: string;
    state: string;
    testDate?: string | null;
    classLabel?: string | null;
  };
  test: {
    templateVersionId: string;
    templateVersionNumber?: number | null;
    title: string;
    subject?: string | null;
    gradeLabel?: string | null;
    category?: string | null;
    course?: string | null;
  };
  student: {
    id: string;
    displayName: string;
    studentNumber?: string | null;
    schoolClass?: string | null;
    course?: string | null;
    gradeLabel?: string | null;
  } | null;
  gradingRun: {
    id: string;
    state: string;
    resultSourceRevision: number;
    earnedPointsMilli: number;
    possiblePointsMilli: number;
  } | null;
  originalPdf: {
    available: boolean;
    url?: string | null;
    contentType?: string | null;
  } | null;
  pages: SubmissionGradingPage[];
  results: SubmissionGradingResult[];
  unresolvedSnapshot: SubmissionGradingSnapshotItem[];
  bulkConfirmationLimit: number;
  canBulkConfirm: boolean;
  canFinalize: boolean;
}

export interface SubmissionGradingPage {
  id: string;
  pageNumber: number;
  widthPixels?: number | null;
  heightPixels?: number | null;
  rotationDegrees?: number | null;
  qualityState?: string | null;
  available: boolean;
  contentUrl?: string | null;
  thumbnailUrl?: string | null;
}

export interface SubmissionGradingResult {
  resultId: string;
  questionId: string;
  orderIndex: number;
  displayLabel: string;
  questionText: string;
  questionType: string;
  gradingMode: string;
  pageNumbers: number[];
  expectedAnswers: string[];
  transcription?: string | null;
  outcome: string;
  awardedPointsMilli: number;
  maxPointsMilli: number;
  pointIncrementMilli: number;
  reason?: string | null;
  explanation?: string | null;
  confidenceBasisPoints?: number | null;
  kanjiRequired: boolean;
  requiresCompleteAnswer: boolean;
  answerOrderInsensitive: boolean;
  reviewRequired: boolean;
  reviewStatus: string;
  sourceResultRevision: number;
}

export interface SubmissionGradingSnapshotItem {
  resultId: string;
  sourceResultRevision: number;
}

export interface SubmissionBulkConfirmResponse {
  confirmed: Array<{
    resultId: string;
    code: "RESULT_CONFIRMED" | string;
    sourceResultRevision: number;
  }>;
  skipped: Array<{
    resultId: string;
    code: string;
    sourceResultRevision: number;
  }>;
  gradingRun: {
    id: string;
    state: string;
    resultSourceRevision: number;
    earnedPointsMilli: number;
    possiblePointsMilli: number;
  };
  submission: {
    id?: string;
    state: SubmissionState;
    revision: number;
  };
  canFinalize: boolean;
}

export interface ResultQuestion {
  id: string;
  displayLabel: string;
  questionText: string;
  expectedAnswer?: string;
  transcription?: string;
  awardedPointsMilli: number;
  maxPointsMilli: number;
  pointIncrementMilli: number;
  outcome: string;
  reason?: string;
  kanjiRuleOutcome?: string;
  cropAvailable?: boolean;
  cropUrl?: string;
  overridden?: boolean;
}

export interface ResultDetail {
  submissionId: string;
  resultRevision: number;
  student?: Pick<StudentSummary, "id" | "displayName" | "studentNumber"> | null;
  testTitle: string;
  testDate: string;
  templateVersionNumber?: number;
  earnedPointsMilli: number;
  possiblePointsMilli: number;
  percentageBasisPoints: number;
  status: "finalized" | "reopened" | string;
  scanAvailable: boolean;
  scanDeletedAt?: string;
  scanDeletionReason?: string;
  questions: ResultQuestion[];
  finalizedAt?: string;
}

export interface ExportStatus {
  id: string;
  state: "queued" | "rendering" | "verified" | "failed" | string;
  revision?: number;
  createdAt?: string;
  fileUrl?: string;
  sha256?: string;
}

export interface RuntimeCapabilities {
  reports: {
    pdfExport: boolean;
  };
  ai: {
    provider: AiProvider;
    modelId: string;
    templateGeneration: RuntimeFeatureCapability;
    nameTranscription: RuntimeFeatureCapability;
    semanticGrading: RuntimeFeatureCapability;
    geminiBatch: RuntimeFeatureCapability;
    openRouterEnabled: boolean;
  };
  safety: {
    automaticAssignment: boolean;
    automaticFinalization: boolean;
  };
}

export interface RuntimeFeatureCapability {
  enabled: boolean;
  ready: boolean;
}

export interface HealthComponent {
  name: string;
  displayName?: string;
  state: "healthy" | "degraded" | "unavailable" | "unknown" | string;
  detail?: string;
  checkedAt?: string;
}

export interface AdminHealth {
  overallState: "healthy" | "degraded" | "unavailable" | "unknown" | string;
  components: HealthComponent[];
  maintenanceMode?: boolean;
  currentModel?: string;
  certificateExpiresAt?: string;
  lastBackupAt?: string | null;
  lastCleanupAt?: string | null;
}

export interface AdminStorage {
  managedBytes: number;
  quotaBytes: number;
  warningBytes?: number;
  proactiveCleanupBytes?: number;
  lowWaterBytes?: number;
  physicalFreeBytes: number;
  physicalTotalBytes?: number;
  originalsBytes?: number;
  derivativesBytes?: number;
  templatesBytes?: number;
  reportsBytes?: number;
  logsBytes?: number;
  temporaryBytes?: number;
  quarantineBytes?: number;
  oldestRetainedAt?: string | null;
  nextCleanupAt?: string | null;
  lastDeletionCount?: number;
}

export interface DurableJob {
  id: string;
  jobType: string;
  state: string;
  attempt: number;
  createdAt?: string;
  nextAttemptAt?: string;
  sanitizedError?: string;
}

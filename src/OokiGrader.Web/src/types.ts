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
  name?: string;
  sessionName?: string;
  templateId: string;
  templateVersionId: string;
  templateTitle?: string;
  templateVersionNumber?: number;
  testDate: string;
  classLabel?: string;
  course?: string;
  priority: "economy" | "expedite";
  state: "draft" | "open" | "closed" | "archived" | string;
  expectedStudentCount?: number;
  submissionCount?: number;
  finalizedCount?: number;
  attentionCount?: number;
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
  jobId?: string;
  statusUrl?: string;
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

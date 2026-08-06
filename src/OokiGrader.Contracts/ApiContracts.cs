namespace OokiGrader.Contracts;

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record DashboardResponse(
    DashboardCounts Counts,
    IReadOnlyList<DashboardAction> Actions,
    SystemStatusSummary System,
    DateTimeOffset GeneratedAt);

public sealed record DashboardCounts(
    int AwaitingNameReview,
    int AwaitingGradeReview,
    int ReadyToFinalize,
    int Processing,
    int Failed);

public sealed record DashboardAction(
    string Id,
    string Kind,
    string Title,
    string Detail,
    string Href,
    string Severity,
    DateTimeOffset UpdatedAt);

public sealed record SystemStatusSummary(
    string Readiness,
    string Provider,
    long ManagedBytes,
    long WarningBytes,
    long TargetBytes,
    long HardLimitBytes,
    DateTimeOffset? LastBackupAt);

public sealed record StudentSummary(
    string Id,
    string StudentNumber,
    string FamilyName,
    string GivenName,
    string FamilyNameKana,
    string GivenNameKana,
    string DisplayName,
    string? GradeLabel,
    string? Course,
    bool IsActive,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record StudentDetail(
    StudentSummary Student,
    IReadOnlyList<StudentAliasResponse> Aliases,
    IReadOnlyList<ResultSummary> RecentResults);

public sealed record StudentAliasResponse(
    string Id,
    string Value,
    string Kind,
    string NormalizedValue);

public sealed record CreateStudentRequest(
    string StudentNumber,
    string FamilyName,
    string GivenName,
    string FamilyNameKana,
    string GivenNameKana,
    string? DisplayName,
    string? GradeLabel,
    string? Course,
    string? SchoolClass,
    string? Notes);

public sealed record UpdateStudentRequest(
    string StudentNumber,
    string FamilyName,
    string GivenName,
    string FamilyNameKana,
    string GivenNameKana,
    string DisplayName,
    string? GradeLabel,
    string? Course,
    string? SchoolClass,
    string? Notes,
    long Revision);

public sealed record CreateAliasRequest(string Value, string Kind);

public sealed record TemplateSummary(
    string Id,
    string Title,
    string Subject,
    string? GradeLabel,
    string? Course,
    string? Category,
    string State,
    int VersionCount,
    int? ActiveVersionNumber,
    DateTimeOffset UpdatedAt);

public sealed record TemplateVersionSummary(
    string Id,
    string TemplateId,
    int VersionNumber,
    string State,
    int QuestionCount,
    long PossiblePointsMilli,
    string? ContentHash,
    long Revision,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);

public sealed record TemplateDetail(
    TemplateSummary Template,
    IReadOnlyList<TemplateVersionSummary> Versions);

public sealed record TemplateVersionDetail(
    TemplateVersionSummary Version,
    IReadOnlyList<QuestionResponse> Questions,
    IReadOnlyList<TemplateSourceResponse> Sources,
    IReadOnlyList<ValidationIssueResponse> ValidationIssues);

public sealed record CreateTemplateRequest(
    string Title,
    string Subject,
    string? GradeLabel,
    string? Course,
    string? Category,
    string? Notes,
    long DefaultPointsMilli);

public sealed record CreateTemplateVersionRequest(string? CloneFromVersionId);

public sealed record TemplateSourceResponse(
    string Id,
    string SourceRole,
    string DisplayName,
    string? UploadId,
    DateTimeOffset CreatedAt);

public sealed record QuestionResponse(
    string Id,
    string DisplayLabel,
    int SortOrder,
    string QuestionText,
    string QuestionType,
    string GradingMode,
    long MaxPointsMilli,
    bool AllowNonKanji,
    bool RequiresReviewAlways,
    long Revision,
    IReadOnlyList<AcceptedAnswerResponse> AcceptedAnswers);

public sealed record AcceptedAnswerResponse(
    string Id,
    string Text,
    string VariantType,
    string Provenance,
    bool IsExplicitNonKanjiException);

public sealed record UpsertQuestionRequest(
    string DisplayLabel,
    int SortOrder,
    string QuestionText,
    string QuestionType,
    string GradingMode,
    long MaxPointsMilli,
    bool AllowNonKanji,
    bool RequiresReviewAlways,
    IReadOnlyList<UpsertAcceptedAnswerRequest> AcceptedAnswers,
    long? Revision);

public sealed record UpsertAcceptedAnswerRequest(
    string Text,
    string VariantType,
    string Provenance,
    bool IsExplicitNonKanjiException);

public sealed record ValidationIssueResponse(
    string Code,
    string Severity,
    string Message,
    string? QuestionId);

public sealed record PublishTemplateVersionRequest(long Revision);

public sealed record TestSessionSummary(
    string Id,
    string Title,
    string TemplateId,
    string TemplateVersionId,
    string TemplateTitle,
    DateOnly TestDate,
    string State,
    string Priority,
    int SubmissionCount,
    int FinalizedCount,
    int ReviewCount,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record CreateTestSessionRequest(
    string Title,
    string TemplateVersionId,
    DateOnly TestDate,
    string? GradeLabel,
    string? Course,
    string Priority);

public sealed record SubmissionSummary(
    string Id,
    string TestSessionId,
    string? StudentId,
    string? StudentDisplayName,
    string FileName,
    string ProcessingState,
    string IdentityState,
    string GradingState,
    string RetentionState,
    string Priority,
    long? EarnedPointsMilli,
    long? PossiblePointsMilli,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record CreateUploadRequest(
    string Purpose,
    string? TestSessionId,
    string FileName,
    string DeclaredMimeType,
    long Length,
    string? ExpectedSha256);

public sealed record UploadStatusResponse(
    string UploadId,
    string State,
    long Offset,
    long Length,
    int MaxChunkBytes,
    DateTimeOffset ExpiresAt,
    string ChunkUrl,
    string? SubmissionId,
    string? JobId);

public sealed record ReviewCountsResponse(
    int Name,
    int Grading,
    int ReadyToFinalize);

public sealed record GradingReviewItem(
    string Id,
    string SubmissionId,
    string QuestionId,
    string QuestionLabel,
    string QuestionText,
    string? StudentDisplayName,
    string? Transcription,
    string Outcome,
    long AwardedPointsMilli,
    long MaxPointsMilli,
    string Method,
    string ReviewReason,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record OverrideResultRequest(
    long SourceResultRevision,
    long AwardedPointsMilli,
    string Outcome,
    string? TranscriptionCorrection,
    string ReasonCode,
    string? Note);

public sealed record FinalizeSubmissionRequest(long SourceRevision);

public sealed record ResultSummary(
    string SubmissionId,
    string TestTitle,
    DateOnly TestDate,
    long EarnedPointsMilli,
    long PossiblePointsMilli,
    int PercentageBasisPoints,
    int ResultRevision,
    bool ScanAvailable);

public sealed record ProgressResponse(
    StudentIdentityResponse Student,
    DateRangeResponse Range,
    IReadOnlyList<ProgressPointResponse> Series);

public sealed record StudentIdentityResponse(string Id, string DisplayName);

public sealed record DateRangeResponse(DateOnly From, DateOnly To, string TimeZone);

public sealed record ProgressPointResponse(
    string SubmissionId,
    DateOnly TestDate,
    string TestTitle,
    long EarnedPointsMilli,
    long PossiblePointsMilli,
    int PercentageBasisPoints,
    int Correct,
    int Partial,
    int Incorrect,
    int Blank,
    int ResultRevision);

public sealed record SiteSettingsResponse(
    string SchoolName,
    string TimeZone,
    string Locale,
    bool MaintenanceMode,
    long ManagedBytes,
    long WarningBytes,
    long TargetBytes,
    long HardLimitBytes,
    long PhysicalReserveBytes,
    long Revision);

public sealed record ComponentHealthResponse(
    string Name,
    string State,
    string Message,
    DateTimeOffset CheckedAt);

public sealed record AdminHealthResponse(
    string Overall,
    IReadOnlyList<ComponentHealthResponse> Components,
    DateTimeOffset CheckedAt);

public sealed record CurrentUserResponse(
    string Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    DateTimeOffset ExpiresAt);

public sealed record LoginRequest(string Username, string Password);

public sealed record BootstrapStatusResponse(
    bool Completed,
    bool HostLocal,
    DateTimeOffset? TokenExpiresAt);

public sealed record CompleteBootstrapRequest(
    string Token,
    string Username,
    string DisplayName,
    string Password,
    string SchoolName);

public sealed record CsrfTokenResponse(string Token);

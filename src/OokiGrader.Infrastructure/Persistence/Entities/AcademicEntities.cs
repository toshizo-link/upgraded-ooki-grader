namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class StudentEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string StudentNumber { get; set; } = string.Empty;
    public string StudentNumberNormalized { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string GivenName { get; set; } = string.Empty;
    public string? FamilyNameKana { get; set; }
    public string? GivenNameKana { get; set; }
    public string FamilyNameNormalized { get; set; } = string.Empty;
    public string GivenNameNormalized { get; set; } = string.Empty;
    public string? FamilyNameKanaNormalized { get; set; }
    public string? GivenNameKanaNormalized { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? SchoolClass { get; set; }
    public string? Course { get; set; }
    public string? GradeLabel { get; set; }
    public string Status { get; set; } = "active";
    public string? MergedIntoStudentId { get; set; }
    public string? PrivateNotes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<StudentAliasEntity> Aliases { get; } = new List<StudentAliasEntity>();
}

public sealed class StudentAliasEntity
{
    public string Id { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string AliasType { get; set; } = string.Empty;
    public string DisplayValue { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public bool RecognitionEnabled { get; set; } = true;
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public StudentEntity Student { get; set; } = null!;
}

public sealed class TestTemplateEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string? Category { get; set; }
    public string? Course { get; set; }
    public string? GradeLabel { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public long DefaultPointsMilli { get; set; } = 1_000;
    public string State { get; set; } = "draft";
    public string? ActiveVersionId { get; set; }
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<TemplateVersionEntity> Versions { get; } = new List<TemplateVersionEntity>();
}

public sealed class TemplateVersionEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string TestTemplateId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string State { get; set; } = "draft";
    public string? BasedOnVersionId { get; set; }
    public long? TargetTotalPointsMilli { get; set; }
    public long DefaultPointsMilli { get; set; } = 1_000;
    public bool DefaultAllowNonKanji { get; set; }
    public string PipelineVersion { get; set; } = string.Empty;
    public string? AiGenerationProvenanceId { get; set; }
    public string? PublishedByStaffUserId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? ContentHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public TestTemplateEntity TestTemplate { get; set; } = null!;
    public ICollection<TemplateSourceEntity> Sources { get; } = new List<TemplateSourceEntity>();
    public ICollection<QuestionEntity> Questions { get; } = new List<QuestionEntity>();
}

public sealed class TemplateSourceEntity
{
    public string Id { get; set; } = string.Empty;
    public string TemplateVersionId { get; set; } = string.Empty;
    public string UploadSessionId { get; set; } = string.Empty;
    public string? FileReferenceId { get; set; }
    public string SourceRole { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string UploadedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public TemplateVersionEntity TemplateVersion { get; set; } = null!;
    public UploadSessionEntity UploadSession { get; set; } = null!;
}

public sealed class QuestionEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string TemplateVersionId { get; set; } = string.Empty;
    public string LogicalQuestionId { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string DisplayLabel { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public string QuestionType { get; set; } = string.Empty;
    public string GradingMode { get; set; } = string.Empty;
    public long MaxPointsMilli { get; set; }
    public long PointIncrementMilli { get; set; } = 1;
    public bool AllowNonKanji { get; set; }
    public string? KanjiPolicyNote { get; set; }
    public string? RubricText { get; set; }
    public string? TeacherNote { get; set; }
    public string? QuestionRegionId { get; set; }
    public string? AnswerRegionId { get; set; }
    public bool RequiresReviewAlways { get; set; }
    public int? AiConfidenceBasisPoints { get; set; }
    public bool TeacherVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public TemplateVersionEntity TemplateVersion { get; set; } = null!;
    public RegionEntity? QuestionRegion { get; set; }
    public RegionEntity? AnswerRegion { get; set; }
    public ICollection<AcceptedAnswerEntity> AcceptedAnswers { get; } =
        new List<AcceptedAnswerEntity>();
}

public sealed class RegionEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string OwnerType { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string RegionType { get; set; } = string.Empty;
    public int XMillionths { get; set; }
    public int YMillionths { get; set; }
    public int WidthMillionths { get; set; }
    public int HeightMillionths { get; set; }
    public int RotationDegrees { get; set; }
    public string CreatedSource { get; set; } = "teacher";
    public int? ConfidenceBasisPoints { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class AcceptedAnswerEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string QuestionId { get; set; } = string.Empty;
    public string AnswerText { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
    public string VariantType { get; set; } = string.Empty;
    public string? CasePolicy { get; set; }
    public string? WidthPolicy { get; set; }
    public string? PunctuationPolicy { get; set; }
    public bool TeacherVerified { get; set; }
    public string AnswerProvenance { get; set; } = string.Empty;
    public string? SourceFileReferenceId { get; set; }
    public int? SourcePageNumber { get; set; }
    public string? SourceRegionId { get; set; }
    public string? Locale { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public QuestionEntity Question { get; set; } = null!;
}

public sealed class TestSessionEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string TemplateVersionId { get; set; } = string.Empty;
    public string? TitleOverride { get; set; }
    public DateOnly TestDate { get; set; }
    public string? Course { get; set; }
    public string? ClassLabel { get; set; }
    public string Priority { get; set; } = "economy";
    public string State { get; set; } = "draft";
    public bool ExpectedRosterEnabled { get; set; }
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public long Revision { get; set; } = 1;

    public TemplateVersionEntity TemplateVersion { get; set; } = null!;
    public ICollection<SessionRosterMemberEntity> RosterMembers { get; } =
        new List<SessionRosterMemberEntity>();
    public ICollection<SubmissionEntity> Submissions { get; } = new List<SubmissionEntity>();
}

public sealed class SessionRosterMemberEntity
{
    public string TestSessionId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public bool Expected { get; set; } = true;
    public string? SeatLabel { get; set; }

    public TestSessionEntity TestSession { get; set; } = null!;
    public StudentEntity Student { get; set; } = null!;
}

public sealed class UploadSessionEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string? TestSessionId { get; set; }
    public string? DestinationType { get; set; }
    public string? DestinationId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string DeclaredMimeType { get; set; } = string.Empty;
    public long ExpectedBytes { get; set; }
    public long CurrentBytes { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? FinalSha256 { get; set; }
    public string? IncrementalHashCheckpointJson { get; set; }
    public string IncomingRelativePath { get; set; } = string.Empty;
    public string State { get; set; } = "uploading";
    public DateTimeOffset ExpiresAt { get; set; }
    public string? SourceIpPrefix { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public TestSessionEntity? TestSession { get; set; }
}

public sealed class SubmissionEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string TestSessionId { get; set; } = string.Empty;
    public string State { get; set; } = "uploading";
    public string ScanPayloadState { get; set; } = "scan_available";
    public DateTimeOffset? ScanDeletedAt { get; set; }
    public string? ScanDeletionReason { get; set; }
    public string? AssignedStudentId { get; set; }
    public string AssignmentMethod { get; set; } = "none";
    public int? AssignmentConfidenceBasisPoints { get; set; }
    public string? AssignmentPolicyVersion { get; set; }
    public string? AssignmentEvidenceJson { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public bool CanonicalForSession { get; set; }
    public string UploadedByStaffUserId { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public string? OriginalFileObjectId { get; set; }
    public DateTimeOffset? UploadCompletedAt { get; set; }
    public string? PreprocessingPipelineVersion { get; set; }
    public string? PreprocessingManifestHash { get; set; }
    public DateTimeOffset? PreprocessingCompletedAt { get; set; }
    public int? PageCount { get; set; }
    public string? QualitySummaryJson { get; set; }
    public string? CurrentGradingRunId { get; set; }
    public string? FinalizedByStaffUserId { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string? VoidedByStaffUserId { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public TestSessionEntity TestSession { get; set; } = null!;
    public StudentEntity? AssignedStudent { get; set; }
    public ICollection<SubmissionPageEntity> Pages { get; } =
        new List<SubmissionPageEntity>();
    public ICollection<SubmissionArtifactEntity> Artifacts { get; } =
        new List<SubmissionArtifactEntity>();
    public ICollection<GradingRunEntity> GradingRuns { get; } = new List<GradingRunEntity>();
}

public sealed class GradingRunEntity
{
    public string Id { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public int RunNumber { get; set; }
    public string TemplateVersionId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string State { get; set; } = "running";
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public string PipelineVersion { get; set; } = string.Empty;
    public string CanonicalInputManifestHash { get; set; } = string.Empty;
    public long EarnedPointsMilli { get; set; }
    public long PossiblePointsMilli { get; set; }
    public long ResultSourceRevision { get; set; } = 1;
    public string? AiUsageAggregationJson { get; set; }
    public string? SupersedesGradingRunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public string? ActivatedByStaffUserId { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string? FinalizedByStaffUserId { get; set; }

    public SubmissionEntity Submission { get; set; } = null!;
    public TemplateVersionEntity TemplateVersion { get; set; } = null!;
    public ICollection<QuestionResultEntity> QuestionResults { get; } =
        new List<QuestionResultEntity>();
}

public sealed class QuestionResultEntity
{
    public string Id { get; set; } = string.Empty;
    public string GradingRunId { get; set; } = string.Empty;
    public string QuestionId { get; set; } = string.Empty;
    public string? TranscribedAnswer { get; set; }
    public string? NormalizedAnswer { get; set; }
    public long ProposedPointsMilli { get; set; }
    public long MaximumPointsMilli { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int ConfidenceBasisPoints { get; set; }
    public string KanjiCheck { get; set; } = "not_applicable";
    public string? ReasonCode { get; set; }
    public string? Explanation { get; set; }
    public string? AnswerCropFileReferenceId { get; set; }
    public bool ReviewRequired { get; set; }
    public string ReviewStatus { get; set; } = "not_required";
    public string? ModelResponseItemHash { get; set; }
    public string? CurrentRevisionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public GradingRunEntity GradingRun { get; set; } = null!;
    public QuestionEntity Question { get; set; } = null!;
    public ICollection<ResultRevisionEntity> Revisions { get; } =
        new List<ResultRevisionEntity>();
}

public sealed class ResultRevisionEntity : IAppendOnlyEntity
{
    public string Id { get; set; } = string.Empty;
    public string QuestionResultId { get; set; } = string.Empty;
    public int RevisionNumber { get; set; }
    public long AwardedPointsMilli { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? AnswerTextCorrection { get; set; }
    public string? ReasonCode { get; set; }
    public string? TeacherNote { get; set; }
    public string Source { get; set; } = "initial";
    public string? ActorStaffUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? SupersedesRevisionId { get; set; }

    public QuestionResultEntity QuestionResult { get; set; } = null!;
}

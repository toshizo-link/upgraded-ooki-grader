namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class ExportRecordEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public string GradingRunId { get; set; } = string.Empty;
    public long ResultSourceRevision { get; set; }
    public long SubmissionRevisionAtCreate { get; set; }
    public string TemplateVersionId { get; set; } = string.Empty;
    public int TemplateVersionNumber { get; set; }
    public int ExportRevision { get; set; }
    public string Type { get; set; } = "result_pdf";
    public string RendererVersion { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string? BackgroundJobId { get; set; }
    public string? FileReferenceId { get; set; }
    public string? Sha256 { get; set; }
    public long? Bytes { get; set; }
    public int? PageCount { get; set; }
    public string State { get; set; } = "queued";
    public string? ErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public string? SupersededReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public SubmissionEntity Submission { get; set; } = null!;
    public GradingRunEntity GradingRun { get; set; } = null!;
    public TemplateVersionEntity TemplateVersion { get; set; } = null!;
    public BackgroundJobEntity? BackgroundJob { get; set; }
    public FileReferenceEntity? FileReference { get; set; }
    public StaffUserEntity CreatedByStaffUser { get; set; } = null!;
}

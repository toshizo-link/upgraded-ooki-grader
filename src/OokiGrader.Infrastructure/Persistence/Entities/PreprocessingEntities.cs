namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class SubmissionPageEntity
{
    public string Id { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string NormalizedFileReferenceId { get; set; } = string.Empty;
    public string ThumbnailFileReferenceId { get; set; } = string.Empty;
    public int WidthPixels { get; set; }
    public int HeightPixels { get; set; }
    public int RotationDegrees { get; set; }
    public string SourceSha256 { get; set; } = string.Empty;
    public string NormalizedSha256 { get; set; } = string.Empty;
    public string DifferenceHash { get; set; } = string.Empty;
    public string PerceptualHash { get; set; } = string.Empty;
    public string QualityState { get; set; } = "accepted";
    public int BlurBasisPoints { get; set; }
    public int ContrastBasisPoints { get; set; }
    public int BrightnessBasisPoints { get; set; }
    public int InkCoverageBasisPoints { get; set; }
    public string AlignmentState { get; set; } = "not_configured";
    public int? AlignmentScoreBasisPoints { get; set; }
    public int? RepeatedPageNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public SubmissionEntity Submission { get; set; } = null!;
    public FileReferenceEntity NormalizedFileReference { get; set; } = null!;
    public FileReferenceEntity ThumbnailFileReference { get; set; } = null!;
    public ICollection<SubmissionArtifactEntity> Artifacts { get; } =
        new List<SubmissionArtifactEntity>();
}

public sealed class SubmissionArtifactEntity
{
    public string Id { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public string SubmissionPageId { get; set; } = string.Empty;
    public string? QuestionId { get; set; }
    public string? RegionId { get; set; }
    public string FileReferenceId { get; set; } = string.Empty;
    public string ArtifactType { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string? PanelLabel { get; set; }
    public string InputManifestHash { get; set; } = string.Empty;
    public int WidthPixels { get; set; }
    public int HeightPixels { get; set; }
    public bool ProviderDisclosureAllowed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public SubmissionEntity Submission { get; set; } = null!;
    public SubmissionPageEntity SubmissionPage { get; set; } = null!;
    public QuestionEntity? Question { get; set; }
    public RegionEntity? Region { get; set; }
    public FileReferenceEntity FileReference { get; set; } = null!;
}

public sealed class VisualDuplicateEntity
{
    public string Id { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public string CandidateSubmissionId { get; set; } = string.Empty;
    public int HammingDistance { get; set; }
    public string State { get; set; } = "possible";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByStaffUserId { get; set; }

    public SubmissionEntity Submission { get; set; } = null!;
    public SubmissionEntity CandidateSubmission { get; set; } = null!;
}

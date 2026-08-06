using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Persistence;

public sealed class OokiGraderDbContext : DbContext, IUnitOfWork
{
    private readonly IClock _clock;

    public OokiGraderDbContext(
        DbContextOptions<OokiGraderDbContext> options,
        IClock? clock = null)
        : base(options)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    public DbSet<SiteSettingsEntity> SiteSettings => Set<SiteSettingsEntity>();
    public DbSet<StaffUserEntity> StaffUsers => Set<StaffUserEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<StaffUserRoleEntity> StaffUserRoles => Set<StaffUserRoleEntity>();
    public DbSet<StaffSessionEntity> StaffSessions => Set<StaffSessionEntity>();
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords => Set<IdempotencyRecordEntity>();
    public DbSet<StudentEntity> Students => Set<StudentEntity>();
    public DbSet<StudentAliasEntity> StudentAliases => Set<StudentAliasEntity>();
    public DbSet<TestTemplateEntity> TestTemplates => Set<TestTemplateEntity>();
    public DbSet<TemplateVersionEntity> TemplateVersions => Set<TemplateVersionEntity>();
    public DbSet<TemplateSourceEntity> TemplateSources => Set<TemplateSourceEntity>();
    public DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    public DbSet<RegionEntity> Regions => Set<RegionEntity>();
    public DbSet<AcceptedAnswerEntity> AcceptedAnswers => Set<AcceptedAnswerEntity>();
    public DbSet<TestSessionEntity> TestSessions => Set<TestSessionEntity>();
    public DbSet<SessionRosterMemberEntity> SessionRosterMembers =>
        Set<SessionRosterMemberEntity>();
    public DbSet<UploadSessionEntity> UploadSessions => Set<UploadSessionEntity>();
    public DbSet<SubmissionEntity> Submissions => Set<SubmissionEntity>();
    public DbSet<SubmissionPageEntity> SubmissionPages => Set<SubmissionPageEntity>();
    public DbSet<SubmissionArtifactEntity> SubmissionArtifacts =>
        Set<SubmissionArtifactEntity>();
    public DbSet<VisualDuplicateEntity> VisualDuplicates =>
        Set<VisualDuplicateEntity>();
    public DbSet<GradingRunEntity> GradingRuns => Set<GradingRunEntity>();
    public DbSet<QuestionResultEntity> QuestionResults => Set<QuestionResultEntity>();
    public DbSet<ResultRevisionEntity> ResultRevisions => Set<ResultRevisionEntity>();
    public DbSet<AiConnectionEntity> AiConnections => Set<AiConnectionEntity>();
    public DbSet<AiCapabilityProbeEntity> AiCapabilityProbes =>
        Set<AiCapabilityProbeEntity>();
    public DbSet<AiTaskProfileEntity> AiTaskProfiles => Set<AiTaskProfileEntity>();
    public DbSet<AiEvaluationRecordEntity> AiEvaluationRecords =>
        Set<AiEvaluationRecordEntity>();
    public DbSet<AiRequestEntity> AiRequests => Set<AiRequestEntity>();
    public DbSet<AiBatchEntity> AiBatches => Set<AiBatchEntity>();
    public DbSet<AiBatchRequestEntity> AiBatchRequests =>
        Set<AiBatchRequestEntity>();
    public DbSet<AiUsageEntity> AiUsage => Set<AiUsageEntity>();
    public DbSet<PricingSnapshotEntity> PricingSnapshots => Set<PricingSnapshotEntity>();
    public DbSet<AiBudgetPolicyEntity> AiBudgetPolicies => Set<AiBudgetPolicyEntity>();
    public DbSet<AiBudgetReservationEntity> AiBudgetReservations =>
        Set<AiBudgetReservationEntity>();
    public DbSet<BackgroundJobEntity> BackgroundJobs => Set<BackgroundJobEntity>();
    public DbSet<OutboxEventEntity> OutboxEvents => Set<OutboxEventEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<FileObjectEntity> FileObjects => Set<FileObjectEntity>();
    public DbSet<FileReferenceEntity> FileReferences => Set<FileReferenceEntity>();
    public DbSet<DeletionManifestEntity> DeletionManifests =>
        Set<DeletionManifestEntity>();
    public DbSet<DeletionManifestItemEntity> DeletionManifestItems =>
        Set<DeletionManifestItemEntity>();
    public DbSet<BackupPolicyEntity> BackupPolicies => Set<BackupPolicyEntity>();
    public DbSet<BackupRecordEntity> BackupRecords => Set<BackupRecordEntity>();
    public DbSet<ExportRecordEntity> ExportRecords => Set<ExportRecordEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareTrackedEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override int SaveChanges()
    {
        PrepareTrackedEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess: true);
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        PrepareTrackedEntities();
        return base.SaveChangesAsync(
            acceptAllChangesOnSuccess: true,
            cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareTrackedEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureIdentity(modelBuilder);
        ConfigureAcademicModel(modelBuilder);
        ConfigurePreprocessingModel(modelBuilder);
        ConfigureAiModel(modelBuilder);
        ConfigureOperations(modelBuilder);
        ConfigureBackupModel(modelBuilder);
        ConfigureExportModel(modelBuilder);
        ApplySqliteTimestampConversions(modelBuilder);
        ApplySnakeCaseColumns(modelBuilder);
    }

    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SiteSettingsEntity>(builder =>
        {
            builder.ToTable("site_settings", table =>
            {
                table.HasCheckConstraint("ck_site_settings_singleton", "id = 'site'");
                table.HasCheckConstraint(
                    "ck_site_settings_scan_limits",
                    "managed_scan_warning_bytes <= managed_scan_cleanup_target_bytes " +
                    "AND managed_scan_cleanup_target_bytes <= managed_scan_hard_limit_bytes");
                table.HasCheckConstraint(
                    "ck_site_settings_retention",
                    "scan_retention_calendar_months > 0");
            });
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasMaxLength(8);
            builder.Property(entity => entity.SchoolName).HasMaxLength(300);
            builder.Property(entity => entity.TimeZone).HasMaxLength(100);
            builder.Property(entity => entity.Locale).HasMaxLength(32);
            builder.Property(entity => entity.DataRoot).HasMaxLength(1024);
            builder.Property(entity => entity.BootstrapTokenHash).HasMaxLength(128);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<StaffUserEntity>(builder =>
        {
            builder.ToTable("staff_user", table =>
            {
                table.HasCheckConstraint(
                    "ck_staff_user_status",
                    "status IN ('active','disabled')");
                table.HasCheckConstraint(
                    "ck_staff_user_failed_attempts",
                    "failed_attempt_count >= 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            builder.HasIndex(entity => entity.UsernameNormalized).IsUnique();
            builder.Property(entity => entity.Username).HasMaxLength(200);
            builder.Property(entity => entity.UsernameNormalized).HasMaxLength(200);
            builder.Property(entity => entity.DisplayName).HasMaxLength(300);
            builder.Property(entity => entity.PasswordHash).HasMaxLength(1024);
            builder.Property(entity => entity.PasswordAlgorithm).HasMaxLength(64);
            builder.Property(entity => entity.Status).HasMaxLength(32);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<RoleEntity>(builder =>
        {
            builder.ToTable("role");
            builder.HasKey(entity => entity.Name);
            builder.Property(entity => entity.Name).HasMaxLength(64);
            builder.Property(entity => entity.DisplayName).HasMaxLength(200);
            builder.HasData(
                new RoleEntity { Name = "administrator", DisplayName = "Administrator" },
                new RoleEntity { Name = "teacher", DisplayName = "Teacher" },
                new RoleEntity { Name = "scanOperator", DisplayName = "Scan operator" },
                new RoleEntity { Name = "readOnlyReviewer", DisplayName = "Read-only reviewer" });
        });

        modelBuilder.Entity<StaffUserRoleEntity>(builder =>
        {
            builder.ToTable("staff_user_role");
            builder.HasKey(entity => new { entity.StaffUserId, entity.RoleName });
            ConfigureUlid(builder, entity => entity.StaffUserId);
            ConfigureUlid(builder, entity => entity.GrantedByStaffUserId);
            builder.Property(entity => entity.RoleName).HasMaxLength(64);
            builder.HasOne(entity => entity.StaffUser)
                .WithMany(entity => entity.Roles)
                .HasForeignKey(entity => entity.StaffUserId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.Role)
                .WithMany()
                .HasForeignKey(entity => entity.RoleName)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StaffSessionEntity>(builder =>
        {
            builder.ToTable("staff_session", table =>
            {
                table.HasCheckConstraint(
                    "ck_staff_session_expiry",
                    "idle_expires_at <= absolute_expires_at");
            });
            builder.HasKey(entity => entity.IdHash);
            builder.Property(entity => entity.IdHash).HasMaxLength(128);
            ConfigureUlid(builder, entity => entity.StaffUserId);
            builder.Property(entity => entity.SourceIpPrefix).HasMaxLength(128);
            builder.Property(entity => entity.UserAgentHash).HasMaxLength(128);
            builder.Property(entity => entity.CsrfSecretHash).HasMaxLength(128);
            builder.Property(entity => entity.RevokeReason).HasMaxLength(256);
            builder.HasIndex(entity => new { entity.StaffUserId, entity.IdleExpiresAt });
            builder.HasOne(entity => entity.StaffUser)
                .WithMany(entity => entity.Sessions)
                .HasForeignKey(entity => entity.StaffUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IdempotencyRecordEntity>(builder =>
        {
            builder.ToTable("idempotency_record");
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            builder.Property(entity => entity.ActorKey).HasMaxLength(200);
            builder.Property(entity => entity.Route).HasMaxLength(500);
            builder.Property(entity => entity.IdempotencyKey).HasMaxLength(64);
            builder.Property(entity => entity.CanonicalRequestHash).HasMaxLength(64);
            builder.Property(entity => entity.ResponseContentType).HasMaxLength(128);
            builder.Property(entity => entity.ResponseHeadersJson).HasMaxLength(4_000);
            builder.HasIndex(entity => new
            {
                entity.ActorKey,
                entity.Route,
                entity.IdempotencyKey
            }).IsUnique();
            builder.HasIndex(entity => entity.ExpiresAt);
        });
    }

    private static void ConfigureAcademicModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StudentEntity>(builder =>
        {
            builder.ToTable("student", table =>
            {
                table.HasCheckConstraint(
                    "ck_student_status",
                    "status IN ('active','inactive','merged','erasure_pending')");
                table.HasCheckConstraint(
                    "ck_student_merge_target",
                    "(status = 'merged' AND merged_into_student_id IS NOT NULL) " +
                    "OR (status <> 'merged')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.MergedIntoStudentId);
            builder.Property(entity => entity.StudentNumber).HasMaxLength(200);
            builder.Property(entity => entity.StudentNumberNormalized).HasMaxLength(200);
            builder.Property(entity => entity.FamilyName).HasMaxLength(200);
            builder.Property(entity => entity.GivenName).HasMaxLength(200);
            builder.Property(entity => entity.DisplayName).HasMaxLength(400);
            builder.Property(entity => entity.Status).HasMaxLength(32);
            builder.HasIndex(entity => entity.StudentNumberNormalized)
                .IsUnique()
                .HasFilter("\"status\" <> 'merged'");
            builder.HasIndex(entity => new
            {
                entity.FamilyNameNormalized,
                entity.GivenNameNormalized
            });
            builder.HasIndex(entity => new
            {
                entity.FamilyNameKanaNormalized,
                entity.GivenNameKanaNormalized
            });
            builder.HasOne<StudentEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.MergedIntoStudentId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<StudentAliasEntity>(builder =>
        {
            builder.ToTable("student_alias", table =>
            {
                table.HasCheckConstraint(
                    "ck_student_alias_type",
                    "alias_type IN ('kanji','kana','romanized','old_name','spacing'," +
                    "'handwriting_hint','other')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.StudentId);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            builder.Property(entity => entity.AliasType).HasMaxLength(32);
            builder.Property(entity => entity.DisplayValue).HasMaxLength(400);
            builder.Property(entity => entity.NormalizedValue).HasMaxLength(400);
            builder.HasIndex(entity => entity.NormalizedValue);
            builder.HasIndex(entity => new
            {
                entity.StudentId,
                entity.NormalizedValue,
                entity.AliasType
            }).IsUnique();
            builder.HasOne(entity => entity.Student)
                .WithMany(entity => entity.Aliases)
                .HasForeignKey(entity => entity.StudentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestTemplateEntity>(builder =>
        {
            builder.ToTable("test_template", table =>
            {
                table.HasCheckConstraint(
                    "ck_test_template_state",
                    "state IN ('draft','active','retired','archived')");
                table.HasCheckConstraint(
                    "ck_test_template_default_points",
                    "default_points_milli > 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.ActiveVersionId);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            builder.Property(entity => entity.Title).HasMaxLength(500);
            builder.Property(entity => entity.DefaultPointsMilli)
                .HasDefaultValue(1_000L);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.HasIndex(entity => new { entity.State, entity.Title });
            builder.HasOne<TemplateVersionEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.ActiveVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<TemplateVersionEntity>(builder =>
        {
            builder.ToTable("template_version", table =>
            {
                table.HasCheckConstraint(
                    "ck_template_version_state",
                    "state IN ('draft','generating','validating','published','superseded','retired')");
                table.HasCheckConstraint(
                    "ck_template_version_points",
                    "(target_total_points_milli IS NULL OR target_total_points_milli >= 0) " +
                    "AND default_points_milli > 0");
                table.HasCheckConstraint(
                    "ck_template_version_published",
                    "(state <> 'published') OR " +
                    "(published_at IS NOT NULL AND published_by_staff_user_id IS NOT NULL " +
                    "AND content_hash IS NOT NULL)");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.TestTemplateId);
            ConfigureUlid(builder, entity => entity.BasedOnVersionId);
            ConfigureUlid(builder, entity => entity.PublishedByStaffUserId);
            builder.Property(entity => entity.DefaultPointsMilli)
                .HasDefaultValue(1_000L);
            builder.Property(entity => entity.ContentHash).HasMaxLength(64);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.PipelineVersion).HasMaxLength(100);
            builder.HasIndex(entity => new
            {
                entity.TestTemplateId,
                entity.VersionNumber
            }).IsUnique();
            builder.HasOne(entity => entity.TestTemplate)
                .WithMany(entity => entity.Versions)
                .HasForeignKey(entity => entity.TestTemplateId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<TemplateVersionEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.BasedOnVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<QuestionEntity>(builder =>
        {
            builder.ToTable("question", table =>
            {
                table.HasCheckConstraint(
                    "ck_question_points",
                    "max_points_milli > 0 AND point_increment_milli > 0 " +
                    "AND point_increment_milli <= max_points_milli " +
                    "AND max_points_milli % point_increment_milli = 0");
                table.HasCheckConstraint(
                    "ck_question_confidence",
                    "ai_confidence_basis_points IS NULL OR " +
                    "ai_confidence_basis_points BETWEEN 0 AND 10000");
                table.HasCheckConstraint(
                    "ck_question_type",
                    "question_type IN ('multiple_choice','boolean','numeric','exact_short_text'," +
                    "'semantic_short_text','multi_part','subjective','unsupported')");
                table.HasCheckConstraint(
                    "ck_question_grading_mode",
                    "grading_mode IN ('deterministic','transcribe_then_rules','ai_rubric','manual')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.TemplateVersionId);
            ConfigureUlid(builder, entity => entity.LogicalQuestionId);
            ConfigureUlid(builder, entity => entity.QuestionRegionId);
            ConfigureUlid(builder, entity => entity.AnswerRegionId);
            builder.Property(entity => entity.DisplayLabel).HasMaxLength(100);
            builder.Property(entity => entity.QuestionType).HasMaxLength(64);
            builder.Property(entity => entity.GradingMode).HasMaxLength(64);
            builder.Property(entity => entity.PointIncrementMilli)
                .HasDefaultValue(1L);
            builder.Property(entity => entity.RubricText).HasMaxLength(20_000);
            builder.Property(entity => entity.TeacherNote).HasMaxLength(4_000);
            builder.HasIndex(entity => new
            {
                entity.TemplateVersionId,
                entity.OrderIndex
            }).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.TemplateVersionId,
                entity.DisplayLabel
            }).IsUnique();
            builder.HasOne(entity => entity.TemplateVersion)
                .WithMany(entity => entity.Questions)
                .HasForeignKey(entity => entity.TemplateVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.QuestionRegion)
                .WithMany()
                .HasForeignKey(entity => entity.QuestionRegionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.AnswerRegion)
                .WithMany()
                .HasForeignKey(entity => entity.AnswerRegionId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<RegionEntity>(builder =>
        {
            builder.ToTable("region", table =>
            {
                table.HasCheckConstraint(
                    "ck_region_owner",
                    "owner_type = 'question'");
                table.HasCheckConstraint(
                    "ck_region_type",
                    "region_type IN ('question','answer','name','student_number','ignore','anchor')");
                table.HasCheckConstraint(
                    "ck_region_bounds",
                    "page_number > 0 " +
                    "AND x_millionths >= 0 AND y_millionths >= 0 " +
                    "AND width_millionths > 0 AND height_millionths > 0 " +
                    "AND x_millionths + width_millionths <= 1000000 " +
                    "AND y_millionths + height_millionths <= 1000000");
                table.HasCheckConstraint(
                    "ck_region_rotation",
                    "rotation_degrees IN (0,90,180,270)");
                table.HasCheckConstraint(
                    "ck_region_confidence",
                    "confidence_basis_points IS NULL OR " +
                    "confidence_basis_points BETWEEN 0 AND 10000");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.OwnerId);
            builder.Property(entity => entity.OwnerType).HasMaxLength(32);
            builder.Property(entity => entity.RegionType).HasMaxLength(32);
            builder.Property(entity => entity.CreatedSource).HasMaxLength(32);
            builder.HasIndex(entity => new
            {
                entity.OwnerType,
                entity.OwnerId,
                entity.RegionType
            }).IsUnique();
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<TemplateSourceEntity>(builder =>
        {
            builder.ToTable("template_source", table =>
            {
                table.HasCheckConstraint(
                    "ck_template_source_role",
                    "source_role IN ('blank_test','contains_model_answers'," +
                    "'contains_non_model_answers','separate_answer_key')");
                table.HasCheckConstraint("ck_template_source_ordinal", "ordinal >= 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.TemplateVersionId);
            ConfigureUlid(builder, entity => entity.UploadSessionId);
            ConfigureUlid(builder, entity => entity.FileReferenceId);
            ConfigureUlid(builder, entity => entity.UploadedByStaffUserId);
            builder.Property(entity => entity.SourceRole).HasMaxLength(64);
            builder.Property(entity => entity.DisplayName).HasMaxLength(500);
            builder.HasIndex(entity => new
            {
                entity.TemplateVersionId,
                entity.Ordinal
            }).IsUnique();
            builder.HasOne(entity => entity.TemplateVersion)
                .WithMany(entity => entity.Sources)
                .HasForeignKey(entity => entity.TemplateVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.UploadSession)
                .WithMany()
                .HasForeignKey(entity => entity.UploadSessionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AcceptedAnswerEntity>(builder =>
        {
            builder.ToTable("accepted_answer", table =>
            {
                table.HasCheckConstraint(
                    "ck_accepted_answer_variant",
                    "variant_type IN ('canonical','equivalent','phonetic_exception','numeric'," +
                    "'regex_restricted','choice')");
                table.HasCheckConstraint(
                    "ck_accepted_answer_provenance",
                    "answer_provenance IN ('provided_model_answer','teacher_entered'," +
                    "'ai_proposed','derived_variant')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.QuestionId);
            builder.Property(entity => entity.VariantType).HasMaxLength(64);
            builder.Property(entity => entity.AnswerProvenance).HasMaxLength(64);
            builder.HasIndex(entity => new
            {
                entity.QuestionId,
                entity.NormalizedText,
                entity.VariantType
            }).IsUnique();
            builder.HasOne(entity => entity.Question)
                .WithMany(entity => entity.AcceptedAnswers)
                .HasForeignKey(entity => entity.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<TestSessionEntity>(builder =>
        {
            builder.ToTable("test_session", table =>
            {
                table.HasCheckConstraint(
                    "ck_test_session_priority",
                    "priority IN ('economy','expedite')");
                table.HasCheckConstraint(
                    "ck_test_session_state",
                    "state IN ('draft','open','closed','archived')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.TemplateVersionId);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            builder.Property(entity => entity.Priority).HasMaxLength(32);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.HasIndex(entity => new { entity.TestDate, entity.State })
                .IsDescending(true, false);
            builder.HasIndex(entity => entity.TemplateVersionId);
            builder.HasOne(entity => entity.TemplateVersion)
                .WithMany()
                .HasForeignKey(entity => entity.TemplateVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<SessionRosterMemberEntity>(builder =>
        {
            builder.ToTable("session_roster_member");
            builder.HasKey(entity => new { entity.TestSessionId, entity.StudentId });
            ConfigureUlid(builder, entity => entity.TestSessionId);
            ConfigureUlid(builder, entity => entity.StudentId);
            builder.HasOne(entity => entity.TestSession)
                .WithMany(entity => entity.RosterMembers)
                .HasForeignKey(entity => entity.TestSessionId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(entity => entity.Student)
                .WithMany()
                .HasForeignKey(entity => entity.StudentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UploadSessionEntity>(builder =>
        {
            builder.ToTable("upload_session", table =>
            {
                table.HasCheckConstraint(
                    "ck_upload_session_bytes",
                    "expected_bytes >= 0 AND current_bytes >= 0 " +
                    "AND current_bytes <= expected_bytes");
                table.HasCheckConstraint(
                    "ck_upload_session_state",
                    "state IN ('uploading','finalizing','duplicate_pending'," +
                    "'completed','cancelled','expired','failed')");
                table.HasCheckConstraint(
                    "ck_upload_session_destination",
                    "(purpose = 'completed_test' AND test_session_id IS NOT NULL) " +
                    "OR (purpose <> 'completed_test')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            ConfigureUlid(builder, entity => entity.TestSessionId);
            builder.Property(entity => entity.Purpose).HasMaxLength(64);
            builder.Property(entity => entity.DestinationType).HasMaxLength(100);
            builder.Property(entity => entity.DestinationId).HasMaxLength(200);
            builder.Property(entity => entity.OriginalFileName).HasMaxLength(500);
            builder.Property(entity => entity.DeclaredMimeType).HasMaxLength(200);
            builder.Property(entity => entity.ExpectedSha256).HasMaxLength(64);
            builder.Property(entity => entity.FinalSha256).HasMaxLength(64);
            builder.Property(entity => entity.IncomingRelativePath).HasMaxLength(1024);
            builder.Property(entity => entity.SourceIpPrefix).HasMaxLength(128);
            builder.Property(entity => entity.IdempotencyKey).HasMaxLength(64);
            builder.HasIndex(entity => entity.ExpiresAt);
            builder.HasIndex(entity => new
            {
                entity.CreatedByStaffUserId,
                entity.IdempotencyKey
            }).IsUnique().HasFilter("\"idempotency_key\" IS NOT NULL");
            builder.HasOne(entity => entity.TestSession)
                .WithMany()
                .HasForeignKey(entity => entity.TestSessionId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<SubmissionEntity>(builder =>
        {
            builder.ToTable("submission", table =>
            {
                table.HasCheckConstraint("ck_submission_attempt", "attempt_number > 0");
                table.HasCheckConstraint(
                    "ck_submission_assignment_confidence",
                    "assignment_confidence_basis_points IS NULL OR " +
                    "assignment_confidence_basis_points BETWEEN 0 AND 10000");
                table.HasCheckConstraint(
                    "ck_submission_assignment_method",
                    "assignment_method IN ('auto','teacher','student_number','none')");
                table.HasCheckConstraint(
                    "ck_submission_scan_payload_state",
                    "scan_payload_state IN ('scan_available','deletion_pending','scan_deleted')");
                table.HasCheckConstraint(
                    "ck_submission_auto_assignment_evidence",
                    "assignment_method <> 'auto' OR " +
                    "(assignment_policy_version IS NOT NULL AND assignment_evidence_json IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_submission_page_count",
                    "page_count IS NULL OR page_count > 0");
                table.HasCheckConstraint(
                    "ck_submission_preprocessing_completion",
                    "preprocessing_completed_at IS NULL OR " +
                    "(preprocessing_pipeline_version IS NOT NULL " +
                    "AND preprocessing_manifest_hash IS NOT NULL " +
                    "AND page_count IS NOT NULL)");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.TestSessionId);
            ConfigureUlid(builder, entity => entity.AssignedStudentId);
            ConfigureUlid(builder, entity => entity.UploadedByStaffUserId);
            ConfigureUlid(builder, entity => entity.CurrentGradingRunId);
            ConfigureUlid(builder, entity => entity.OriginalFileObjectId);
            builder.Property(entity => entity.State).HasMaxLength(64);
            builder.Property(entity => entity.ScanPayloadState).HasMaxLength(32);
            builder.Property(entity => entity.ScanDeletionReason).HasMaxLength(32);
            builder.Property(entity => entity.AssignmentMethod).HasMaxLength(32);
            builder.Property(entity => entity.OriginalFileName).HasMaxLength(500);
            builder.Property(entity => entity.PreprocessingPipelineVersion)
                .HasMaxLength(100);
            builder.Property(entity => entity.PreprocessingManifestHash)
                .HasMaxLength(64);
            builder.Property(entity => entity.QualitySummaryJson)
                .HasMaxLength(16_000);
            builder.HasIndex(entity => new
            {
                entity.TestSessionId,
                entity.AssignedStudentId,
                entity.State
            });
            builder.HasIndex(entity => new
            {
                entity.TestSessionId,
                entity.AssignedStudentId
            }).IsUnique().HasFilter(
                "\"assigned_student_id\" IS NOT NULL " +
                "AND \"canonical_for_session\" = 1 AND \"voided_at\" IS NULL");
            builder.HasIndex(entity => entity.UploadCompletedAt);
            builder.HasIndex(entity => entity.ScanPayloadState);
            builder.HasIndex(entity => entity.PreprocessingManifestHash);
            builder.HasOne(entity => entity.TestSession)
                .WithMany(entity => entity.Submissions)
                .HasForeignKey(entity => entity.TestSessionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.AssignedStudent)
                .WithMany()
                .HasForeignKey(entity => entity.AssignedStudentId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<GradingRunEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.CurrentGradingRunId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<FileObjectEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.OriginalFileObjectId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<GradingRunEntity>(builder =>
        {
            builder.ToTable("grading_run", table =>
            {
                table.HasCheckConstraint(
                    "ck_grading_run_points",
                    "earned_points_milli >= 0 AND possible_points_milli >= 0 " +
                    "AND earned_points_milli <= possible_points_milli");
                table.HasCheckConstraint("ck_grading_run_number", "run_number > 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.SubmissionId);
            ConfigureUlid(builder, entity => entity.TemplateVersionId);
            ConfigureUlid(builder, entity => entity.SupersedesGradingRunId);
            builder.Property(entity => entity.CanonicalInputManifestHash).HasMaxLength(64);
            builder.Property(entity => entity.State).HasMaxLength(64);
            builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.RunNumber
            }).IsUnique();
            builder.HasIndex(entity => entity.State);
            builder.HasOne(entity => entity.Submission)
                .WithMany(entity => entity.GradingRuns)
                .HasForeignKey(entity => entity.SubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.TemplateVersion)
                .WithMany()
                .HasForeignKey(entity => entity.TemplateVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<GradingRunEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.SupersedesGradingRunId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QuestionResultEntity>(builder =>
        {
            builder.ToTable("question_result", table =>
            {
                table.HasCheckConstraint(
                    "ck_question_result_points",
                    "proposed_points_milli >= 0 AND maximum_points_milli >= 0 " +
                    "AND proposed_points_milli <= maximum_points_milli");
                table.HasCheckConstraint(
                    "ck_question_result_confidence",
                    "confidence_basis_points BETWEEN 0 AND 10000");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.GradingRunId);
            ConfigureUlid(builder, entity => entity.QuestionId);
            ConfigureUlid(builder, entity => entity.CurrentRevisionId);
            builder.Property(entity => entity.Outcome).HasMaxLength(64);
            builder.Property(entity => entity.Method).HasMaxLength(64);
            builder.HasIndex(entity => new
            {
                entity.GradingRunId,
                entity.QuestionId
            }).IsUnique();
            builder.HasOne(entity => entity.GradingRun)
                .WithMany(entity => entity.QuestionResults)
                .HasForeignKey(entity => entity.GradingRunId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.Question)
                .WithMany()
                .HasForeignKey(entity => entity.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<ResultRevisionEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.CurrentRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ResultRevisionEntity>(builder =>
        {
            builder.ToTable("result_revision", table =>
            {
                table.HasCheckConstraint("ck_result_revision_number", "revision_number > 0");
                table.HasCheckConstraint("ck_result_revision_points", "awarded_points_milli >= 0");
                table.HasCheckConstraint(
                    "ck_result_revision_source",
                    "source IN ('initial','teacher_override','regrade_adoption','system_correction')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.QuestionResultId);
            ConfigureUlid(builder, entity => entity.ActorStaffUserId);
            ConfigureUlid(builder, entity => entity.SupersedesRevisionId);
            builder.HasIndex(entity => new
            {
                entity.QuestionResultId,
                entity.RevisionNumber
            }).IsUnique();
            builder.HasOne(entity => entity.QuestionResult)
                .WithMany(entity => entity.Revisions)
                .HasForeignKey(entity => entity.QuestionResultId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<ResultRevisionEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.SupersedesRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePreprocessingModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubmissionPageEntity>(builder =>
        {
            builder.ToTable("submission_page", table =>
            {
                table.HasCheckConstraint(
                    "ck_submission_page_number",
                    "page_number > 0");
                table.HasCheckConstraint(
                    "ck_submission_page_dimensions",
                    "width_pixels > 0 AND height_pixels > 0");
                table.HasCheckConstraint(
                    "ck_submission_page_rotation",
                    "rotation_degrees IN (0,90,180,270)");
                table.HasCheckConstraint(
                    "ck_submission_page_quality_state",
                    "quality_state IN ('accepted','warning','rejected')");
                table.HasCheckConstraint(
                    "ck_submission_page_quality_metrics",
                    "blur_basis_points BETWEEN 0 AND 10000 " +
                    "AND contrast_basis_points BETWEEN 0 AND 10000 " +
                    "AND brightness_basis_points BETWEEN 0 AND 10000 " +
                    "AND ink_coverage_basis_points BETWEEN 0 AND 10000 " +
                    "AND (alignment_score_basis_points IS NULL " +
                    "OR alignment_score_basis_points BETWEEN 0 AND 10000)");
                table.HasCheckConstraint(
                    "ck_submission_page_alignment_state",
                    "alignment_state IN ('not_configured','aligned','warning','failed')");
                table.HasCheckConstraint(
                    "ck_submission_page_repeat",
                    "repeated_page_number IS NULL " +
                    "OR (repeated_page_number > 0 " +
                    "AND repeated_page_number <> page_number)");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.SubmissionId);
            ConfigureUlid(builder, entity => entity.NormalizedFileReferenceId);
            ConfigureUlid(builder, entity => entity.ThumbnailFileReferenceId);
            builder.Property(entity => entity.SourceSha256).HasMaxLength(64);
            builder.Property(entity => entity.NormalizedSha256).HasMaxLength(64);
            builder.Property(entity => entity.DifferenceHash).HasMaxLength(64);
            builder.Property(entity => entity.PerceptualHash).HasMaxLength(128);
            builder.Property(entity => entity.QualityState).HasMaxLength(32);
            builder.Property(entity => entity.AlignmentState).HasMaxLength(32);
            builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.PageNumber
            }).IsUnique();
            builder.HasIndex(entity => entity.DifferenceHash);
            builder.HasOne(entity => entity.Submission)
                .WithMany(entity => entity.Pages)
                .HasForeignKey(entity => entity.SubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.NormalizedFileReference)
                .WithMany()
                .HasForeignKey(entity => entity.NormalizedFileReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.ThumbnailFileReference)
                .WithMany()
                .HasForeignKey(entity => entity.ThumbnailFileReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SubmissionArtifactEntity>(builder =>
        {
            builder.ToTable("submission_artifact", table =>
            {
                table.HasCheckConstraint(
                    "ck_submission_artifact_type",
                    "artifact_type IN ('answer_crop','name_crop'," +
                    "'student_number_crop','alignment_diagnostic')");
                table.HasCheckConstraint(
                    "ck_submission_artifact_ordinal",
                    "ordinal >= 0");
                table.HasCheckConstraint(
                    "ck_submission_artifact_dimensions",
                    "width_pixels > 0 AND height_pixels > 0");
                table.HasCheckConstraint(
                    "ck_submission_artifact_minimization",
                    "provider_disclosure_allowed = 0 " +
                    "OR (artifact_type = 'answer_crop' " +
                    "AND question_id IS NOT NULL) " +
                    "OR (artifact_type IN ('name_crop','student_number_crop') " +
                    "AND question_id IS NULL)");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.SubmissionId);
            ConfigureUlid(builder, entity => entity.SubmissionPageId);
            ConfigureUlid(builder, entity => entity.QuestionId);
            ConfigureUlid(builder, entity => entity.RegionId);
            ConfigureUlid(builder, entity => entity.FileReferenceId);
            builder.Property(entity => entity.ArtifactType).HasMaxLength(64);
            builder.Property(entity => entity.PanelLabel).HasMaxLength(200);
            builder.Property(entity => entity.InputManifestHash).HasMaxLength(64);
            builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.ArtifactType,
                entity.QuestionId,
                entity.Ordinal
            }).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.ProviderDisclosureAllowed
            });
            builder.HasOne(entity => entity.Submission)
                .WithMany(entity => entity.Artifacts)
                .HasForeignKey(entity => entity.SubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.SubmissionPage)
                .WithMany(entity => entity.Artifacts)
                .HasForeignKey(entity => entity.SubmissionPageId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.Question)
                .WithMany()
                .HasForeignKey(entity => entity.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.Region)
                .WithMany()
                .HasForeignKey(entity => entity.RegionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.FileReference)
                .WithMany()
                .HasForeignKey(entity => entity.FileReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VisualDuplicateEntity>(builder =>
        {
            builder.ToTable("visual_duplicate", table =>
            {
                table.HasCheckConstraint(
                    "ck_visual_duplicate_order",
                    "submission_id < candidate_submission_id");
                table.HasCheckConstraint(
                    "ck_visual_duplicate_distance",
                    "hamming_distance BETWEEN 0 AND 64");
                table.HasCheckConstraint(
                    "ck_visual_duplicate_state",
                    "state IN ('possible','confirmed','dismissed')");
                table.HasCheckConstraint(
                    "ck_visual_duplicate_resolution",
                    "(state = 'possible' AND resolved_at IS NULL) " +
                    "OR (state <> 'possible' AND resolved_at IS NOT NULL)");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.SubmissionId);
            ConfigureUlid(builder, entity => entity.CandidateSubmissionId);
            ConfigureUlid(builder, entity => entity.ResolvedByStaffUserId);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.CandidateSubmissionId
            }).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.CreatedAt,
                entity.Id
            });
            builder.HasOne(entity => entity.Submission)
                .WithMany()
                .HasForeignKey(entity => entity.SubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.CandidateSubmission)
                .WithMany()
                .HasForeignKey(entity => entity.CandidateSubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAiModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiConnectionEntity>(builder =>
        {
            builder.ToTable("ai_connection", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_connection_provider",
                    "provider IN ('geminiDirect','openRouter')");
                table.HasCheckConstraint(
                    "ck_ai_connection_state",
                    "state IN ('pending_probe','active','disabled','blocked')");
                table.HasCheckConstraint(
                    "ck_ai_connection_limits",
                    "credential_revision > 0 AND timeout_seconds BETWEEN 5 AND 300 " +
                    "AND concurrency_limit BETWEEN 1 AND 16");
                table.HasCheckConstraint(
                    "ck_ai_connection_batch_probe_revision",
                    "last_batch_capability_probe_credential_revision IS NULL " +
                    "OR last_batch_capability_probe_credential_revision > 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            builder.Property(entity => entity.Provider).HasMaxLength(32);
            builder.Property(entity => entity.EndpointProfile).HasMaxLength(100);
            builder.Property(entity => entity.ModelId).HasMaxLength(128);
            builder.Property(entity => entity.SecretReference).HasMaxLength(500);
            builder.Property(entity => entity.KeyFingerprint).HasMaxLength(100);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.LastCapabilityProbeState).HasMaxLength(32);
            builder.Property(entity => entity.LastCapabilityProbeErrorCode).HasMaxLength(200);
            builder.Property(entity => entity.LastBatchCapabilityProbeState)
                .HasMaxLength(32);
            builder.Property(entity => entity.LastBatchCapabilityProbeErrorCode)
                .HasMaxLength(200);
            builder.HasIndex(entity => new
            {
                entity.Provider,
                entity.State
            });
            builder.HasIndex(entity => entity.Provider)
                .IsUnique()
                .HasFilter("\"state\" <> 'disabled'");
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<AiCapabilityProbeEntity>(builder =>
        {
            builder.ToTable("ai_capability_probe", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_capability_probe_state",
                    "state IN ('running','passed','failed')");
                table.HasCheckConstraint(
                    "ck_ai_capability_probe_latency",
                    "(latency_milliseconds IS NULL OR latency_milliseconds >= 0) " +
                    "AND (batch_latency_milliseconds IS NULL " +
                    "OR batch_latency_milliseconds >= 0)");
                table.HasCheckConstraint(
                    "ck_ai_capability_probe_batch_state",
                    "batch_state IN ('not_run','passed','failed')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiConnectionId);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.SafeErrorCode).HasMaxLength(200);
            builder.Property(entity => entity.BatchState).HasMaxLength(32);
            builder.Property(entity => entity.BatchSafeErrorCode)
                .HasMaxLength(200);
            builder.HasIndex(entity => new
            {
                entity.AiConnectionId,
                entity.CreatedAt
            }).IsDescending(false, true);
            builder.HasOne(entity => entity.AiConnection)
                .WithMany(entity => entity.CapabilityProbes)
                .HasForeignKey(entity => entity.AiConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiTaskProfileEntity>(builder =>
        {
            builder.ToTable("ai_task_profile", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_task_profile_task",
                    "task_type IN ('templateExtraction','nameTranscription'," +
                    "'initialGrading','adjudication')");
                table.HasCheckConstraint(
                    "ck_ai_task_profile_strategy",
                    "processing_strategy IN ('gemini_batch','queued_standard'," +
                    "'expedite_standard')");
                table.HasCheckConstraint(
                    "ck_ai_task_profile_approval",
                    "approval_state IN ('draft','capability_passed','pilot_approved'," +
                    "'production_approved','rejected')");
                table.HasCheckConstraint(
                    "ck_ai_task_profile_limits",
                    "max_output_tokens BETWEEN 64 AND 65536 " +
                    "AND concurrency_limit BETWEEN 1 AND 16");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiConnectionId);
            ConfigureUlid(builder, entity => entity.ActivatedByStaffUserId);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            builder.Property(entity => entity.Name).HasMaxLength(200);
            builder.Property(entity => entity.TaskType).HasMaxLength(64);
            builder.Property(entity => entity.ModelId).HasMaxLength(128);
            builder.Property(entity => entity.ProcessingStrategy).HasMaxLength(64);
            builder.Property(entity => entity.PromptVersion).HasMaxLength(100);
            builder.Property(entity => entity.SchemaVersion).HasMaxLength(100);
            builder.Property(entity => entity.PromptContentHash).HasMaxLength(64);
            builder.Property(entity => entity.ThinkingLevel).HasMaxLength(32);
            builder.Property(entity => entity.MediaResolution).HasMaxLength(32);
            builder.Property(entity => entity.ApprovalState).HasMaxLength(32);
            builder.HasIndex(entity => new
            {
                entity.TaskType,
                entity.Active
            }).IsUnique().HasFilter("\"active\" = 1");
            builder.HasOne(entity => entity.AiConnection)
                .WithMany(entity => entity.TaskProfiles)
                .HasForeignKey(entity => entity.AiConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<AiEvaluationRecordEntity>(builder =>
        {
            builder.ToTable("ai_evaluation_record", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_evaluation_record_provider",
                    "provider IN ('geminiDirect','openRouter')");
                table.HasCheckConstraint(
                    "ck_ai_evaluation_record_task",
                    "task_type IN ('templateExtraction','nameTranscription'," +
                    "'initialGrading','adjudication')");
                table.HasCheckConstraint(
                    "ck_ai_evaluation_record_strategy",
                    "processing_strategy IN ('gemini_batch','queued_standard'," +
                    "'expedite_standard')");
                table.HasCheckConstraint(
                    "ck_ai_evaluation_record_revisions",
                    "task_profile_revision > 0 AND connection_revision > 0");
                table.HasCheckConstraint(
                    "ck_ai_evaluation_record_sample",
                    "sample_count > 0 AND critical_failure_count >= 0 " +
                    "AND critical_failure_count <= sample_count");
                table.HasCheckConstraint(
                    "ck_ai_evaluation_record_accuracy",
                    "agreement_basis_points BETWEEN 0 AND 10000 " +
                    "AND lower_confidence_bound_basis_points BETWEEN 0 AND 10000 " +
                    "AND lower_confidence_bound_basis_points <= agreement_basis_points");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiTaskProfileId);
            ConfigureUlid(builder, entity => entity.SignedOffByStaffUserId);
            builder.Property(entity => entity.Provider).HasMaxLength(32);
            builder.Property(entity => entity.ModelId).HasMaxLength(128);
            builder.Property(entity => entity.TaskType).HasMaxLength(64);
            builder.Property(entity => entity.ProcessingStrategy).HasMaxLength(64);
            builder.Property(entity => entity.PromptVersion).HasMaxLength(100);
            builder.Property(entity => entity.SchemaVersion).HasMaxLength(100);
            builder.Property(entity => entity.PromptContentHash).HasMaxLength(64);
            builder.Property(entity => entity.DatasetVersion).HasMaxLength(200);
            builder.Property(entity => entity.DatasetSha256).HasMaxLength(64);
            builder.Property(entity => entity.EvidenceSha256).HasMaxLength(64);
            builder.HasIndex(entity => new
            {
                entity.AiTaskProfileId,
                entity.CreatedAt,
            }).IsDescending(false, true);
            builder.HasIndex(entity => new
            {
                entity.AiTaskProfileId,
                entity.TaskProfileRevision,
                entity.EvidenceSha256,
            }).IsUnique();
            builder.HasOne(entity => entity.AiTaskProfile)
                .WithMany(entity => entity.EvaluationRecords)
                .HasForeignKey(entity => entity.AiTaskProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<StaffUserEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.SignedOffByStaffUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiRequestEntity>(builder =>
        {
            builder.ToTable("ai_request", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_request_state",
                    "state IN ('prepared','budget_blocked','dispatching','retry_waiting'," +
                    "'response_ready','succeeded','invalid_output','safety_blocked'," +
                    "'failed','cancelled')");
                table.HasCheckConstraint(
                    "ck_ai_request_attempt",
                    "dispatch_attempt >= 0");
                table.HasCheckConstraint(
                    "ck_ai_request_attempt_number",
                    "attempt_number BETWEEN 1 AND 8");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiTaskProfileId);
            ConfigureUlid(builder, entity => entity.RetryOfAiRequestId);
            builder.Property(entity => entity.RequestKey).HasMaxLength(200);
            builder.Property(entity => entity.Purpose).HasMaxLength(100);
            builder.Property(entity => entity.EntityType).HasMaxLength(100);
            builder.Property(entity => entity.EntityId).HasMaxLength(200);
            builder.Property(entity => entity.InputManifestHash).HasMaxLength(64);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.ProviderResponseId).HasMaxLength(500);
            builder.Property(entity => entity.ActualModel).HasMaxLength(128);
            builder.Property(entity => entity.FinishReason).HasMaxLength(100);
            builder.Property(entity => entity.AcceptedResponseHash).HasMaxLength(64);
            builder.Property(entity => entity.ValidatedResponseJson).HasMaxLength(1_000_000);
            builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
            builder.Property(entity => entity.SafeErrorDetail).HasMaxLength(2_000);
            builder.HasIndex(entity => entity.RequestKey).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.EntityType,
                entity.EntityId,
                entity.InputManifestHash,
                entity.TaskProfileRevision,
                entity.AttemptNumber
            }).IsUnique();
            builder.HasIndex(entity => entity.RetryOfAiRequestId)
                .IsUnique()
                .HasFilter("\"retry_of_ai_request_id\" IS NOT NULL");
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.CreatedAt
            });
            builder.HasOne(entity => entity.AiTaskProfile)
                .WithMany(entity => entity.Requests)
                .HasForeignKey(entity => entity.AiTaskProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<AiBatchEntity>(builder =>
        {
            builder.ToTable("ai_batch", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_batch_provider",
                    "provider = 'geminiDirect'");
                table.HasCheckConstraint(
                    "ck_ai_batch_model",
                    "model_id = 'gemini-3.5-flash-lite'");
                table.HasCheckConstraint(
                    "ck_ai_batch_state",
                    "state IN ('prepared','uploading','submitting','submitted'," +
                    "'reconcile_required','pending','running','delayed'," +
                    "'succeeded','failed','cancelled','expired','manual_review')");
                table.HasCheckConstraint(
                    "ck_ai_batch_counts",
                    "submission_epoch > 0 AND create_attempt_count >= 0 " +
                    "AND request_count > 0 AND input_json_lines_bytes >= 0 " +
                    "AND successful_request_count >= 0 " +
                    "AND failed_request_count >= 0 " +
                    "AND pending_request_count >= 0 " +
                    "AND reconciliation_attempt_count >= 0");
                table.HasCheckConstraint(
                    "ck_ai_batch_remote_identity",
                    "(provider_batch_name IS NULL) OR " +
                    "(state NOT IN ('prepared','uploading','submitting'," +
                    "'reconcile_required'))");
                table.HasCheckConstraint(
                    "ck_ai_batch_create_attempt",
                    "create_attempt_count <= 1");
                table.HasCheckConstraint(
                    "ck_ai_batch_cleanup",
                    "cleanup_state IN ('not_started','pending','completed','failed'," +
                    "'expired')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiConnectionId);
            ConfigureUlid(builder, entity => entity.AiTaskProfileId);
            builder.Property(entity => entity.Provider).HasMaxLength(32);
            builder.Property(entity => entity.ModelId).HasMaxLength(128);
            builder.Property(entity => entity.CompatibilityKey).HasMaxLength(64);
            builder.Property(entity => entity.ManifestJson).HasMaxLength(1_000_000);
            builder.Property(entity => entity.ManifestHash).HasMaxLength(64);
            builder.Property(entity => entity.DisplayName).HasMaxLength(200);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.ProviderBatchName).HasMaxLength(500);
            builder.Property(entity => entity.ProviderInputFileName).HasMaxLength(500);
            builder.Property(entity => entity.ProviderOutputFileName).HasMaxLength(500);
            builder.Property(entity => entity.InputJsonLinesSha256).HasMaxLength(64);
            builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
            builder.Property(entity => entity.SafeErrorDetail).HasMaxLength(2_000);
            builder.Property(entity => entity.CleanupState).HasMaxLength(32);
            builder.HasIndex(entity => entity.DisplayName).IsUnique();
            builder.HasIndex(entity => entity.ProviderBatchName)
                .IsUnique()
                .HasFilter("\"provider_batch_name\" IS NOT NULL");
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.NextActionAt,
                entity.CreatedAt,
            });
            builder.HasIndex(entity => new
            {
                entity.CompatibilityKey,
                entity.State,
            });
            builder.HasOne(entity => entity.AiConnection)
                .WithMany(entity => entity.Batches)
                .HasForeignKey(entity => entity.AiConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.AiTaskProfile)
                .WithMany(entity => entity.Batches)
                .HasForeignKey(entity => entity.AiTaskProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<AiBatchRequestEntity>(builder =>
        {
            builder.ToTable("ai_batch_request", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_batch_request_state",
                    "state IN ('ready','prepared','submitted','response_ready'," +
                    "'failed','missing','cancelled')");
                table.HasCheckConstraint(
                    "ck_ai_batch_request_bytes",
                    "((state IN ('ready','prepared','submitted') " +
                    "AND provider_request_json IS NOT NULL " +
                    "AND provider_request_bytes > 0) OR " +
                    "(state IN ('response_ready','failed','missing','cancelled') " +
                    "AND provider_request_json IS NULL " +
                    "AND provider_request_bytes = 0))");
                table.HasCheckConstraint(
                    "ck_ai_batch_request_ordinal",
                    "(ai_batch_id IS NULL AND ordinal IS NULL) OR " +
                    "(ai_batch_id IS NOT NULL AND ordinal >= 0)");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiBatchId);
            ConfigureUlid(builder, entity => entity.AiRequestId);
            builder.Property(entity => entity.RequestKey).HasMaxLength(200);
            builder.Property(entity => entity.CompatibilityKey).HasMaxLength(64);
            builder.Property(entity => entity.ProviderRequestJson)
                .HasMaxLength(25_000_000);
            builder.Property(entity => entity.ProviderRequestHash).HasMaxLength(64);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.ProviderResponseId).HasMaxLength(500);
            builder.Property(entity => entity.ResponseJson).HasMaxLength(1_000_000);
            builder.Property(entity => entity.ResponseHash).HasMaxLength(64);
            builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
            builder.HasIndex(entity => entity.AiRequestId).IsUnique();
            builder.HasIndex(entity => entity.RequestKey).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.CompatibilityKey,
                entity.CreatedAt,
            });
            builder.HasIndex(entity => new
            {
                entity.AiBatchId,
                entity.Ordinal,
            }).IsUnique().HasFilter("\"ai_batch_id\" IS NOT NULL");
            builder.HasOne(entity => entity.AiBatch)
                .WithMany(entity => entity.Requests)
                .HasForeignKey(entity => entity.AiBatchId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.AiRequest)
                .WithOne(entity => entity.BatchRequest)
                .HasForeignKey<AiBatchRequestEntity>(entity => entity.AiRequestId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<AiUsageEntity>(builder =>
        {
            builder.ToTable("ai_usage", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_usage_tokens",
                    "(input_tokens IS NULL OR input_tokens >= 0) " +
                    "AND (cached_tokens IS NULL OR cached_tokens >= 0) " +
                    "AND (output_tokens IS NULL OR output_tokens >= 0) " +
                    "AND (thinking_tokens IS NULL OR thinking_tokens >= 0) " +
                    "AND (total_tokens IS NULL OR total_tokens >= 0)");
                table.HasCheckConstraint(
                    "ck_ai_usage_cost",
                    "estimated_usd_micros >= 0 AND estimated_jpy_micros >= 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiRequestId);
            ConfigureUlid(builder, entity => entity.PricingSnapshotId);
            builder.Property(entity => entity.RequestedProvider).HasMaxLength(32);
            builder.Property(entity => entity.RequestedModel).HasMaxLength(128);
            builder.Property(entity => entity.ActualProvider).HasMaxLength(64);
            builder.Property(entity => entity.ActualModel).HasMaxLength(128);
            builder.Property(entity => entity.ProviderRequestId).HasMaxLength(500);
            builder.HasIndex(entity => entity.AiRequestId).IsUnique();
            builder.HasIndex(entity => entity.MeasuredAt);
            builder.HasOne(entity => entity.AiRequest)
                .WithOne(entity => entity.Usage)
                .HasForeignKey<AiUsageEntity>(entity => entity.AiRequestId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.PricingSnapshot)
                .WithMany()
                .HasForeignKey(entity => entity.PricingSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PricingSnapshotEntity>(builder =>
        {
            builder.ToTable("pricing_snapshot", table =>
            {
                table.HasCheckConstraint(
                    "ck_pricing_snapshot_rates",
                    "input_usd_micros_per_million_tokens >= 0 " +
                    "AND output_usd_micros_per_million_tokens >= 0 " +
                    "AND thinking_usd_micros_per_million_tokens >= 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            builder.Property(entity => entity.Provider).HasMaxLength(32);
            builder.Property(entity => entity.ModelId).HasMaxLength(128);
            builder.Property(entity => entity.SourceUrl).HasMaxLength(1_000);
            builder.HasIndex(entity => new
            {
                entity.Provider,
                entity.ModelId,
                entity.EffectiveAt
            }).IsDescending(false, false, true);
        });

        modelBuilder.Entity<AiBudgetPolicyEntity>(builder =>
        {
            builder.ToTable("ai_budget_policy", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_budget_policy_limits",
                    "daily_warning_usd_micros >= 0 " +
                    "AND daily_hard_usd_micros >= daily_warning_usd_micros " +
                    "AND monthly_warning_usd_micros >= 0 " +
                    "AND monthly_hard_usd_micros >= monthly_warning_usd_micros " +
                    "AND usd_to_jpy_micros > 0");
            });
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasMaxLength(64);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<AiBudgetReservationEntity>(builder =>
        {
            builder.ToTable("ai_budget_reservation", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_budget_reservation_state",
                    "state IN ('reserved','settled','released')");
                table.HasCheckConstraint(
                    "ck_ai_budget_reservation_amounts",
                    "reserved_usd_micros >= 0 AND actual_usd_micros >= 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.AiRequestId);
            builder.Property(entity => entity.UsageMonth).HasMaxLength(7);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.HasIndex(entity => entity.AiRequestId).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.UsageDay,
                entity.State
            });
            builder.HasIndex(entity => new
            {
                entity.UsageMonth,
                entity.State
            });
            builder.HasOne(entity => entity.AiRequest)
                .WithMany()
                .HasForeignKey(entity => entity.AiRequestId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackgroundJobEntity>(builder =>
        {
            builder.ToTable("background_job", table =>
            {
                table.HasCheckConstraint(
                    "ck_background_job_state",
                    "state IN ('queued','leased','retry_waiting','succeeded','failed'," +
                    "'blocked','cancelled')");
                table.HasCheckConstraint(
                    "ck_background_job_attempts",
                    "attempt_count >= 0 AND max_attempts > 0");
                table.HasCheckConstraint(
                    "ck_background_job_progress",
                    "progress_basis_points BETWEEN 0 AND 10000");
                table.HasCheckConstraint(
                    "ck_background_job_lease",
                    "(state <> 'leased') OR " +
                    "(lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL)");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            builder.Property(entity => entity.Type).HasMaxLength(200);
            builder.Property(entity => entity.DeduplicationKey).HasMaxLength(500);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
            builder.Property(entity => entity.SafeErrorDetail).HasMaxLength(2000);
            builder.HasIndex(entity => entity.DeduplicationKey).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.Priority,
                entity.NextAttemptAt,
                entity.CreatedAt
            }).IsDescending(false, true, false, false);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<OutboxEventEntity>(builder =>
        {
            builder.ToTable("outbox_event");
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            builder.Property(entity => entity.AggregateType).HasMaxLength(200);
            builder.Property(entity => entity.EventType).HasMaxLength(200);
            builder.HasIndex(entity => new
            {
                entity.DeliveredAt,
                entity.OccurredAt,
                entity.Id
            });
        });

        modelBuilder.Entity<AuditEventEntity>(builder =>
        {
            builder.ToTable("audit_event");
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.ActorStaffUserId);
            builder.Property(entity => entity.EventType).HasMaxLength(200);
            builder.Property(entity => entity.ObjectType).HasMaxLength(200);
            builder.Property(entity => entity.ObjectId).HasMaxLength(200);
            builder.Property(entity => entity.Outcome).HasMaxLength(100);
            builder.Property(entity => entity.SourceIpPrefix).HasMaxLength(128);
            builder.HasIndex(entity => entity.OccurredAt).IsDescending();
            builder.HasIndex(entity => entity.ActorStaffUserId);
            builder.HasIndex(entity => new { entity.ObjectType, entity.ObjectId });
        });

        modelBuilder.Entity<FileObjectEntity>(builder =>
        {
            builder.ToTable("file_object", table =>
            {
                table.HasCheckConstraint("ck_file_object_bytes", "bytes >= 0");
                table.HasCheckConstraint(
                    "ck_file_object_references",
                    "reference_count_cache >= 0");
                table.HasCheckConstraint(
                    "ck_file_object_state",
                    "state IN ('pending','available','deletion_pending','deleted'," +
                    "'quarantined','missing')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            builder.Property(entity => entity.Sha256).HasMaxLength(64);
            builder.Property(entity => entity.Extension).HasMaxLength(16);
            builder.Property(entity => entity.RelativeObjectPath).HasMaxLength(1024);
            builder.Property(entity => entity.StorageClass).HasMaxLength(64);
            builder.Property(entity => entity.RetentionClass).HasMaxLength(64);
            builder.HasIndex(entity => new { entity.StorageClass, entity.Sha256 }).IsUnique();
            builder.HasIndex(entity => entity.State);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<FileReferenceEntity>(builder =>
        {
            builder.ToTable("file_reference");
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.FileObjectId);
            builder.Property(entity => entity.OwnerType).HasMaxLength(200);
            builder.Property(entity => entity.OwnerId).HasMaxLength(200);
            builder.Property(entity => entity.Purpose).HasMaxLength(200);
            builder.HasIndex(entity => new { entity.OwnerType, entity.OwnerId });
            builder.HasIndex(entity => entity.RetentionAnchorAt);
            builder.HasOne(entity => entity.FileObject)
                .WithMany(entity => entity.References)
                .HasForeignKey(entity => entity.FileObjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeletionManifestEntity>(builder =>
        {
            builder.ToTable("deletion_manifest", table =>
            {
                table.HasCheckConstraint(
                    "ck_deletion_manifest_reason",
                    "reason IN ('age','quota','manual_erasure','orphan_cleanup')");
                table.HasCheckConstraint(
                    "ck_deletion_manifest_state",
                    "state IN ('pending','deleting','completed','failed')");
                table.HasCheckConstraint(
                    "ck_deletion_manifest_counts",
                    "planned_object_count >= 0 " +
                    "AND planned_reference_count >= 0 " +
                    "AND planned_bytes >= 0 " +
                    "AND deleted_object_count >= 0 " +
                    "AND released_reference_count >= 0 " +
                    "AND missing_object_count >= 0 " +
                    "AND failure_count >= 0 " +
                    "AND deleted_bytes >= 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.BackgroundJobId);
            builder.Property(entity => entity.Reason).HasMaxLength(32);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.SafeErrorDetail).HasMaxLength(2_000);
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.CreatedAt,
                entity.Id
            });
            builder.HasIndex(entity => entity.CompletedAt).IsDescending();
            builder.HasOne<BackgroundJobEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.BackgroundJobId)
                .OnDelete(DeleteBehavior.SetNull);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<DeletionManifestItemEntity>(builder =>
        {
            builder.ToTable("deletion_manifest_item", table =>
            {
                table.HasCheckConstraint(
                    "ck_deletion_manifest_item_bytes",
                    "bytes >= 0");
                table.HasCheckConstraint(
                    "ck_deletion_manifest_item_attempts",
                    "attempt_count >= 0");
                table.HasCheckConstraint(
                    "ck_deletion_manifest_item_state",
                    "state IN ('pending','deleted','already_missing'," +
                    "'reference_released','failed')");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.DeletionManifestId);
            ConfigureUlid(builder, entity => entity.FileObjectId);
            ConfigureUlid(builder, entity => entity.FileReferenceId);
            ConfigureUlid(builder, entity => entity.SubmissionId);
            builder.Property(entity => entity.Purpose).HasMaxLength(200);
            builder.Property(entity => entity.Sha256).HasMaxLength(64);
            builder.Property(entity => entity.StorageClass).HasMaxLength(64);
            builder.Property(entity => entity.Extension).HasMaxLength(16);
            builder.Property(entity => entity.RelativeObjectPath).HasMaxLength(1_024);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.Outcome).HasMaxLength(64);
            builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
            builder.HasIndex(entity => new
            {
                entity.DeletionManifestId,
                entity.FileReferenceId
            }).IsUnique();
            builder.HasIndex(entity => entity.FileObjectId);
            builder.HasIndex(entity => entity.SubmissionId);
            builder.HasIndex(entity => new
            {
                entity.DeletionManifestId,
                entity.State
            });
            builder.HasOne(entity => entity.DeletionManifest)
                .WithMany(entity => entity.Items)
                .HasForeignKey(entity => entity.DeletionManifestId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(entity => entity.FileObject)
                .WithMany()
                .HasForeignKey(entity => entity.FileObjectId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });
    }

    private static void ConfigureBackupModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupPolicyEntity>(builder =>
        {
            builder.ToTable("backup_policy", table =>
            {
                table.HasCheckConstraint(
                    "ck_backup_policy_schedule",
                    "schedule_local_hour BETWEEN 0 AND 23 " +
                    "AND schedule_local_minute BETWEEN 0 AND 59");
                table.HasCheckConstraint(
                    "ck_backup_policy_retention",
                    "daily_retention_days > 0 " +
                    "AND weekly_retention_weeks > 0 " +
                    "AND monthly_retention_months > 0");
                table.HasCheckConstraint(
                    "ck_backup_policy_destination",
                    "enabled = 0 OR destination_root_path IS NOT NULL");
                table.HasCheckConstraint(
                    "ck_backup_policy_scan_encryption",
                    "include_managed_scans = 0 " +
                    "OR destination_encryption_confirmed = 1");
            });
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasMaxLength(64);
            builder.Property(entity => entity.Name).HasMaxLength(200);
            builder.Property(entity => entity.DestinationRootPath)
                .HasMaxLength(1_024);
            builder.HasIndex(entity => entity.Enabled);
            ConfigureRevision(builder);
        });

        modelBuilder.Entity<BackupRecordEntity>(builder =>
        {
            builder.ToTable("backup_record", table =>
            {
                table.HasCheckConstraint(
                    "ck_backup_record_trigger",
                    "trigger IN ('manual','scheduled','pre_upgrade')");
                table.HasCheckConstraint(
                    "ck_backup_record_state",
                    "state IN ('queued','running','verifying','verified'," +
                    "'failed','expired')");
                table.HasCheckConstraint(
                    "ck_backup_record_sizes",
                    "database_bytes >= 0 AND object_count >= 0 " +
                    "AND object_bytes >= 0 AND secret_envelope_count >= 0 " +
                    "AND secret_envelope_bytes >= 0 AND database_data_version >= 0");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.BackgroundJobId);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            builder.Property(entity => entity.BackupPolicyId).HasMaxLength(64);
            builder.Property(entity => entity.Trigger).HasMaxLength(32);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.DestinationRelativePath)
                .HasMaxLength(1_024);
            builder.Property(entity => entity.ManifestSha256).HasMaxLength(64);
            builder.Property(entity => entity.DatabaseSha256).HasMaxLength(64);
            builder.Property(entity => entity.DatabaseMigrationId).HasMaxLength(200);
            builder.Property(entity => entity.ApplicationVersion).HasMaxLength(100);
            builder.Property(entity => entity.IntegrityResult).HasMaxLength(200);
            builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
            builder.Property(entity => entity.SafeErrorDetail).HasMaxLength(2_000);
            builder.HasIndex(entity => entity.BackgroundJobId).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.RequestedAt,
                entity.Id
            });
            builder.HasIndex(entity => entity.CompletedAt).IsDescending();
            builder.HasOne(entity => entity.BackupPolicy)
                .WithMany(entity => entity.Records)
                .HasForeignKey(entity => entity.BackupPolicyId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<BackgroundJobEntity>()
                .WithMany()
                .HasForeignKey(entity => entity.BackgroundJobId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });
    }

    private static void ConfigureExportModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExportRecordEntity>(builder =>
        {
            builder.ToTable("export_record", table =>
            {
                table.HasCheckConstraint(
                    "ck_export_record_revisions",
                    "result_source_revision > 0 " +
                    "AND submission_revision_at_create > 0 " +
                    "AND template_version_number > 0 " +
                    "AND export_revision > 0");
                table.HasCheckConstraint(
                    "ck_export_record_type",
                    "type = 'result_pdf'");
                table.HasCheckConstraint(
                    "ck_export_record_state",
                    "state IN ('queued','rendering','verified','failed'," +
                    "'superseded')");
                table.HasCheckConstraint(
                    "ck_export_record_sizes",
                    "(bytes IS NULL OR bytes >= 0) " +
                    "AND (page_count IS NULL OR page_count > 0)");
                table.HasCheckConstraint(
                    "ck_export_record_verified",
                    "state <> 'verified' OR " +
                    "(file_reference_id IS NOT NULL AND sha256 IS NOT NULL " +
                    "AND bytes IS NOT NULL AND page_count IS NOT NULL " +
                    "AND completed_at IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_export_record_superseded",
                    "superseded_at IS NULL OR superseded_reason IS NOT NULL");
            });
            builder.HasKey(entity => entity.Id);
            ConfigureUlid(builder, entity => entity.Id);
            ConfigureUlid(builder, entity => entity.SubmissionId);
            ConfigureUlid(builder, entity => entity.GradingRunId);
            ConfigureUlid(builder, entity => entity.TemplateVersionId);
            ConfigureUlid(builder, entity => entity.BackgroundJobId);
            ConfigureUlid(builder, entity => entity.FileReferenceId);
            ConfigureUlid(builder, entity => entity.CreatedByStaffUserId);
            builder.Property(entity => entity.Type).HasMaxLength(32);
            builder.Property(entity => entity.RendererVersion).HasMaxLength(100);
            builder.Property(entity => entity.SourceHash).HasMaxLength(64);
            builder.Property(entity => entity.Sha256).HasMaxLength(64);
            builder.Property(entity => entity.State).HasMaxLength(32);
            builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
            builder.Property(entity => entity.SafeErrorDetail).HasMaxLength(2_000);
            builder.Property(entity => entity.SupersededReason).HasMaxLength(200);
            builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.ExportRevision
            }).IsUnique();
            builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.CreatedAt,
                entity.Id
            });
            builder.HasIndex(entity => new
            {
                entity.State,
                entity.CreatedAt,
                entity.Id
            });
            builder.HasIndex(entity => entity.BackgroundJobId)
                .IsUnique()
                .HasFilter("\"background_job_id\" IS NOT NULL");
            builder.HasIndex(entity => entity.FileReferenceId)
                .IsUnique()
                .HasFilter("\"file_reference_id\" IS NOT NULL");
            builder.HasOne(entity => entity.Submission)
                .WithMany()
                .HasForeignKey(entity => entity.SubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.GradingRun)
                .WithMany()
                .HasForeignKey(entity => entity.GradingRunId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.TemplateVersion)
                .WithMany()
                .HasForeignKey(entity => entity.TemplateVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.BackgroundJob)
                .WithMany()
                .HasForeignKey(entity => entity.BackgroundJobId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.FileReference)
                .WithMany()
                .HasForeignKey(entity => entity.FileReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(entity => entity.CreatedByStaffUser)
                .WithMany()
                .HasForeignKey(entity => entity.CreatedByStaffUserId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigureRevision(builder);
        });
    }

    private void PrepareTrackedEntities()
    {
        var now = _clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries<IAppendOnlyEntity>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<IRevisionedEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.Revision = Math.Max(1, entry.Entity.Revision);
            }
            else if (entry.State == EntityState.Modified)
            {
                var original = entry.Property(entity => entity.Revision).OriginalValue;
                entry.Entity.Revision = checked(original + 1);
            }
        }

        foreach (var entry in ChangeTracker.Entries<IUpdatedEntity>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    private static void ConfigureRevision<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder)
        where TEntity : class, IRevisionedEntity
    {
        builder.Property(entity => entity.Revision).IsConcurrencyToken();
    }

    private static void ConfigureUlid<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder,
        System.Linq.Expressions.Expression<Func<TEntity, string?>> property)
        where TEntity : class
    {
        builder.Property(property).HasMaxLength(26).IsFixedLength();
    }

    private static void ApplySnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static void ApplySqliteTimestampConversions(ModelBuilder modelBuilder)
    {
        var requiredConverter = new ValueConverter<DateTimeOffset, long>(
            timestamp => timestamp.ToUniversalTime().ToUnixTimeMilliseconds(),
            milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        var nullableConverter = new ValueConverter<DateTimeOffset?, long?>(
            timestamp => timestamp.HasValue
                ? timestamp.Value.ToUniversalTime().ToUnixTimeMilliseconds()
                : null,
            milliseconds => milliseconds.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value)
                : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(requiredConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableConverter);
                }
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}

namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class SiteSettingsEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = "site";
    public string SchoolName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "Asia/Tokyo";
    public string Locale { get; set; } = "ja-JP";
    public long ManagedScanHardLimitBytes { get; set; } = 161_061_273_600;
    public long ManagedScanCleanupTargetBytes { get; set; } = 155_692_564_480;
    public long ManagedScanWarningBytes { get; set; } = 144_955_146_240;
    public long PhysicalFreeReserveBytes { get; set; } = 5_368_709_120;
    public int ScanRetentionCalendarMonths { get; set; } = 3;
    public string DataRoot { get; set; } = string.Empty;
    public string? BackupPolicyId { get; set; }
    public string? ActiveAiProfileSetId { get; set; }
    public string? BootstrapTokenHash { get; set; }
    public DateTimeOffset? BootstrapTokenExpiresAt { get; set; }
    public DateTimeOffset? BootstrapCompletedAt { get; set; }
    public bool MaintenanceMode { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StaffUserEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string UsernameNormalized { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordAlgorithm { get; set; } = string.Empty;
    public int PasswordAlgorithmVersion { get; set; }
    public string Status { get; set; } = "active";
    public int FailedAttemptCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset CredentialChangedAt { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? PasswordSetupExpiresAt { get; set; }
    public DateTimeOffset? PasswordSetupUsedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<StaffUserRoleEntity> Roles { get; } = new List<StaffUserRoleEntity>();
    public ICollection<StaffSessionEntity> Sessions { get; } = new List<StaffSessionEntity>();
}

public sealed class RoleEntity
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class StaffUserRoleEntity
{
    public string StaffUserId { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string GrantedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset GrantedAt { get; set; }

    public StaffUserEntity StaffUser { get; set; } = null!;
    public RoleEntity Role { get; set; } = null!;
}

public sealed class StaffSessionEntity
{
    // This is the one-way hash of the random session token. Raw tokens never persist.
    public string IdHash { get; set; } = string.Empty;
    public string StaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    public string? SourceIpPrefix { get; set; }
    public string? UserAgentHash { get; set; }
    public string CsrfSecretHash { get; set; } = string.Empty;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }

    public StaffUserEntity StaffUser { get; set; } = null!;
}

public sealed class IdempotencyRecordEntity
{
    public string Id { get; set; } = string.Empty;
    public string ActorKey { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CanonicalRequestHash { get; set; } = string.Empty;
    public int ResponseStatusCode { get; set; }
    public string? ResponseContentType { get; set; }
    public string? ResponseHeadersJson { get; set; }
    public string? ResponseBodyJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

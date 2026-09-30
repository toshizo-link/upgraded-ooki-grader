using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Security;

namespace OokiGrader.IntegrationTests;

public sealed class Gemini38UpgradeMigrationTests
{
    [Theory]
    [InlineData("gemini-3.5-flash-lite")]
    [InlineData("gemini-3.7-flash")]
    [InlineData("gemini-3.8-flash-preview")]
    [InlineData("custom-previous-model")]
    [InlineData("gemini-3.8-flash")]
    public async Task UpgradeForcesExact38AndPreservesCredentialAndSchoolData(
        string previousModel)
    {
        await using var fixture = await Fixture.CreateAsync(previousModel);
        await using var db = fixture.CreateDbContext();
        var schoolBefore = await SnapshotSchoolDataAsync(db);
        var historyBefore = JsonSerializer.Serialize(await db.AiRequests
            .AsNoTracking().SingleAsync());
        var oldProfiles = await db.AiTaskProfiles.AsNoTracking().ToArrayAsync();
        var oldConnection = await db.AiConnections.AsNoTracking().SingleAsync();

        Assert.True(await fixture.ApplyAsync(db));

        db.ChangeTracker.Clear();
        var connection = await db.AiConnections.AsNoTracking().SingleAsync();
        Assert.Equal("gemini-3.8-flash", connection.ModelId);
        Assert.Equal(oldConnection.SecretReference, connection.SecretReference);
        Assert.Equal(oldConnection.KeyFingerprint, connection.KeyFingerprint);
        Assert.Equal(oldConnection.CredentialRevision, connection.CredentialRevision);
        Assert.Equal(300, connection.TimeoutSeconds);
        Assert.Equal(oldConnection.ConcurrencyLimit, connection.ConcurrencyLimit);
        Assert.Equal("active", connection.State);
        Assert.Equal("passed", connection.LastCapabilityProbeState);
        Assert.Equal("not_run", connection.LastBatchCapabilityProbeState);
        Assert.Null(connection.LastBatchCapabilityProbeCredentialRevision);
        Assert.True(connection.Revision > oldConnection.Revision);
        using (var secret = await fixture.Secrets.ReadAsync(
            new AiSecretReference(connection.SecretReference)))
        {
            Assert.Equal(Fixture.ApiKey, Encoding.UTF8.GetString(secret.Utf8Bytes.Span));
        }

        await AssertCurrentProfilesAsync(db, fixture.Catalog, active: true);
        var usable = await TemplateExtractionAiProfilePolicy.FindCurrentUsableAsync(
            db, fixture.Catalog, AiProviderFeaturePolicy.AllowAll, default);
        Assert.NotNull(usable);
        Assert.Equal("gemini-3.8-flash", usable.Profile.ModelId);
        Assert.Equal("medium", usable.Profile.ThinkingLevel);
        Assert.Equal(65_536, usable.Profile.MaxOutputTokens);
        foreach (var oldProfile in oldProfiles)
        {
            var historical = await db.AiTaskProfiles.AsNoTracking()
                .SingleAsync(item => item.Id == oldProfile.Id);
            Assert.False(historical.Active);
            Assert.Equal(oldProfile.ModelId, historical.ModelId);
            Assert.Equal(oldProfile.PromptVersion, historical.PromptVersion);
            Assert.Equal(oldProfile.ThinkingLevel, historical.ThinkingLevel);
        }

        Assert.Equal(schoolBefore, await SnapshotSchoolDataAsync(db));
        var schoolManager = await db.SchoolManagerSettings.AsNoTracking().SingleAsync();
        using (var schoolManagerSecret = await fixture.Secrets.ReadAsync(
            new AiSecretReference(schoolManager.PasswordSecretReference!)))
        {
            Assert.Equal(Fixture.SchoolManagerPassword,
                Encoding.UTF8.GetString(schoolManagerSecret.Utf8Bytes.Span));
        }

        Assert.Equal(historyBefore, JsonSerializer.Serialize(await db.AiRequests
            .AsNoTracking().SingleAsync()));
        Assert.Equal(1, fixture.Provider.ProbeCount);
        Assert.Equal("gemini-3.8-flash", fixture.Provider.ProbedModel);
        Assert.Equal(Fixture.ApiKey, fixture.Provider.ProbedCredential);
        Assert.Equal(1, await db.AuditEvents.CountAsync(item =>
            item.EventType == Gemini38UpgradeMigration.AuditEventType));
    }

    [Fact]
    public async Task UpgradeRunsOnceAndDoesNotUndoLaterIntentionalChanges()
    {
        await using var fixture = await Fixture.CreateAsync("gemini-3.5-flash-lite");
        await using var db = fixture.CreateDbContext();
        Assert.True(await fixture.ApplyAsync(db));
        var connection = await db.AiConnections.SingleAsync();
        connection.ModelId = "gemini-later-intentional-choice";
        await db.SaveChangesAsync();
        var revision = connection.Revision;
        var profileCount = await db.AiTaskProfiles.CountAsync();
        var probeCount = await db.AiCapabilityProbes.CountAsync();

        db.ChangeTracker.Clear();
        Assert.False(await fixture.ApplyAsync(db));
        Assert.Equal(1, fixture.Provider.ProbeCount);
        var after = await db.AiConnections.AsNoTracking().SingleAsync();
        Assert.Equal("gemini-later-intentional-choice", after.ModelId);
        Assert.Equal(revision, after.Revision);
        Assert.Equal(profileCount, await db.AiTaskProfiles.CountAsync());
        Assert.Equal(probeCount, await db.AiCapabilityProbes.CountAsync());
    }

    [Fact]
    public async Task FailedNewModelProbeDoesNotReuseOldPassedCapabilityOrLoseKey()
    {
        await using var fixture = await Fixture.CreateAsync("gemini-3.5-flash-lite");
        fixture.Provider.Result = new AiCapabilityProbeResult(
            true, false, false, false, false, "failed", "gemini_rate_limited", null);
        await using var db = fixture.CreateDbContext();
        var before = await SnapshotSchoolDataAsync(db);
        Assert.True(await fixture.ApplyAsync(db));
        db.ChangeTracker.Clear();
        var connection = await db.AiConnections.SingleAsync();
        Assert.Equal("gemini-3.8-flash", connection.ModelId);
        Assert.Equal("blocked", connection.State);
        Assert.Equal("failed", connection.LastCapabilityProbeState);
        Assert.Equal("gemini_rate_limited", connection.LastCapabilityProbeErrorCode);
        Assert.Equal(7, connection.CredentialRevision);
        using var stored = await fixture.Secrets.ReadAsync(
            new AiSecretReference(connection.SecretReference));
        Assert.Equal(Fixture.ApiKey, Encoding.UTF8.GetString(stored.Utf8Bytes.Span));
        await AssertCurrentProfilesAsync(db, fixture.Catalog, active: false);
        Assert.Equal(before, await SnapshotSchoolDataAsync(db));
        Assert.False(await fixture.ApplyAsync(db));
        Assert.Equal(1, fixture.Provider.ProbeCount);

        // The existing administrator recheck path can enable the migrated
        // defaults when the temporary provider problem has cleared.
        var now = TimeProvider.System.GetUtcNow();
        AiAdminEndpoints.ApplyProbeResult(connection, Fixture.PassedProbe, now);
        await AiAdminEndpoints.ReconcileCurrentProfilesAsync(
            db, connection, connection.CreatedByStaffUserId, fixture.Catalog,
            now, replaceOtherProviders: true, cancellationToken: default);
        await AssertCurrentProfilesAsync(db, fixture.Catalog, active: true);
    }

    [Fact]
    public async Task UnavailableStoredKeyLeaves3Point8ConfiguredAndRequiresReconnect()
    {
        await using var fixture = await Fixture.CreateAsync("gemini-3.5-flash-lite");
        await using var db = fixture.CreateDbContext();
        var connection = await db.AiConnections.SingleAsync();
        var reference = connection.SecretReference;
        await fixture.Secrets.DeleteAsync(new AiSecretReference(reference));
        Assert.True(await fixture.ApplyAsync(db));
        Assert.Equal("gemini-3.8-flash", connection.ModelId);
        Assert.Equal(reference, connection.SecretReference);
        Assert.Equal("blocked", connection.State);
        Assert.Equal("ai_secret_unavailable", connection.LastCapabilityProbeErrorCode);
        Assert.Equal(0, fixture.Provider.ProbeCount);
        await AssertCurrentProfilesAsync(db, fixture.Catalog, active: false);
    }

    [Fact]
    public async Task UpgradeReplacesActiveAlternateProviderForAllDefaultTasks()
    {
        await using var fixture = await Fixture.CreateAsync("gemini-3.5-flash-lite");
        await using var db = fixture.CreateDbContext();
        var current = await db.AiTaskProfiles.SingleAsync(item =>
            item.TaskType == AiTaskTypes.TemplateExtraction && item.Active);
        current.Active = false;
        await db.SaveChangesAsync();
        var alternate = new AiConnectionEntity
        {
            Id = UlidId.New(), Provider = AiProviders.OpenRouter,
            EndpointProfile = AiProviderCatalog.OpenRouterEndpointProfile,
            ModelId = "google/gemini-3.5-flash-lite", State = "active",
            LastCapabilityProbeState = "passed", SecretReference = "unchanged-alternate-key",
            KeyFingerprint = "alternate", CreatedByStaffUserId = current.CreatedByStaffUserId,
        };
        db.AiConnections.Add(alternate);
        db.AiTaskProfiles.Add(new AiTaskProfileEntity
        {
            Id = UlidId.New(), AiConnectionId = alternate.Id,
            ConnectionRevision = 1, ModelId = alternate.ModelId,
            TaskType = AiTaskTypes.TemplateExtraction, Name = "Alternate template",
            PromptVersion = "old-prompt", SchemaVersion = "old-schema",
            PromptContentHash = new string('b', 64), Active = true,
            ApprovalState = "capability_passed", CreatedByStaffUserId = current.CreatedByStaffUserId,
        });
        await db.SaveChangesAsync();
        var alternateBefore = JsonSerializer.Serialize(await db.AiConnections
            .AsNoTracking().SingleAsync(item => item.Id == alternate.Id));
        Assert.True(await fixture.ApplyAsync(db));
        db.ChangeTracker.Clear();
        Assert.Equal(alternateBefore, JsonSerializer.Serialize(await db.AiConnections
            .AsNoTracking().SingleAsync(item => item.Id == alternate.Id)));
        await AssertCurrentProfilesAsync(db, fixture.Catalog, active: true);
        Assert.Equal(4, await db.AiTaskProfiles.CountAsync(item => item.Active));
    }

    private static async Task AssertCurrentProfilesAsync(
        OokiGraderDbContext db,
        ApprovedPromptBundleCatalog catalog,
        bool active)
    {
        var connection = await db.AiConnections.AsNoTracking()
            .SingleAsync(item => item.Provider == AiProviders.GeminiDirect);
        var profiles = await db.AiTaskProfiles.AsNoTracking()
            .Where(item => item.ModelId == "gemini-3.8-flash"
                && item.PromptVersion != "old-prompt")
            .ToArrayAsync();
        Assert.Equal(4, profiles.Length);
        Assert.All(profiles, profile =>
        {
            var bundle = catalog.GetRequired(profile.TaskType);
            Assert.Equal(connection.Id, profile.AiConnectionId);
            Assert.Equal(connection.CredentialRevision, profile.ConnectionRevision);
            Assert.Equal(bundle.PromptVersion, profile.PromptVersion);
            Assert.Equal(bundle.SchemaVersion, profile.SchemaVersion);
            Assert.Equal(bundle.ContentHash, profile.PromptContentHash);
            Assert.Equal(profile.TaskType == AiTaskTypes.TemplateExtraction
                ? "medium" : "low", profile.ThinkingLevel);
            Assert.Equal(profile.TaskType == AiTaskTypes.TemplateExtraction
                ? 65_536 : 8_192, profile.MaxOutputTokens);
            Assert.Equal(active, profile.Active);
            Assert.Equal(active ? "capability_passed" : "draft", profile.ApprovalState);
            Assert.Null(profile.AccuracyEvaluationId);
        });
    }

    private static async Task<string> SnapshotSchoolDataAsync(OokiGraderDbContext db) =>
        JsonSerializer.Serialize(new
        {
            site = await db.SiteSettings.AsNoTracking().SingleAsync(),
            students = await db.Students.AsNoTracking().ToArrayAsync(),
            templates = await db.TestTemplates.AsNoTracking().ToArrayAsync(),
            files = await db.FileObjects.AsNoTracking().ToArrayAsync(),
            fileReferences = await db.FileReferences.AsNoTracking().ToArrayAsync(),
            jobs = await db.BackgroundJobs.AsNoTracking().ToArrayAsync(),
            schoolManager = await db.SchoolManagerSettings.AsNoTracking().ToArrayAsync(),
            passFailImports = await db.PassFailImports.AsNoTracking().ToArrayAsync(),
            guardianDeliveries = await db.GuardianDeliveries.AsNoTracking().ToArrayAsync(),
        });

    private sealed class Fixture : IAsyncDisposable
    {
        public const string ApiKey = "AIza-test-migration-retained-key-1234567890";
        public const string SchoolManagerPassword = "synthetic-school-manager-password";
        public static readonly AiCapabilityProbeResult PassedProbe =
            new(true, true, true, true, true, "passed", null, TimeSpan.FromMilliseconds(1));
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<OokiGraderDbContext> _options;
        public InMemoryAiSecretStore Secrets { get; } = new();
        public ProbeProvider Provider { get; } = new();
        public ApprovedPromptBundleCatalog Catalog { get; } = new();

        private Fixture(SqliteConnection connection)
        {
            _connection = connection;
            _options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connection).Options;
        }

        public OokiGraderDbContext CreateDbContext() => new(_options);

        public Task<bool> ApplyAsync(OokiGraderDbContext db) =>
            Gemini38UpgradeMigration.ApplyAsync(
                db, Catalog, TimeProvider.System, AiProviderFeaturePolicy.AllowAll,
                Secrets, new AiProviderClientResolver([Provider]));

        public static async Task<Fixture> CreateAsync(string oldModel)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection);
            await using var db = fixture.CreateDbContext();
            await new OokiDatabaseInitializer(db, SystemClock.Instance).InitializeAsync(
                new OokiDatabaseInitializationOptions(Path.GetTempPath(), SchoolName: "移行テスト校"));
            var now = DateTimeOffset.UtcNow.AddDays(-10);
            var connectionId = UlidId.New(now);
            var actorId = UlidId.New(now);
            var reference = await fixture.Secrets.WriteAsync(
                connectionId, 7, ApiKey.AsMemory());
            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = connectionId, ModelId = oldModel, State = "active",
                SecretReference = reference.Value, KeyFingerprint = "retained-fingerprint",
                CredentialRevision = 7, TimeoutSeconds = 90, ConcurrencyLimit = 3,
                LastCapabilityProbeState = "passed", LastCapabilityProbeAt = now,
                LastBatchCapabilityProbeState = "passed",
                LastBatchCapabilityProbeCredentialRevision = 7,
                LastBatchCapabilityProbeAt = now,
                CreatedByStaffUserId = actorId, CreatedAt = now, UpdatedAt = now,
            });
            foreach (var task in new[] { AiTaskTypes.TemplateExtraction,
                AiTaskTypes.NameTranscription, AiTaskTypes.InitialGrading, AiTaskTypes.Adjudication })
            {
                db.AiTaskProfiles.Add(new AiTaskProfileEntity
                {
                    Id = UlidId.New(now), Name = "Historical " + task,
                    TaskType = task, AiConnectionId = connectionId,
                    ConnectionRevision = 7, ModelId = oldModel,
                    ProcessingStrategy = "queued_standard", PromptVersion = "old-prompt",
                    SchemaVersion = "old-schema", PromptContentHash = new string('b', 64),
                    ThinkingLevel = "minimal", MaxOutputTokens = 16_384,
                    ApprovalState = "production_approved", Active = true,
                    CreatedByStaffUserId = actorId, CreatedAt = now, UpdatedAt = now,
                });
            }
            var historicProfile = db.AiTaskProfiles.Local.First();
            db.AiRequests.Add(new AiRequestEntity
            {
                Id = UlidId.New(now), AiTaskProfileId = historicProfile.Id,
                TaskProfileRevision = 1, RequestKey = "historical-request", State = "succeeded",
                Purpose = "template_extraction", EntityType = "template_version",
                EntityId = UlidId.New(now), EntityRevision = 1,
                InputManifestHash = new string('c', 64), ActualModel = oldModel,
                ValidatedResponseJson = "{\"preserved\":true}", CreatedAt = now, UpdatedAt = now,
            });
            var studentId = UlidId.New(now);
            db.Students.Add(new StudentEntity
            {
                Id = studentId, StudentNumber = "009", StudentNumberNormalized = "009",
                FamilyName = "移行", GivenName = "生徒", FamilyNameNormalized = "移行",
                GivenNameNormalized = "生徒", DisplayName = "移行 生徒", PrivateNotes = "保存するメモ",
                CreatedAt = now, UpdatedAt = now,
            });
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = UlidId.New(now), Title = "既存の理科テスト", Subject = "理科",
                CreatedByStaffUserId = actorId, CreatedAt = now, UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = UlidId.New(now), Sha256 = new string('d', 64), Bytes = 1234,
                VerifiedMime = "application/pdf", Extension = ".pdf",
                RelativeObjectPath = "existing/original.pdf", StorageClass = "managed_scan",
                RetentionClass = "submission_original", State = "available", CreatedAt = now,
            });
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = UlidId.New(now), Type = "gemini_template_generation_unit",
                SchemaVersion = 1, DeduplicationKey = "failed-template-retry",
                PayloadJson = "{\"preservedUnit\":true}", State = "failed",
                ErrorCode = "AI_PROVIDER_UNAVAILABLE", NextAttemptAt = now,
                CreatedAt = now, UpdatedAt = now,
            });
            var schoolManagerSecret = await fixture.Secrets.WriteAsync(
                UlidId.New(now), 3, SchoolManagerPassword.AsMemory());
            db.SchoolManagerSettings.Add(new SchoolManagerSettingsEntity
            {
                Id = "school-manager", BaseUrl = "https://school-manager.example.jp/",
                Username = "synthetic-teacher", PasswordSecretReference = schoolManagerSecret.Value,
                CredentialRevision = 3, Enabled = true, DryRun = true,
                ActivationStartedAt = now, LastCredentialTestedAt = now.AddHours(1),
                CreatedAt = now, UpdatedAt = now.AddHours(1), Revision = 4,
            });
            var passFailImportId = UlidId.New(now);
            db.PassFailImports.Add(new PassFailImportEntity
            {
                Id = passFailImportId, SourceSha256 = new string('e', 64),
                SourceFileName = "既存成績表.xlsx", SourceLastWriteAt = now,
                State = "processed", RowCount = 12, DeliveryCount = 1,
                CreatedAt = now, UpdatedAt = now, ProcessedAt = now,
            });
            var deliveryId = UlidId.New(now);
            var attachmentObjectId = UlidId.New(now);
            var attachmentReferenceId = UlidId.New(now);
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = attachmentObjectId, Sha256 = new string('f', 64), Bytes = 2345,
                VerifiedMime = "application/pdf", Extension = "pdf",
                RelativeObjectPath = "existing/class-report.pdf", StorageClass = "ResultReport",
                RetentionClass = "result_report", State = "available", CreatedAt = now,
                VerifiedAt = now, ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = attachmentReferenceId, FileObjectId = attachmentObjectId,
                OwnerType = "guardian_delivery", OwnerId = deliveryId,
                Purpose = "pass_fail_table_pdf", RetentionAnchorAt = now, CreatedAt = now,
            });
            db.GuardianDeliveries.Add(new GuardianDeliveryEntity
            {
                Id = deliveryId, Kind = "pass_fail_table", StudentId = studentId,
                PassFailImportId = passFailImportId, FileReferenceId = attachmentReferenceId,
                SourceKey = $"existing-class-report:{passFailImportId}:{studentId}",
                AttachmentName = "既存成績表.pdf", State = "ready", NotBeforeAt = now,
                LastDryRunAt = now.AddHours(2), CreatedAt = now, UpdatedAt = now.AddHours(2),
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Catalog.Dispose();
            Secrets.Dispose();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ProbeProvider : IAiProviderClient
    {
        public string Provider => AiProviders.GeminiDirect;
        public AiCapabilityProbeResult Result { get; set; } = Fixture.PassedProbe;
        public int ProbeCount { get; private set; }
        public string? ProbedModel { get; private set; }
        public string? ProbedCredential { get; private set; }

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection, ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            ProbedModel = connection.ModelId;
            ProbedCredential = Encoding.UTF8.GetString(credentialUtf8.Span);
            return Task.FromResult(Result);
        }

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection, ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class AiEvaluationPersistenceTests
{
    [Fact]
    public async Task EvaluationRecordPinsProfileAndEvidenceSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = database.Clock.UtcNow;
        var fixture = CreateFixture(now);

        await using (var context = database.Factory.CreateDbContext())
        {
            context.StaffUsers.Add(fixture.Staff);
            context.AiConnections.Add(fixture.Connection);
            context.AiTaskProfiles.Add(fixture.Profile);
            context.AiEvaluationRecords.Add(fixture.Evaluation);
            await context.SaveChangesAsync();
        }

        await using var verification = database.Factory.CreateDbContext();
        var persisted = await verification.AiEvaluationRecords
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(fixture.Profile.Id, persisted.AiTaskProfileId);
        Assert.Equal(fixture.Profile.Revision, persisted.TaskProfileRevision);
        Assert.Equal(fixture.Connection.CredentialRevision, persisted.ConnectionRevision);
        Assert.Equal(fixture.Profile.PromptContentHash, persisted.PromptContentHash);
        Assert.Equal(new string('d', 64), persisted.DatasetSha256);
        Assert.Equal(new string('e', 64), persisted.EvidenceSha256);
        Assert.True(persisted.TeacherReviewOnly);
        Assert.Equal(0, persisted.CriticalFailureCount);
    }

    [Fact]
    public async Task EvaluationRecordRejectsInconsistentAccuracyEvidence()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = CreateFixture(database.Clock.UtcNow);
        fixture.Evaluation.AgreementBasisPoints = 9_000;
        fixture.Evaluation.LowerConfidenceBoundBasisPoints = 9_001;

        await using var context = database.Factory.CreateDbContext();
        context.StaffUsers.Add(fixture.Staff);
        context.AiConnections.Add(fixture.Connection);
        context.AiTaskProfiles.Add(fixture.Profile);
        context.AiEvaluationRecords.Add(fixture.Evaluation);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());
    }

    private static EvaluationFixture CreateFixture(DateTimeOffset now)
    {
        var staffId = UlidId.New(now);
        var connectionId = UlidId.New(now);
        var profileId = UlidId.New(now);
        var staff = new StaffUserEntity
        {
            Id = staffId,
            Username = "evaluation.admin",
            UsernameNormalized = "EVALUATION.ADMIN",
            DisplayName = "Evaluation Admin",
            PasswordHash = "unused",
            PasswordAlgorithm = "test",
            PasswordAlgorithmVersion = 1,
            Status = "active",
            CredentialChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var connection = new AiConnectionEntity
        {
            Id = connectionId,
            Provider = "geminiDirect",
            ModelId = "gemini-3.5-flash-lite",
            SecretReference = "secret:test",
            KeyFingerprint = "sha256:test",
            CredentialRevision = 3,
            State = "active",
            LastCapabilityProbeState = "passed",
            LastCapabilityProbeAt = now,
            CreatedByStaffUserId = staffId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var profile = new AiTaskProfileEntity
        {
            Id = profileId,
            Name = "Golden set candidate",
            TaskType = "initialGrading",
            AiConnectionId = connectionId,
            ConnectionRevision = connection.CredentialRevision,
            ModelId = connection.ModelId,
            ProcessingStrategy = "gemini_batch",
            PromptVersion = "initial-grading-v1.0.0",
            SchemaVersion = "initial-grading-response-v1.0.0",
            PromptContentHash = new string('c', 64),
            ThinkingLevel = "minimal",
            MediaResolution = "high",
            MaxOutputTokens = 8_192,
            ConcurrencyLimit = 2,
            ApprovalState = "capability_passed",
            CreatedByStaffUserId = staffId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var evaluation = new AiEvaluationRecordEntity
        {
            Id = UlidId.New(now),
            AiTaskProfileId = profileId,
            TaskProfileRevision = profile.Revision,
            Provider = connection.Provider,
            ModelId = profile.ModelId,
            ConnectionRevision = connection.CredentialRevision,
            TaskType = profile.TaskType,
            ProcessingStrategy = profile.ProcessingStrategy,
            PromptVersion = profile.PromptVersion,
            SchemaVersion = profile.SchemaVersion,
            PromptContentHash = profile.PromptContentHash,
            DatasetVersion = "school-golden-set-v1",
            DatasetSha256 = new string('d', 64),
            EvidenceSha256 = new string('e', 64),
            SampleCount = 200,
            AgreementBasisPoints = 9_950,
            LowerConfidenceBoundBasisPoints = 9_900,
            CriticalFailureCount = 0,
            TeacherReviewOnly = true,
            SignedOffByStaffUserId = staffId,
            CreatedAt = now,
        };
        return new EvaluationFixture(staff, connection, profile, evaluation);
    }

    private sealed record EvaluationFixture(
        StaffUserEntity Staff,
        AiConnectionEntity Connection,
        AiTaskProfileEntity Profile,
        AiEvaluationRecordEntity Evaluation);
}

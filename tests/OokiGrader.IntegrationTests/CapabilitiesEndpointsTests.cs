using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class CapabilitiesEndpointsTests
{
    [Fact]
    public async Task CapabilitiesAcceptAnActiveCapabilityPassedHealthyExactProfile()
    {
        await using var connection = new SqliteConnection(
            "Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new OokiGraderDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:Ai.TemplateGeneration"] = "true",
                ["Features:Ai.GeminiDirect"] = "true",
                ["Features:Grading.Semantic"] = "true",
                ["Features:Reports.Pdf"] = "true",
                ["Features:Recognition.AutoAssign"] = "false",
                ["Features:Grading.AutoFinalize"] = "false",
            })
            .Build();
        using var promptCatalog = new ApprovedPromptBundleCatalog();

        var unavailable = await CapabilitiesEndpoints.ReadAsync(
            db,
            configuration,
            promptCatalog,
            CancellationToken.None);
        Assert.True(unavailable.Reports.PdfExport);
        Assert.True(unavailable.Ai.TemplateGeneration.Enabled);
        Assert.False(unavailable.Ai.TemplateGeneration.Ready);
        Assert.False(unavailable.Ai.GeminiBatch.Ready);

        var now = new DateTimeOffset(
            2026,
            7,
            27,
            9,
            0,
            0,
            TimeSpan.Zero);
        var connectionId = UlidId.New(now);
        var aiConnection = new AiConnectionEntity
        {
            Id = connectionId,
            Provider = AiProviders.GeminiDirect,
            ModelId = "gemini-3.5-flash-lite",
            SecretReference = "test-secret-reference",
            KeyFingerprint = "sha256:test",
            State = "active",
            LastCapabilityProbeState = "passed",
            LastCapabilityProbeAt = now,
            LastBatchCapabilityProbeState = "passed",
            LastBatchCapabilityProbeAt = now,
            LastBatchCapabilityProbeCredentialRevision = 1,
            CreatedByStaffUserId = UlidId.New(now.AddSeconds(1)),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var templateBundle = promptCatalog.GetRequired(
            AiTaskTypes.TemplateExtraction);
        var gradingBundle = promptCatalog.GetRequired(
            AiTaskTypes.InitialGrading);
        db.AiConnections.Add(aiConnection);
        db.AiCapabilityProbes.Add(new AiCapabilityProbeEntity
        {
            Id = UlidId.New(now.AddMilliseconds(500)),
            AiConnectionId = aiConnection.Id,
            ConnectionRevision = aiConnection.CredentialRevision,
            State = "passed",
            Authentication = true,
            ModelAvailable = true,
            ImageInput = true,
            StructuredOutput = true,
            UsageMetadata = true,
            CreatedAt = now.AddMilliseconds(500),
            CompletedAt = now.AddMilliseconds(750),
        });
        db.AiTaskProfiles.AddRange(
            Profile(
                AiTaskTypes.TemplateExtraction,
                "queued_standard",
                aiConnection,
                templateBundle,
                now.AddSeconds(2)),
            Profile(
                AiTaskTypes.InitialGrading,
                "gemini_batch",
                aiConnection,
                gradingBundle,
                now.AddSeconds(3)));
        await db.SaveChangesAsync();

        var ready = await CapabilitiesEndpoints.ReadAsync(
            db,
            configuration,
            promptCatalog,
            CancellationToken.None);
        Assert.Equal(AiProviders.GeminiDirect, ready.Ai.Provider);
        Assert.Equal("gemini-3.5-flash-lite", ready.Ai.ModelId);
        Assert.True(ready.Ai.TemplateGeneration.Ready);
        Assert.True(ready.Ai.SemanticGrading.Ready);
        Assert.True(ready.Ai.GeminiBatch.Ready);
        Assert.False(ready.Ai.OpenRouterEnabled);
        Assert.False(ready.Safety.AutomaticAssignment);
        Assert.False(ready.Safety.AutomaticFinalization);

        aiConnection.LastBatchCapabilityProbeState = "failed";
        aiConnection.LastBatchCapabilityProbeErrorCode =
            "gemini_batch_probe_failed";
        await db.SaveChangesAsync();
        var batchUnavailable = await CapabilitiesEndpoints.ReadAsync(
            db,
            configuration,
            promptCatalog,
            CancellationToken.None);
        Assert.True(batchUnavailable.Ai.TemplateGeneration.Ready);
        Assert.False(batchUnavailable.Ai.SemanticGrading.Ready);
        Assert.False(batchUnavailable.Ai.GeminiBatch.Ready);
    }

    [Fact]
    public async Task OpenRouterImageModelIsReadyButDeepSeekV4FlashIsNot()
    {
        await using var connection = new SqliteConnection(
            "Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new OokiGraderDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:Ai.TemplateGeneration"] = "true",
                ["Features:Ai.GeminiDirect"] = "false",
                ["Features:Ai.OpenRouter"] = "true",
                ["Features:Grading.Semantic"] = "true",
            })
            .Build();
        using var promptCatalog = new ApprovedPromptBundleCatalog();
        var now = new DateTimeOffset(
            2026,
            8,
            6,
            9,
            0,
            0,
            TimeSpan.Zero);
        var aiConnection = new AiConnectionEntity
        {
            Id = UlidId.New(now),
            Provider = AiProviders.OpenRouter,
            EndpointProfile = AiProviderCatalog.OpenRouterEndpointProfile,
            ModelId = "google/gemini-3.1-flash-lite",
            SecretReference = "test-openrouter-secret-reference",
            KeyFingerprint = "sha256:openrouter-test",
            State = "active",
            LastCapabilityProbeState = "passed",
            LastCapabilityProbeAt = now,
            CreatedByStaffUserId = UlidId.New(now.AddSeconds(1)),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var templateBundle = promptCatalog.GetRequired(
            AiTaskTypes.TemplateExtraction);
        var gradingBundle = promptCatalog.GetRequired(
            AiTaskTypes.InitialGrading);
        db.AiConnections.Add(aiConnection);
        db.AiCapabilityProbes.Add(new AiCapabilityProbeEntity
        {
            Id = UlidId.New(now.AddMilliseconds(500)),
            AiConnectionId = aiConnection.Id,
            ConnectionRevision = aiConnection.CredentialRevision,
            State = "passed",
            Authentication = true,
            ModelAvailable = true,
            ImageInput = true,
            StructuredOutput = true,
            UsageMetadata = true,
            CreatedAt = now.AddMilliseconds(500),
            CompletedAt = now.AddMilliseconds(750),
        });
        db.AiTaskProfiles.AddRange(
            Profile(
                AiTaskTypes.TemplateExtraction,
                "queued_standard",
                aiConnection,
                templateBundle,
                now.AddSeconds(2)),
            Profile(
                AiTaskTypes.InitialGrading,
                "queued_standard",
                aiConnection,
                gradingBundle,
                now.AddSeconds(3)));
        await db.SaveChangesAsync();

        var imageCapable = await CapabilitiesEndpoints.ReadAsync(
            db,
            configuration,
            promptCatalog,
            CancellationToken.None);

        Assert.True(imageCapable.Ai.OpenRouterEnabled);
        Assert.True(imageCapable.Ai.TemplateGeneration.Enabled);
        Assert.True(imageCapable.Ai.TemplateGeneration.Ready);
        Assert.True(imageCapable.Ai.SemanticGrading.Ready);
        Assert.False(imageCapable.Ai.GeminiBatch.Enabled);
        Assert.False(imageCapable.Ai.GeminiBatch.Ready);

        aiConnection.ModelId = AiProviderCatalog.DeepSeekV4FlashModelId;
        foreach (var profile in db.AiTaskProfiles.Local)
        {
            profile.ModelId = AiProviderCatalog.DeepSeekV4FlashModelId;
        }

        await db.SaveChangesAsync();
        var textOnlyModel = await CapabilitiesEndpoints.ReadAsync(
            db,
            configuration,
            promptCatalog,
            CancellationToken.None);

        Assert.True(textOnlyModel.Ai.TemplateGeneration.Enabled);
        Assert.False(textOnlyModel.Ai.TemplateGeneration.Ready);
        Assert.True(textOnlyModel.Ai.SemanticGrading.Enabled);
        Assert.False(textOnlyModel.Ai.SemanticGrading.Ready);
    }

    private static AiTaskProfileEntity Profile(
        string taskType,
        string processingStrategy,
        AiConnectionEntity connection,
        AiPromptBundle bundle,
        DateTimeOffset now) =>
        new()
        {
            Id = UlidId.New(now),
            Name = taskType,
            TaskType = taskType,
            AiConnectionId = connection.Id,
            ConnectionRevision = connection.CredentialRevision,
            ModelId = connection.ModelId,
            ProcessingStrategy = processingStrategy,
            PromptVersion = bundle.PromptVersion,
            SchemaVersion = bundle.SchemaVersion,
            PromptContentHash = bundle.ContentHash,
            ApprovalState = "capability_passed",
            Active = true,
            ActivatedAt = now,
            CreatedByStaffUserId = UlidId.New(now.AddMilliseconds(1)),
            CreatedAt = now,
            UpdatedAt = now,
        };
}

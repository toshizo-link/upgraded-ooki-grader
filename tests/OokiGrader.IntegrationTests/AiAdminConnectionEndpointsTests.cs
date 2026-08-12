using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Security;

namespace OokiGrader.IntegrationTests;

public sealed class AiAdminConnectionEndpointsTests
{
    private const string GeminiModel = "gemini-3.5-flash-lite";
    private const string OpenRouterModel = "google/gemini-3.1-flash-lite";

    [Fact]
    public async Task GeminiAndOpenRouterCanCoexistButDuplicateProviderIsRejected()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();

        var gemini = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-test-gemini-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel));
        var openRouter = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        var duplicate = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-replacement-key-123456789",
                AiProviders.OpenRouter,
                "google/gemini-3.1-pro-preview"));

        Assert.Equal(HttpStatusCode.Created, gemini.StatusCode);
        Assert.Equal(HttpStatusCode.Created, openRouter.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("AI_CONNECTION_EXISTS", await ProblemCodeAsync(duplicate));

        var list = await application.GetAsync("/api/v1/admin/ai-connections");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var document = await ReadJsonAsync(list);
        var items = document.RootElement.GetProperty("items").EnumerateArray()
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(
            items,
            item => item.GetProperty("provider").GetString()
                == AiProviders.GeminiDirect
                && item.GetProperty("modelId").GetString() == GeminiModel);
        Assert.Contains(
            items,
            item => item.GetProperty("provider").GetString()
                == AiProviders.OpenRouter
                && item.GetProperty("modelId").GetString() == OpenRouterModel);
    }

    [Fact]
    public async Task AutomaticGeminiSetupProbesBeforeSavingAndEnablesAllProfiles()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        const string apiKey = "AIza-auto-enable-gemini-key-1234567890";

        var response = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                apiKey,
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Equal(
            "active",
            document.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            "passed",
            document.RootElement.GetProperty("lastCapabilityProbe")
                .GetProperty("state")
                .GetString());
        Assert.Equal(1, application.GeminiClient.ProbeCount);
        Assert.Equal(1, application.StoredSecretCount);

        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections.AsNoTracking().SingleAsync();
            Assert.Equal(apiKey, await application.ReadStoredSecretAsync(
                connection.SecretReference));
            var probe = await db.AiCapabilityProbes.AsNoTracking().SingleAsync();
            Assert.Equal(connection.CredentialRevision, probe.ConnectionRevision);
            var profiles = await db.AiTaskProfiles
                .AsNoTracking()
                .OrderBy(profile => profile.TaskType)
                .ToArrayAsync();
            Assert.Equal(4, profiles.Length);
            Assert.All(profiles, profile =>
            {
                Assert.True(profile.Active);
                Assert.Equal("capability_passed", profile.ApprovalState);
                Assert.Equal(connection.CredentialRevision, profile.ConnectionRevision);
                Assert.Equal("test-prompt-v1", profile.PromptVersion);
                Assert.Equal("test-schema-v1", profile.SchemaVersion);
                Assert.Equal(new string('a', 64), profile.PromptContentHash);
            });
        });
    }

    [Fact]
    public async Task FailedAutomaticGeminiSetupPersistsNothing()
    {
        var failedProbe = new AiCapabilityProbeResult(
            Authentication: false,
            ModelAvailable: true,
            ImageInput: false,
            StructuredOutput: false,
            UsageMetadata: false,
            State: "failed",
            SafeErrorCode: "gemini_authentication_failed",
            Latency: null);
        await using var application = await AiAdminTestApplication.CreateAsync(
            geminiProbe: failedProbe);

        var response = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-invalid-gemini-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("AI_CONNECTION_TEST_FAILED", await ProblemCodeAsync(response));
        Assert.Equal(1, application.GeminiClient.ProbeCount);
        Assert.Equal(0, application.StoredSecretCount);
        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(0, await db.AiConnections.CountAsync());
            Assert.Equal(0, await db.AiCapabilityProbes.CountAsync());
            Assert.Equal(0, await db.AiTaskProfiles.CountAsync());
        });
    }

    [Fact]
    public async Task FailedAutomaticGeminiReplacementKeepsOldKeyAndProfilesActive()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        const string originalKey = "AIza-original-gemini-key-1234567890";
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                originalKey,
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = Assert.IsType<string>(createdDocument.RootElement
            .GetProperty("id").GetString());
        var revision = createdDocument.RootElement.GetProperty("revision").GetInt64();

        application.GeminiClient.Result = new AiCapabilityProbeResult(
            Authentication: false,
            ModelAvailable: true,
            ImageInput: false,
            StructuredOutput: false,
            UsageMetadata: false,
            State: "failed",
            SafeErrorCode: "gemini_authentication_failed",
            Latency: null);
        var replaced = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "AIza-bad-replacement-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel,
                revision,
                testAndEnable: true));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, replaced.StatusCode);
        Assert.Equal(1, application.StoredSecretCount);
        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(1, connection.CredentialRevision);
            Assert.Equal(revision, connection.Revision);
            Assert.Equal("active", connection.State);
            Assert.Equal(originalKey, await application.ReadStoredSecretAsync(
                connection.SecretReference));
            Assert.Equal(4, await db.AiTaskProfiles.CountAsync(
                profile => profile.Active));
        });
    }

    [Fact]
    public async Task AutomaticReplacementRechecksRevisionAfterSlowProbe()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        const string originalKey = "AIza-race-original-key-1234567890";
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                originalKey,
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = Assert.IsType<string>(createdDocument.RootElement
            .GetProperty("id").GetString());
        var revision = createdDocument.RootElement.GetProperty("revision").GetInt64();

        application.GeminiClient.BeforeProbeReturnsAsync = async () =>
        {
            application.GeminiClient.BeforeProbeReturnsAsync = null;
            await application.WithDatabaseAsync(async db =>
            {
                var connection = await db.AiConnections.SingleAsync(item =>
                    item.Id == connectionId);
                connection.TimeoutSeconds = 76;
                await db.SaveChangesAsync();
            });
        };
        var replaced = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "AIza-race-next-key-0987654321",
                AiProviders.GeminiDirect,
                GeminiModel,
                revision,
                testAndEnable: true));

        Assert.Equal(HttpStatusCode.PreconditionFailed, replaced.StatusCode);
        Assert.Equal("REVISION_MISMATCH", await ProblemCodeAsync(replaced));
        Assert.Equal(1, application.StoredSecretCount);
        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(1, connection.CredentialRevision);
            Assert.Equal(76, connection.TimeoutSeconds);
            Assert.Equal(originalKey, await application.ReadStoredSecretAsync(
                connection.SecretReference));
            Assert.Equal(4, await db.AiTaskProfiles.CountAsync(
                profile => profile.Active));
        });
    }

    [Fact]
    public async Task SuccessfulAutomaticGeminiReplacementAtomicallySwitchesProfilesAndKey()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-first-gemini-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = Assert.IsType<string>(createdDocument.RootElement
            .GetProperty("id").GetString());
        var revision = createdDocument.RootElement.GetProperty("revision").GetInt64();

        const string replacementKey =
            "AIza-second-gemini-key-0987654321";
        var replaced = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                replacementKey,
                AiProviders.GeminiDirect,
                GeminiModel,
                revision,
                testAndEnable: true));

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(1, application.StoredSecretCount);
        Assert.Equal(2, application.GeminiClient.ProbeCount);
        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(2, connection.CredentialRevision);
            Assert.Equal("active", connection.State);
            Assert.Equal(replacementKey, await application.ReadStoredSecretAsync(
                connection.SecretReference));
            Assert.Equal(2, await db.AiCapabilityProbes.CountAsync());
            Assert.Equal(8, await db.AiTaskProfiles.CountAsync());
            var active = await db.AiTaskProfiles
                .AsNoTracking()
                .Where(profile => profile.Active)
                .ToArrayAsync();
            Assert.Equal(4, active.Length);
            Assert.All(active, profile => Assert.Equal(
                connection.CredentialRevision,
                profile.ConnectionRevision));
        });
    }

    [Fact]
    public async Task SupersededSecretCleanupFailureDoesNotUndoSuccessfulReplacement()
    {
        var secretStore = new ThrowingDeleteSecretStore();
        await using var application = await AiAdminTestApplication.CreateAsync(
            secretStore: secretStore);
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-cleanup-first-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = Assert.IsType<string>(createdDocument.RootElement
            .GetProperty("id").GetString());
        var revision = createdDocument.RootElement.GetProperty("revision").GetInt64();

        secretStore.ThrowOnDelete = true;
        const string replacementKey = "AIza-cleanup-next-key-0987654321";
        var replaced = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                replacementKey,
                AiProviders.GeminiDirect,
                GeminiModel,
                revision,
                testAndEnable: true));

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(2, connection.CredentialRevision);
            Assert.Equal(replacementKey, await application.ReadStoredSecretAsync(
                connection.SecretReference));
            Assert.Equal(4, await db.AiTaskProfiles.CountAsync(
                profile => profile.Active
                    && profile.ConnectionRevision == 2));
        });
    }

    [Fact]
    public async Task RepeatedGeminiConnectionTestSelfHealsWithoutDuplicateProfiles()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-manual-probe-gemini-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = createdDocument.RootElement.GetProperty("id").GetString();

        var first = await application.PostAsync(
            $"/api/v1/admin/ai-connections/{connectionId}:test");
        var second = await application.PostAsync(
            $"/api/v1/admin/ai-connections/{connectionId}:test");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(2, await db.AiCapabilityProbes.CountAsync());
            Assert.Equal(4, await db.AiTaskProfiles.CountAsync());
            Assert.Equal(4, await db.AiTaskProfiles.CountAsync(
                profile => profile.Active
                    && profile.ApprovalState == "capability_passed"));
        });
    }

    [Fact]
    public async Task FailedRecheckMarksExistingProfilesStaleForClients()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-recheck-failure-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = Assert.IsType<string>(createdDocument.RootElement
            .GetProperty("id")
            .GetString());

        application.GeminiClient.Result = new AiCapabilityProbeResult(
            Authentication: true,
            ModelAvailable: true,
            ImageInput: true,
            StructuredOutput: true,
            UsageMetadata: false,
            State: "passed",
            SafeErrorCode: "gemini_usage_metadata_missing",
            Latency: null);
        var recheck = await application.PostAsync(
            $"/api/v1/admin/ai-connections/{connectionId}:test");
        Assert.Equal(HttpStatusCode.OK, recheck.StatusCode);

        var profilesResponse = await application.GetAsync(
            "/api/v1/admin/ai-task-profiles");
        Assert.Equal(HttpStatusCode.OK, profilesResponse.StatusCode);
        using var profilesDocument = await ReadJsonAsync(profilesResponse);
        var profiles = profilesDocument.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(4, profiles.Length);
        Assert.All(profiles, profile => Assert.True(
            profile.GetProperty("stale").GetBoolean()));

        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal("blocked", connection.State);
            Assert.Equal(4, await db.AiTaskProfiles.CountAsync(
                profile => profile.Active));
        });
    }

    [Fact]
    public async Task StartupReconcileReplacesOnlyStaleGeminiProfiles()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-startup-reconcile-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            var stale = await db.AiTaskProfiles.SingleAsync(profile =>
                profile.TaskType == AiTaskTypes.TemplateExtraction
                && profile.Active);
            stale.PromptVersion = "old-prompt";
            stale.PromptContentHash = new string('b', 64);
            await db.SaveChangesAsync();
        });

        Assert.Equal(1, await application.EnsureCurrentProfilesAsync());
        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(5, await db.AiTaskProfiles.CountAsync());
            var active = await db.AiTaskProfiles
                .AsNoTracking()
                .Where(profile => profile.Active)
                .ToArrayAsync();
            Assert.Equal(4, active.Length);
            Assert.All(active, profile =>
            {
                Assert.Equal("test-prompt-v1", profile.PromptVersion);
                Assert.Equal("test-schema-v1", profile.SchemaVersion);
                Assert.Equal(new string('a', 64), profile.PromptContentHash);
            });
        });
    }

    [Fact]
    public async Task StartupReconcileDoesNotReplaceManuallyActiveOpenRouterProfile()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var gemini = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-gemini-startup-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel,
                testAndEnable: true));
        Assert.Equal(HttpStatusCode.Created, gemini.StatusCode);
        var openRouter = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-startup-key-12345678901234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, openRouter.StatusCode);
        using var openRouterDocument = await ReadJsonAsync(openRouter);
        var openRouterConnectionId = Assert.IsType<string>(
            openRouterDocument.RootElement.GetProperty("id").GetString());
        var probe = await application.PostAsync(
            $"/api/v1/admin/ai-connections/{openRouterConnectionId}:test");
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);

        string openRouterProfileId = string.Empty;
        await application.WithDatabaseAsync(async db =>
        {
            var geminiProfile = await db.AiTaskProfiles.SingleAsync(profile =>
                profile.TaskType == AiTaskTypes.TemplateExtraction
                && profile.Active);
            var openRouterProfile = await db.AiTaskProfiles.SingleAsync(profile =>
                profile.TaskType == AiTaskTypes.TemplateExtraction
                && profile.AiConnectionId == openRouterConnectionId);
            geminiProfile.Active = false;
            await db.SaveChangesAsync();
            openRouterProfile.Active = true;
            openRouterProfile.ActivatedAt = DateTimeOffset.UtcNow;
            openRouterProfile.ActivatedByStaffUserId =
                "01J00000000000000000000000";
            await db.SaveChangesAsync();
            openRouterProfileId = openRouterProfile.Id;
        });

        Assert.Equal(0, await application.EnsureCurrentProfilesAsync());
        await application.WithDatabaseAsync(async db =>
        {
            var activeTemplate = await db.AiTaskProfiles
                .AsNoTracking()
                .SingleAsync(profile =>
                    profile.TaskType == AiTaskTypes.TemplateExtraction
                    && profile.Active);
            Assert.Equal(openRouterProfileId, activeTemplate.Id);
            Assert.Equal(openRouterConnectionId, activeTemplate.AiConnectionId);
        });
    }

    [Fact]
    public async Task ReplacingAConnectionKeepsProviderButAllowsModelUpdate()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var createdRoot = createdDocument.RootElement;
        var connectionId = createdRoot.GetProperty("id").GetString();
        var revision = createdRoot.GetProperty("revision").GetInt64();
        Assert.False(string.IsNullOrWhiteSpace(connectionId));

        var providerChange = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "AIza-test-gemini-key-0987654321",
                AiProviders.GeminiDirect,
                GeminiModel,
                revision));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            providerChange.StatusCode);
        Assert.Equal(
            "AI_CONNECTION_PROVIDER_IMMUTABLE",
            await ProblemCodeAsync(providerChange));

        const string updatedModel = "google/gemini-3.1-pro-preview";
        var modelChange = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "sk-or-test-new-openrouter-key-0987654321",
                AiProviders.OpenRouter,
                updatedModel,
                revision));
        Assert.Equal(HttpStatusCode.OK, modelChange.StatusCode);
        using var updatedDocument = await ReadJsonAsync(modelChange);
        Assert.Equal(
            AiProviders.OpenRouter,
            updatedDocument.RootElement.GetProperty("provider").GetString());
        Assert.Equal(
            updatedModel,
            updatedDocument.RootElement.GetProperty("modelId").GetString());
        Assert.Equal(
            "pending_probe",
            updatedDocument.RootElement.GetProperty("state").GetString());
        Assert.True(
            updatedDocument.RootElement.GetProperty("revision").GetInt64()
                > revision);
    }

    [Fact]
    public async Task ReplacingLegacyMemoryReferenceMigratesWithoutCleanupFailure()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ooki-ai-endpoint-migration-{Guid.NewGuid():N}");
        var keyRingRoot = Path.Combine(root, "key-ring");
        Directory.CreateDirectory(keyRingRoot);
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(keyRingRoot),
            configuration => configuration.SetApplicationName(
                "OokiGrader.IntegrationTests.LegacyMigration"));
        var secretStore = new DataProtectionFileAiSecretStore(
            new DataProtectionFileAiSecretStoreOptions
            {
                RootPath = Path.Combine(root, "secrets"),
            },
            provider);

        try
        {
            await using var application = await AiAdminTestApplication.CreateAsync(
                secretStore: secretStore);
            var created = await application.PostAsync(
                "/api/v1/admin/ai-connections",
                ConnectionBody(
                    "sk-or-test-openrouter-key-1234567890",
                    AiProviders.OpenRouter,
                    OpenRouterModel));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdDocument = await ReadJsonAsync(created);
            var connectionId = Assert.IsType<string>(createdDocument.RootElement
                .GetProperty("id")
                .GetString());
            var revision = createdDocument.RootElement
                .GetProperty("revision")
                .GetInt64();

            await application.WithDatabaseAsync(async db =>
            {
                var connection = await db.AiConnections.SingleAsync(
                    item => item.Id == connectionId);
                connection.SecretReference =
                    $"memory-v1/{connectionId}/" +
                    "00000000000000000001.secret";
                await db.SaveChangesAsync();
                revision = connection.Revision;
            });

            const string replacementKey =
                "sk-or-test-persisted-replacement-key-0987654321";
            var replaced = await application.PutAsync(
                $"/api/v1/admin/ai-connections/{connectionId}",
                ConnectionBody(
                    replacementKey,
                    AiProviders.OpenRouter,
                    OpenRouterModel,
                    revision));

            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            string? migratedReference = null;
            await application.WithDatabaseAsync(async db =>
            {
                var connection = await db.AiConnections
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == connectionId);
                Assert.Equal(2, connection.CredentialRevision);
                Assert.StartsWith(
                    $"devfile-v1/{connectionId}/",
                    connection.SecretReference,
                    StringComparison.Ordinal);
                migratedReference = connection.SecretReference;
            });
            using var lease = await secretStore.ReadAsync(
                new AiSecretReference(Assert.IsType<string>(migratedReference)));
            Assert.Equal(
                replacementKey,
                Encoding.UTF8.GetString(lease.Utf8Bytes.Span));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacingOpenRouterConnectionIsRejectedWhenFeatureIsDisabled()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = createdDocument.RootElement
            .GetProperty("id")
            .GetString();
        var revision = createdDocument.RootElement
            .GetProperty("revision")
            .GetInt64();
        application.DisableOpenRouter();

        var response = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "sk-or-test-new-openrouter-key-0987654321",
                AiProviders.OpenRouter,
                "google/gemini-3.1-pro-preview",
                revision));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "AI_PROVIDER_FEATURE_DISABLED",
            await ProblemCodeAsync(response));
        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(OpenRouterModel, connection.ModelId);
            Assert.Equal(revision, connection.Revision);
            Assert.Equal(1, connection.CredentialRevision);
        });
    }

    [Fact]
    public async Task OpenRouterPricingUsesSelectedConnectionAndOfficialHost()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var invalidSource = await application.PostAsync(
            "/api/v1/admin/pricing-snapshots",
            PricingBody("https://ai.google.dev/gemini-api/docs/pricing"));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            invalidSource.StatusCode);
        Assert.Equal(
            "AI_PRICING_SNAPSHOT_INVALID",
            await ProblemCodeAsync(invalidSource));

        var saved = await application.PostAsync(
            "/api/v1/admin/pricing-snapshots",
            PricingBody("https://openrouter.ai/models"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        using var savedDocument = await ReadJsonAsync(saved);
        Assert.Equal(
            AiProviders.OpenRouter,
            savedDocument.RootElement.GetProperty("provider").GetString());
        Assert.Equal(
            OpenRouterModel,
            savedDocument.RootElement.GetProperty("modelId").GetString());

        var list = await application.GetAsync(
            "/api/v1/admin/pricing-snapshots");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDocument = await ReadJsonAsync(list);
        var snapshot = Assert.Single(
            listDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            AiProviders.OpenRouter,
            snapshot.GetProperty("provider").GetString());
        Assert.Equal(OpenRouterModel, snapshot.GetProperty("modelId").GetString());
    }

    [Fact]
    public async Task OpenRouterProbeWithoutImageSupportBlocksConnectionAndCreatesNoProfiles()
    {
        var openRouterProbe = new AiCapabilityProbeResult(
            Authentication: true,
            ModelAvailable: true,
            ImageInput: false,
            StructuredOutput: true,
            UsageMetadata: true,
            State: "passed",
            SafeErrorCode: null,
            Latency: TimeSpan.FromMilliseconds(25));
        await using var application = await AiAdminTestApplication.CreateAsync(
            openRouterProbe);
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = createdDocument.RootElement
            .GetProperty("id")
            .GetString();

        var probe = await application.PostAsync(
            $"/api/v1/admin/ai-connections/{connectionId}:test");

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        using var probeDocument = await ReadJsonAsync(probe);
        Assert.Equal(
            "passed",
            probeDocument.RootElement.GetProperty("state").GetString());
        Assert.False(
            probeDocument.RootElement.GetProperty("imageInput").GetBoolean());
        Assert.Equal(1, application.OpenRouterClient.ProbeCount);
        Assert.Equal(
            AiProviderCatalog.OpenRouterBaseAddress,
            application.OpenRouterClient.LastConnection?.BaseAddress);

        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(AiProviders.OpenRouter, connection.Provider);
            Assert.Equal("blocked", connection.State);
            Assert.Equal("passed", connection.LastCapabilityProbeState);
            Assert.Equal(1, await db.AiCapabilityProbes.CountAsync());
            Assert.Equal(0, await db.AiTaskProfiles.CountAsync());
        });
    }

    [Fact]
    public async Task ProbeWithMissingStoredSecretReturnsActionableConflict()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = createdDocument.RootElement
            .GetProperty("id")
            .GetString();
        application.RemoveStoredSecrets();

        var response = await application.PostAsync(
            $"/api/v1/admin/ai-connections/{connectionId}:test");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(responseBody);
        Assert.Equal(
            "AI_CONNECTION_SECRET_MISSING",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "保存済みのAI APIキーを読み込めません",
            problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "「APIキーを交換」からAPIキーを再登録し、もう一度接続を確認してください。",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("test-secret:", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sk-or-test-openrouter-key",
            responseBody,
            StringComparison.Ordinal);
        Assert.Equal(0, application.OpenRouterClient.ProbeCount);

        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal("pending_probe", connection.State);
            Assert.Equal(0, await db.AiCapabilityProbes.CountAsync());
        });
    }

    private static object ConnectionBody(
        string apiKey,
        string provider,
        string modelId,
        long? revision = null,
        bool testAndEnable = false) => new
        {
            apiKey,
            provider,
            modelId,
            timeoutSeconds = 75,
            concurrencyLimit = 2,
            revision,
            testAndEnable,
        };

    private static object PricingBody(string sourceUrl) => new
    {
        provider = AiProviders.OpenRouter,
        modelId = OpenRouterModel,
        inputUsdMicrosPerMillionTokens = 250_000,
        outputUsdMicrosPerMillionTokens = 1_500_000,
        thinkingUsdMicrosPerMillionTokens = 0,
        sourceUrl,
    };

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> ProblemCodeAsync(
        HttpResponseMessage response)
    {
        using var document = await ReadJsonAsync(response);
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed class AiAdminTestApplication : IAsyncDisposable
    {
        private static readonly DateTimeOffset UtcNow = new(
            2026,
            8,
            6,
            4,
            0,
            0,
            TimeSpan.Zero);

        private readonly IHost _host;
        private readonly SqliteConnection _connection;
        private readonly TestProviderFeaturePolicy _featurePolicy;

        private AiAdminTestApplication(
            IHost host,
            SqliteConnection connection,
            ProbeProviderClient geminiClient,
            ProbeProviderClient openRouterClient,
            TestProviderFeaturePolicy featurePolicy)
        {
            _host = host;
            _connection = connection;
            _featurePolicy = featurePolicy;
            GeminiClient = geminiClient;
            OpenRouterClient = openRouterClient;
            Client = host.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(5);
        }

        private HttpClient Client { get; }
        public ProbeProviderClient GeminiClient { get; }
        public ProbeProviderClient OpenRouterClient { get; }

        public static async Task<AiAdminTestApplication> CreateAsync(
            AiCapabilityProbeResult? openRouterProbe = null,
            IAiSecretStore? secretStore = null,
            AiCapabilityProbeResult? geminiProbe = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var geminiClient = new ProbeProviderClient(
                AiProviders.GeminiDirect,
                geminiProbe ?? PassedProbe());
            var openRouterClient = new ProbeProviderClient(
                AiProviders.OpenRouter,
                openRouterProbe ?? PassedProbe());
            secretStore ??= new InMemorySecretStore();
            var featurePolicy = new TestProviderFeaturePolicy();

            var hostBuilder = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddDataProtection();
                        services.AddSingleton<ProtectedCursorCodec>();
                        services.AddSingleton(connection);
                        services.AddSingleton<TimeProvider>(
                            new FixedTimeProvider(UtcNow));
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services.AddSingleton<IAiSecretStore>(secretStore);
                        services.AddSingleton<IAiProviderClient>(geminiClient);
                        services.AddSingleton<IAiProviderClient>(openRouterClient);
                        services.AddSingleton<IAiProviderClientResolver>(provider =>
                            new AiProviderClientResolver(
                                provider.GetServices<IAiProviderClient>()));
                        services.AddSingleton<IAiProviderFeaturePolicy>(
                            featurePolicy);
                        services.AddSingleton<IAiPromptBundleCatalog>(
                            new StubPromptBundleCatalog());
                        services
                            .AddAuthentication(
                                TestAuthenticationHandler.SchemeName)
                            .AddScheme<
                                AuthenticationSchemeOptions,
                                TestAuthenticationHandler>(
                                TestAuthenticationHandler.SchemeName,
                                _ => { });
                        services.AddAuthorizationBuilder()
                            .SetFallbackPolicy(
                                new AuthorizationPolicyBuilder(
                                    TestAuthenticationHandler.SchemeName)
                                    .RequireAuthenticatedUser()
                                    .Build())
                            .AddPolicy(
                                "administrator",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole("administrator"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseEndpoints(
                            endpoints => endpoints.MapAiAdminEndpoints());
                    });
                });

            var host = hostBuilder.Build();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return new AiAdminTestApplication(
                host,
                connection,
                geminiClient,
                openRouterClient,
                featurePolicy);
        }

        public void DisableOpenRouter() =>
            _featurePolicy.OpenRouterEnabled = false;

        public void RemoveStoredSecrets() =>
            ((InMemorySecretStore)_host.Services
                .GetRequiredService<IAiSecretStore>())
                .Clear();

        public int StoredSecretCount =>
            ((InMemorySecretStore)_host.Services
                .GetRequiredService<IAiSecretStore>())
                .Count;

        public async Task<string> ReadStoredSecretAsync(string reference)
        {
            var store = _host.Services.GetRequiredService<IAiSecretStore>();
            using var lease = await store.ReadAsync(
                new AiSecretReference(reference));
            return Encoding.UTF8.GetString(lease.Utf8Bytes.Span);
        }

        public Task<HttpResponseMessage> GetAsync(string path) =>
            SendAsync(HttpMethod.Get, path);

        public Task<HttpResponseMessage> PostAsync(
            string path,
            object? body = null) => SendAsync(HttpMethod.Post, path, body);

        public Task<HttpResponseMessage> PutAsync(
            string path,
            object body) => SendAsync(HttpMethod.Put, path, body);

        public async Task WithDatabaseAsync(
            Func<OokiGraderDbContext, Task> action)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            await action(db);
        }

        public async Task<int> EnsureCurrentProfilesAsync()
        {
            await using var scope = _host.Services.CreateAsyncScope();
            return await AiAdminEndpoints.EnsureCurrentProfilesAsync(
                scope.ServiceProvider.GetRequiredService<OokiGraderDbContext>(),
                scope.ServiceProvider
                    .GetRequiredService<IAiPromptBundleCatalog>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                _featurePolicy);
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add(
                TestAuthenticationHandler.RoleHeader,
                "administrator");
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }

        private static AiCapabilityProbeResult PassedProbe() => new(
            Authentication: true,
            ModelAvailable: true,
            ImageInput: true,
            StructuredOutput: true,
            UsageMetadata: true,
            State: "passed",
            SafeErrorCode: null,
            Latency: TimeSpan.FromMilliseconds(10));
    }

    public sealed class ProbeProviderClient(
        string provider,
        AiCapabilityProbeResult result) : IAiProviderClient
    {
        private int _probeCount;

        public string Provider { get; } = provider;
        public AiCapabilityProbeResult Result { get; set; } = result;
        public Func<Task>? BeforeProbeReturnsAsync { get; set; }
        public int ProbeCount => Volatile.Read(ref _probeCount);
        public AiConnectionSettings? LastConnection { get; private set; }

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default)
        {
            LastConnection = connection;
            Interlocked.Increment(ref _probeCount);
            if (BeforeProbeReturnsAsync is not null)
            {
                await BeforeProbeReturnsAsync();
            }

            return Result;
        }
    }

    private sealed class InMemorySecretStore : IAiSecretStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _secrets = new();

        public Task<AiSecretReference> WriteAsync(
            string ownerId,
            long credentialRevision,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            var reference = new AiSecretReference(
                $"test-secret:{ownerId}:{credentialRevision}");
            _secrets[reference.Value] = Encoding.UTF8.GetBytes(secret.ToString());
            return Task.FromResult(reference);
        }

        public Task<AiSecretLease> ReadAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default)
        {
            if (!_secrets.TryGetValue(reference.Value, out var bytes))
            {
                throw new KeyNotFoundException("Test secret was not found.");
            }

            return Task.FromResult(AiSecretLease.CopyFrom(bytes));
        }

        public Task<bool> DeleteAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryRemove(reference.Value, out _));

        public int Count => _secrets.Count;

        public void Clear() => _secrets.Clear();
    }

    private sealed class ThrowingDeleteSecretStore : IAiSecretStore
    {
        private readonly InMemorySecretStore _inner = new();

        public bool ThrowOnDelete { get; set; }

        public Task<AiSecretReference> WriteAsync(
            string ownerId,
            long credentialRevision,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(
                ownerId,
                credentialRevision,
                secret,
                cancellationToken);

        public Task<AiSecretLease> ReadAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(reference, cancellationToken);

        public Task<bool> DeleteAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default) =>
            ThrowOnDelete
                ? throw new IOException("Injected secret cleanup failure.")
                : _inner.DeleteAsync(reference, cancellationToken);
    }

    private sealed class StubPromptBundleCatalog : IAiPromptBundleCatalog
    {
        private static readonly JsonElement Schema =
            JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}");

        public AiPromptBundle GetRequired(string taskType) => new(
            taskType,
            "test-prompt-v1",
            "test-schema-v1",
            "Test prompt",
            Schema,
            new string('a', 64));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestProviderFeaturePolicy : IAiProviderFeaturePolicy
    {
        public bool OpenRouterEnabled { get; set; } = true;

        public bool IsEnabled(string provider) => provider switch
        {
            AiProviders.GeminiDirect => true,
            AiProviders.OpenRouter => OpenRouterEnabled,
            _ => false,
        };
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string SchemeName = "AiAdminConnectionIntegrationTest";
        public const string RoleHeader = "X-Test-Role";
        private const string AdministratorId =
            "01J00000000000000000000000";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, AdministratorId),
                    new Claim(ClaimTypes.Name, "ai-admin-test-user"),
                    new Claim(ClaimTypes.Role, role),
                ],
                SchemeName,
                ClaimTypes.Name,
                ClaimTypes.Role);
            return Task.FromResult(
                AuthenticateResult.Success(
                    new AuthenticationTicket(
                        new ClaimsPrincipal(identity),
                        SchemeName)));
        }
    }
}

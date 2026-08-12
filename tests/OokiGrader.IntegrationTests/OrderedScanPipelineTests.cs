using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;
using OokiGrader.Preprocessing;
using SkiaSharp;

namespace OokiGrader.IntegrationTests;

public sealed class OrderedScanPipelineTests
{
    [Theory]
    [InlineData(TestType.Hop, 1)]
    [InlineData(TestType.Step, 2)]
    [InlineData(TestType.ClassPlacement, 3)]
    [InlineData(TestType.Other, 3)]
    [InlineData(TestType.Other, 4)]
    public async Task OrderedOnePageUploadsAssembleAndPreprocessEveryTestType(
        TestType testType,
        int expectedPageCount)
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(testType, expectedPageCount);
        const int submissionCount = 2;
        var manifest = CreateManifest(expectedPageCount, submissionCount);

        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);
        var staged = manifest
            .Select(item => fixture.UploadPageAsync(
                seeded.SessionId,
                batch.Id,
                item,
                seeded.Pages[item.InputOrdinal - 1]))
            .Reverse()
            .ToArray();
        await Task.WhenAll(staged);

        var ready = await fixture.GetBatchAsync(batch.Id);
        Assert.All(ready.Items, item => Assert.Equal("uploaded", item.Status));
        await fixture.FinalizeBatchAsync(batch.Id, ready.RowVersion);
        Assert.True(await fixture.AssemblyWorker.ProcessNextAsync());

        var completed = await fixture.GetBatchAsync(batch.Id);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(expectedPageCount, completed.ExpectedPageCount);
        Assert.Equal(submissionCount, completed.SubmissionIds.Length);
        Assert.Equal(submissionCount, completed.Groups.Length);
        Assert.All(completed.Groups, group => Assert.Equal("complete", group.Status));
        Assert.Equal(
            manifest.Select(item => item.ClientItemId),
            completed.Items
                .OrderBy(item => item.InputOrdinal)
                .Select(item => item.ClientItemId));

        for (var index = 0; index < submissionCount; index++)
        {
            Assert.True(await fixture.PreprocessingWorker.ProcessNextAsync());
        }

        await fixture.WithDatabaseAsync(async db =>
        {
            var submissions = await db.Submissions
                .AsNoTracking()
                .Where(item => completed.SubmissionIds.Contains(item.Id))
                .OrderBy(item => item.OrderedScanGroupOrdinal)
                .ToArrayAsync();
            Assert.Equal(submissionCount, submissions.Length);
            Assert.All(submissions, submission =>
            {
                Assert.Equal("needs_name_review", submission.State);
                Assert.Equal(expectedPageCount, submission.PageCount);
                Assert.NotNull(submission.AssemblyManifestHash);
                Assert.NotNull(submission.PreprocessingManifestHash);
            });

            var pages = await db.SubmissionPages
                .AsNoTracking()
                .Where(item => completed.SubmissionIds.Contains(item.SubmissionId))
                .ToArrayAsync();
            Assert.Equal(submissionCount * expectedPageCount, pages.Length);
            Assert.All(
                submissions,
                submission => Assert.Equal(
                    Enumerable.Range(1, expectedPageCount),
                    pages.Where(item => item.SubmissionId == submission.Id)
                        .OrderBy(item => item.PageNumber)
                        .Select(item => item.PageNumber)));

            var sourcePages = await db.SubmissionSourcePages
                .AsNoTracking()
                .Where(item => completed.SubmissionIds.Contains(item.SubmissionId))
                .ToArrayAsync();
            Assert.Equal(submissionCount * expectedPageCount, sourcePages.Length);
            Assert.All(sourcePages, item =>
            {
                Assert.NotNull(item.FileReferenceId);
                Assert.Equal(64, item.SourceSha256.Length);
                Assert.Equal(1, item.SourcePageNumber);
            });
        });
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("retired")]
    public async Task OpenSessionKeepsAcceptingScansFromPinnedImmutableVersion(
        string versionState)
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(
            TestType.Hop,
            expectedPageCount: 1,
            scanGroupCount: 1);
        await fixture.WithDatabaseAsync(async db =>
        {
            var session = await db.TestSessions
                .Include(item => item.TemplateVersion)
                .ThenInclude(item => item.TestTemplate)
                .SingleAsync(item => item.Id == seeded.SessionId);
            session.TemplateVersion.State = versionState;
            session.TemplateVersion.TestTemplate.State = "archived";
            await db.SaveChangesAsync();
        });

        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 1);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);
        await fixture.UploadPageAsync(
            seeded.SessionId,
            batch.Id,
            manifest[0],
            seeded.Pages[0]);
        var uploaded = await fixture.GetBatchAsync(batch.Id);
        Assert.Equal("uploaded", Assert.Single(uploaded.Items).Status);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(50)]
    public async Task ArbitraryOtherAssemblesLargeOrderedSubmissionWithinBounds(
        int expectedPageCount)
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(
            TestType.Other,
            expectedPageCount,
            scanGroupCount: 1);
        var manifest = CreateManifest(expectedPageCount, submissionCount: 1);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);

        foreach (var uploadWave in manifest.Reverse().Chunk(3))
        {
            await Task.WhenAll(uploadWave.Select(item => fixture.UploadPageAsync(
                seeded.SessionId,
                batch.Id,
                item,
                seeded.Pages[item.InputOrdinal - 1])));
        }
        var ready = await fixture.GetBatchAsync(batch.Id);
        await fixture.FinalizeBatchAsync(batch.Id, ready.RowVersion);

        Assert.True(await fixture.AssemblyWorker.ProcessNextAsync());
        var completed = await fixture.GetBatchAsync(batch.Id);
        Assert.True(
            completed.Status == "completed",
            $"Batch status {completed.Status}: "
            + string.Join(", ", completed.Issues.Select(item => item.Code)));
        var submissionId = Assert.Single(completed.SubmissionIds);
        Assert.True(await fixture.PreprocessingWorker.ProcessNextAsync());

        await fixture.WithDatabaseAsync(async db =>
        {
            var persistedBatch = await db.OrderedScanBatches
                .AsNoTracking()
                .SingleAsync(item => item.Id == batch.Id);
            var submission = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == submissionId);
            Assert.Null(persistedBatch.LastErrorCode);
            Assert.Equal(expectedPageCount, submission.PageCount);
            Assert.Equal(
                expectedPageCount,
                await db.SubmissionSourcePages.CountAsync(
                    item => item.SubmissionId == submissionId));
        });
    }

    [Fact]
    public async Task SwappedStepRolesNeedReviewWithoutCreatingSubmission()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(TestType.Step, 2);
        var manifest = CreateManifest(expectedPageCount: 2, submissionCount: 1);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);

        await fixture.UploadPageAsync(
            seeded.SessionId,
            batch.Id,
            manifest[0],
            seeded.Pages[1]);
        await fixture.UploadPageAsync(
            seeded.SessionId,
            batch.Id,
            manifest[1],
            seeded.Pages[0]);
        var ready = await fixture.GetBatchAsync(batch.Id);
        await fixture.FinalizeBatchAsync(batch.Id, ready.RowVersion);

        Assert.True(await fixture.AssemblyWorker.ProcessNextAsync());

        var result = await fixture.GetBatchAsync(batch.Id);
        Assert.Equal("needsReview", result.Status);
        Assert.Empty(result.SubmissionIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "ORDERED_SCAN_PAGE_ORDER_MISMATCH");
    }

    [Fact]
    public async Task RepeatedSourcePageNeedsReviewWithoutDuplicateAttempt()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(TestType.Hop, 1);
        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 2);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);

        await fixture.UploadPageAsync(
            seeded.SessionId,
            batch.Id,
            manifest[0],
            seeded.Pages[0]);
        await fixture.UploadPageAsync(
            seeded.SessionId,
            batch.Id,
            manifest[1],
            seeded.Pages[0]);
        var ready = await fixture.GetBatchAsync(batch.Id);
        await fixture.FinalizeBatchAsync(batch.Id, ready.RowVersion);

        Assert.True(await fixture.AssemblyWorker.ProcessNextAsync());

        var result = await fixture.GetBatchAsync(batch.Id);
        Assert.Equal("needsReview", result.Status);
        Assert.Empty(result.SubmissionIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "ORDERED_SCAN_EXACT_DUPLICATE_PAGE");
    }

    [Fact]
    public async Task AssemblyRejectsTemplateReferenceWithWrongProvenance()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(
            TestType.Hop,
            expectedPageCount: 1,
            corruptReferenceProvenance: true);
        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 1);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);
        await fixture.UploadPageAsync(
            seeded.SessionId,
            batch.Id,
            manifest[0],
            seeded.Pages[0]);
        var ready = await fixture.GetBatchAsync(batch.Id);
        await fixture.FinalizeBatchAsync(batch.Id, ready.RowVersion);

        Assert.True(await fixture.AssemblyWorker.ProcessNextAsync());

        await fixture.WithDatabaseAsync(async db =>
        {
            var persisted = await db.OrderedScanBatches
                .AsNoTracking()
                .SingleAsync(item => item.Id == batch.Id);
            Assert.Equal(OrderedScanBatchStatus.Failed, persisted.Status);
            Assert.Equal(
                "ordered_scan_template_reference_invalid",
                persisted.LastErrorCode);
            Assert.Empty(await db.Submissions
                .Where(item => item.OrderedScanBatchId == batch.Id)
                .ToArrayAsync());
        });
    }

    [Fact]
    public async Task LegacyPageCountResolutionRejectsWrongSourceProvenance()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(
            TestType.Other,
            expectedPageCount: 3,
            corruptReferenceProvenance: true,
            persistExpectedPageCount: false);
        var manifest = CreateManifest(expectedPageCount: 3, submissionCount: 1);

        using var response = await fixture.SendCreateBatchAsync(
            seeded.SessionId,
            manifest);

        await AssertProblemCodeAsync(
            response,
            HttpStatusCode.Conflict,
            "TEMPLATE_SUBMISSION_PAGE_COUNT_MISSING");
    }

    [Fact]
    public async Task UploadBindingEnforcesOwnerManifestAndImmutableRetryMetadata()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(TestType.Hop, 1);
        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 1);
        var item = Assert.Single(manifest);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);
        var bytes = seeded.Pages[0];

        var forbidden = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item,
            bytes,
            OrderedScanFixture.SecondOperatorId);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var wrongName = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item with { FileName = "renamed.pdf" },
            bytes,
            OrderedScanFixture.OwnerId);
        await AssertProblemCodeAsync(
            wrongName,
            HttpStatusCode.Conflict,
            "ORDERED_SCAN_MANIFEST_FILE_MISMATCH");

        var created = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item,
            bytes,
            OrderedScanFixture.OwnerId);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = await ReadJsonAsync(created);
        var uploadId = createdJson.RootElement
            .GetProperty("uploadId")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(uploadId));

        var retry = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item,
            bytes,
            OrderedScanFixture.OwnerId);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var retryJson = await ReadJsonAsync(retry);
        Assert.Equal(
            uploadId,
            retryJson.RootElement.GetProperty("uploadId").GetString());

        var mismatch = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item,
            [.. bytes, (byte)0],
            OrderedScanFixture.OwnerId);
        await AssertProblemCodeAsync(
            mismatch,
            HttpStatusCode.Conflict,
            "ORDERED_SCAN_UPLOAD_RETRY_MISMATCH");
    }

    [Fact]
    public async Task FailedPageUploadCanBeReplacedWithoutLeavingPromotedBytes()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(TestType.Hop, 1);
        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 1);
        var item = Assert.Single(manifest);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);
        var bytes = seeded.Pages[0];
        using var created = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item,
            bytes,
            OrderedScanFixture.OwnerId,
            expectedSha256: new string('0', 64));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = await ReadJsonAsync(created);
        var failedUploadId = createdJson.RootElement
            .GetProperty("uploadId")
            .GetString()!;
        await fixture.AppendUploadAsync(failedUploadId, bytes);

        using var failed = await fixture.FinalizeUploadAsync(failedUploadId);
        await AssertProblemCodeAsync(
            failed,
            HttpStatusCode.UnprocessableEntity,
            "UPLOAD_HASH_MISMATCH");

        string failedIncomingPath = string.Empty;
        await fixture.WithDatabaseAsync(async db =>
        {
            var upload = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(value => value.Id == failedUploadId);
            failedIncomingPath = upload.IncomingRelativePath;
            Assert.Equal("failed", upload.State);
            Assert.Empty(await db.FileObjects
                .Where(value => value.ManagedScanBytes)
                .ToArrayAsync());
        });
        Assert.True(fixture.IncomingFileExists(failedIncomingPath));

        using var replacement = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item,
            bytes,
            OrderedScanFixture.OwnerId);
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        using var replacementJson = await ReadJsonAsync(replacement);
        var replacementUploadId = replacementJson.RootElement
            .GetProperty("uploadId")
            .GetString();
        Assert.NotEqual(failedUploadId, replacementUploadId);
        Assert.False(fixture.IncomingFileExists(failedIncomingPath));
        await fixture.WithDatabaseAsync(async db =>
        {
            var planned = await db.OrderedScanItems
                .AsNoTracking()
                .SingleAsync(value => value.BatchId == batch.Id);
            Assert.Equal(replacementUploadId, planned.UploadSessionId);
        });
    }

    [Fact]
    public async Task FinalizeDoesNotAttachWhileDeduplicatedObjectIsBeingDeleted()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(TestType.Hop, 1);
        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 1);
        var item = Assert.Single(manifest);
        var bytes = seeded.Pages[0];

        var firstBatch = await fixture.CreateBatchAsync(
            seeded.SessionId,
            manifest);
        await fixture.UploadPageAsync(
            seeded.SessionId,
            firstBatch.Id,
            item,
            bytes);

        var secondBatch = await fixture.CreateBatchAsync(
            seeded.SessionId,
            manifest);
        using var created = await fixture.CreateUploadAsync(
            seeded.SessionId,
            secondBatch.Id,
            item,
            bytes,
            OrderedScanFixture.OwnerId);
        using var createdJson = await ReadJsonAsync(created);
        var uploadId = createdJson.RootElement
            .GetProperty("uploadId")
            .GetString()!;
        await fixture.AppendUploadAsync(uploadId, bytes);

        string fileObjectId = string.Empty;
        await fixture.WithDatabaseAsync(async db =>
        {
            var fileObject = await db.FileObjects
                .SingleAsync(value => value.ManagedScanBytes);
            fileObjectId = fileObject.Id;
            fileObject.State = "deletion_pending";
            await db.SaveChangesAsync();
        });

        using var busy = await fixture.FinalizeUploadAsync(uploadId);
        await AssertProblemCodeAsync(
            busy,
            HttpStatusCode.ServiceUnavailable,
            "UPLOAD_OBJECT_DELETION_IN_PROGRESS");
        Assert.Equal("2", busy.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture)
            ?? busy.Headers.GetValues("Retry-After").Single());
        await fixture.WithDatabaseAsync(async db =>
        {
            var upload = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(value => value.Id == uploadId);
            var planned = await db.OrderedScanItems
                .AsNoTracking()
                .SingleAsync(value => value.BatchId == secondBatch.Id);
            var fileObject = await db.FileObjects
                .AsNoTracking()
                .SingleAsync(value => value.Id == fileObjectId);
            Assert.Equal("finalizing", upload.State);
            Assert.Equal(OrderedScanItemStatus.Pending, planned.Status);
            Assert.Null(planned.SourceFileReferenceId);
            Assert.Equal("deletion_pending", fileObject.State);
            Assert.Equal(1, fileObject.ReferenceCountCache);
            Assert.Single(await db.FileReferences
                .Where(value => value.FileObjectId == fileObjectId)
                .ToArrayAsync());
        });

        await fixture.WithDatabaseAsync(async db =>
        {
            var fileObject = await db.FileObjects
                .SingleAsync(value => value.Id == fileObjectId);
            fileObject.State = "available";
            await db.SaveChangesAsync();
        });
        using var retried = await fixture.FinalizeUploadAsync(uploadId);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        await fixture.WithDatabaseAsync(async db =>
        {
            var planned = await db.OrderedScanItems
                .AsNoTracking()
                .SingleAsync(value => value.BatchId == secondBatch.Id);
            var fileObject = await db.FileObjects
                .AsNoTracking()
                .SingleAsync(value => value.Id == fileObjectId);
            Assert.Equal(OrderedScanItemStatus.Uploaded, planned.Status);
            Assert.NotNull(planned.SourceFileReferenceId);
            Assert.Equal("available", fileObject.State);
            Assert.Equal(2, fileObject.ReferenceCountCache);
        });
    }

    [Fact]
    public async Task FinalizingUploadSafelyReplaysAfterPromoteCrash()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(TestType.Hop, 1);
        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 1);
        var item = Assert.Single(manifest);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);
        var bytes = seeded.Pages[0];
        using var created = await fixture.CreateUploadAsync(
            seeded.SessionId,
            batch.Id,
            item,
            bytes,
            OrderedScanFixture.OwnerId);
        using var createdJson = await ReadJsonAsync(created);
        var uploadId = createdJson.RootElement
            .GetProperty("uploadId")
            .GetString()!;
        await fixture.AppendUploadAsync(uploadId, bytes);

        fixture.ContentStore.FailNextManagedScanWrite();
        HttpResponseMessage? failedResponse = null;
        try
        {
            failedResponse = await fixture.FinalizeUploadAsync(uploadId);
            Assert.Equal(
                HttpStatusCode.InternalServerError,
                failedResponse.StatusCode);
        }
        catch (Exception exception) when (exception is IOException
            or HttpRequestException)
        {
            // TestServer may surface an unhandled injected storage failure
            // directly rather than converting it to an HTTP response.
        }
        finally
        {
            failedResponse?.Dispose();
        }

        await fixture.WithDatabaseAsync(async db =>
        {
            var upload = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(value => value.Id == uploadId);
            Assert.Equal("finalizing", upload.State);
            Assert.Equal(upload.ExpectedBytes, upload.CurrentBytes);
        });

        using var replay = await fixture.FinalizeUploadAsync(uploadId);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        await fixture.WithDatabaseAsync(async db =>
        {
            var upload = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(value => value.Id == uploadId);
            var staged = await db.OrderedScanItems
                .AsNoTracking()
                .SingleAsync(value => value.UploadSessionId == uploadId);
            Assert.Equal("completed", upload.State);
            Assert.Equal(OrderedScanItemStatus.Uploaded, staged.Status);
            Assert.NotNull(staged.SourceFileReferenceId);
            Assert.Equal(
                1,
                await db.FileReferences.CountAsync(value =>
                    value.OwnerType == "ordered_scan_batch"
                    && value.OwnerId == batch.Id));
        });
    }

    [Fact]
    public async Task CancellingDraftReleasesStagedReferenceAndManagedBytes()
    {
        await using var fixture = await OrderedScanFixture.CreateAsync();
        var seeded = await fixture.SeedSessionAsync(TestType.Hop, 1);
        var manifest = CreateManifest(expectedPageCount: 1, submissionCount: 1);
        var batch = await fixture.CreateBatchAsync(seeded.SessionId, manifest);
        await fixture.UploadPageAsync(
            seeded.SessionId,
            batch.Id,
            manifest[0],
            seeded.Pages[0]);
        var ready = await fixture.GetBatchAsync(batch.Id);
        ContentObjectLocator? locator = null;
        string fileObjectId = string.Empty;
        await fixture.WithDatabaseAsync(async db =>
        {
            var reference = await db.FileReferences
                .AsNoTracking()
                .Include(item => item.FileObject)
                .SingleAsync(item => item.OwnerType == "ordered_scan_batch"
                    && item.OwnerId == batch.Id);
            fileObjectId = reference.FileObjectId;
            locator = new ContentObjectLocator(
                ContentStorageClass.ManagedScanOriginal,
                reference.FileObject.Sha256,
                reference.FileObject.Bytes,
                reference.FileObject.Extension);
        });
        Assert.NotNull(locator);
        Assert.True(await fixture.ContentStore.ExistsAsync(locator));

        using var cancelledResponse = await fixture.CancelBatchAsync(
            batch.Id,
            ready.RowVersion);
        Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);

        Assert.False(await fixture.ContentStore.ExistsAsync(locator));
        await fixture.WithDatabaseAsync(async db =>
        {
            var persistedBatch = await db.OrderedScanBatches
                .AsNoTracking()
                .Include(item => item.Items)
                .SingleAsync(item => item.Id == batch.Id);
            var fileObject = await db.FileObjects
                .AsNoTracking()
                .SingleAsync(item => item.Id == fileObjectId);
            Assert.Equal(OrderedScanBatchStatus.Cancelled, persistedBatch.Status);
            var item = Assert.Single(persistedBatch.Items);
            Assert.Equal(OrderedScanItemStatus.Rejected, item.Status);
            Assert.Null(item.SourceFileReferenceId);
            Assert.Null(item.SourceSha256);
            Assert.Equal("deleted", fileObject.State);
            Assert.Equal(0, fileObject.ReferenceCountCache);
            Assert.Empty(await db.FileReferences
                .Where(reference => reference.OwnerType == "ordered_scan_batch"
                    && reference.OwnerId == batch.Id)
                .ToArrayAsync());
        });
    }

    private static ManifestItem[] CreateManifest(
        int expectedPageCount,
        int submissionCount) =>
        Enumerable.Range(1, expectedPageCount * submissionCount)
            .Select(ordinal => new ManifestItem(
                $"client-{ordinal:D3}",
                $"scan-{ordinal:D3}.pdf",
                ordinal))
            .ToArray();

    private static async Task AssertProblemCodeAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        Assert.Equal(statusCode, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return JsonDocument.Parse(bytes);
    }

    private sealed class OrderedScanFixture : IAsyncDisposable
    {
        public const string OwnerId = "01K13ORDEREDSCANOWNER0001";
        public const string SecondOperatorId = "01K13ORDEREDSCANOTHER0001";
        private const string TeacherId = "01K13ORDEREDSCANTEACHER001";
        private readonly IHost _host;
        private readonly string _root;

        private OrderedScanFixture(IHost host, string root)
        {
            _host = host;
            _root = root;
            Client = host.GetTestClient();
            AssemblyWorker = host.Services
                .GetRequiredService<OrderedScanAssemblyWorker>();
            PreprocessingWorker = host.Services
                .GetRequiredService<SubmissionPreprocessingWorker>();
            ContentStore = host.Services
                .GetRequiredService<FaultInjectingContentStore>();
        }

        public HttpClient Client { get; }
        public OrderedScanAssemblyWorker AssemblyWorker { get; }
        public SubmissionPreprocessingWorker PreprocessingWorker { get; }
        public FaultInjectingContentStore ContentStore { get; }

        public bool IncomingFileExists(string relativePath) => File.Exists(
            Path.Combine(_root, "incoming", relativePath));

        public static async Task<OrderedScanFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ooki-ordered-scan-tests",
                Guid.NewGuid().ToString("N"));
            var incoming = Path.Combine(root, "incoming");
            var objectRoot = Path.Combine(root, "objects");
            var database = Path.Combine(root, "ordered.db");
            Directory.CreateDirectory(incoming);
            var contentStore = new NtfsContentStore(new ContentStoreOptions
            {
                RootPath = objectRoot,
            });
            var faultInjectingStore = new FaultInjectingContentStore(
                contentStore);
            var host = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["Data:Incoming"] = incoming,
                                ["Storage:PhysicalReserveBytes"] = "0",
                            });
                    });
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddLogging();
                        services.ConfigureHttpJsonOptions(options =>
                        {
                            options.SerializerOptions.Converters.Add(
                                new JsonStringEnumConverter(
                                    JsonNamingPolicy.CamelCase));
                        });
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton(faultInjectingStore);
                        services.AddSingleton<IContentStore>(faultInjectingStore);
                        services.AddSingleton<IPdfPageCountReader,
                            LocalPdfPageCountReader>();
                        services.AddSingleton<IPreprocessingService>(
                            new PreprocessingService(new PreprocessingOptions
                            {
                                MaxPages = 50,
                                MaxDimensionPixels = 4_000,
                                MaxPixelsPerPage = 20_000_000,
                                MaxTotalPixels = 200_000_000,
                                PdfDpi = 72,
                                ImageDpi = 72,
                                ThumbnailMaxDimension = 96,
                                AlignmentGridMaxDimension = 48,
                                AlignmentMaxTranslationFraction = 0,
                            }));
                        services.AddSingleton<IWriteCoordinator,
                            SemaphoreWriteCoordinator>();
                        services.AddSingleton<IOrderedScanAssemblyPlanner,
                            OrderedScanAssemblyPlanner>();
                        services.AddSingleton<UploadLockProvider>();
                        services.AddSingleton<ContentObjectLockProvider>();
                        services.AddDbContextFactory<OokiGraderDbContext>(
                            options => options.UseSqlite(
                                $"Data Source={database}"));
                        services.AddScoped<OrderedScanBatchService>();
                        services.AddSingleton<OrderedScanAssemblyWorker>();
                        services.AddSingleton(Options.Create(
                            new SubmissionPreprocessingWorkerOptions
                            {
                                LeaseDuration = TimeSpan.FromMinutes(5),
                                MaximumAlignmentReferencePages = 50,
                                MaximumAlignmentReferencePixels = 200_000_000,
                            }));
                        services.AddSingleton<SubmissionPreprocessingWorker>();
                        services
                            .AddAuthentication(TestAuthHandler.SchemeName)
                            .AddScheme<AuthenticationSchemeOptions,
                                TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                        services.AddAuthorizationBuilder()
                            .SetFallbackPolicy(new AuthorizationPolicyBuilder(
                                    TestAuthHandler.SchemeName)
                                .RequireAuthenticatedUser()
                                .Build())
                            .AddPolicy(
                                "upload",
                                policy => policy
                                    .AddAuthenticationSchemes(TestAuthHandler.SchemeName)
                                    .RequireRole(
                                        "scanOperator",
                                        "teacher",
                                        "administrator"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapOrderedScanBatchEndpoints();
                            endpoints.MapUploadsEndpoints();
                        });
                    });
                })
                .Build();
            try
            {
                await host.StartAsync();
                await using var scope = host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                db.SiteSettings.Add(new SiteSettingsEntity
                {
                    Id = "site",
                    SchoolName = "Ordered scan fixture",
                    DataRoot = root,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.StaffUsers.AddRange(
                    Staff(OwnerId, "scan.owner", now),
                    Staff(SecondOperatorId, "scan.other", now),
                    Staff(TeacherId, "scan.teacher", now));
                await db.SaveChangesAsync();
                return new OrderedScanFixture(host, root);
            }
            catch
            {
                await host.StopAsync();
                host.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        public async Task<SeededSession> SeedSessionAsync(
            TestType testType,
            int expectedPageCount,
            bool corruptReferenceProvenance = false,
            bool persistExpectedPageCount = true,
            int scanGroupCount = 2)
        {
            var now = DateTimeOffset.UtcNow;
            var templatePdf = CreatePdf(
                Enumerable.Range(1, expectedPageCount)
                    .Select(role => (role, group: 0)));
            var sourceStored = await PutAsync(
                templatePdf,
                ContentStorageClass.TemplateSource);
            var templateId = UlidId.New(now);
            var versionId = UlidId.New(now.AddMilliseconds(1));
            var sessionId = UlidId.New(now.AddMilliseconds(2));
            var sourceUploadId = UlidId.New(now.AddMilliseconds(3));
            var sourceObjectId = UlidId.New(now.AddMilliseconds(4));
            var sourceReferenceId = UlidId.New(now.AddMilliseconds(5));

            await WithDatabaseAsync(async db =>
            {
                db.TestTemplates.Add(new TestTemplateEntity
                {
                    Id = templateId,
                    Title = $"Ordered {testType} fixture",
                    State = "active",
                    CreatedByStaffUserId = TeacherId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.TemplateVersions.Add(new TemplateVersionEntity
                {
                    Id = versionId,
                    TestTemplateId = templateId,
                    VersionNumber = 1,
                    State = "published",
                    PipelineVersion = "template-v1",
                    PublishedByStaffUserId = TeacherId,
                    PublishedAt = now,
                    ContentHash = new string('a', 64),
                    TestType = testType,
                    ExpectedSubmissionPageCount = persistExpectedPageCount
                        ? expectedPageCount
                        : null,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.UploadSessions.Add(new UploadSessionEntity
                {
                    Id = sourceUploadId,
                    CreatedByStaffUserId = TeacherId,
                    Purpose = "template_source",
                    DestinationType = "template_source",
                    OriginalFileName = "template.pdf",
                    DeclaredMimeType = "application/pdf",
                    ExpectedBytes = templatePdf.LongLength,
                    CurrentBytes = templatePdf.LongLength,
                    FinalSha256 = sourceStored.Locator.Sha256,
                    IncomingRelativePath = "fixture/template",
                    State = "completed",
                    ExpiresAt = now.AddHours(1),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.FileObjects.Add(new FileObjectEntity
                {
                    Id = sourceObjectId,
                    Sha256 = sourceStored.Locator.Sha256,
                    Bytes = sourceStored.Locator.Bytes,
                    VerifiedMime = "application/pdf",
                    Extension = sourceStored.Locator.Extension,
                    RelativeObjectPath = sourceStored.RelativePath,
                    StorageClass = ContentStorageClass.TemplateSource.ToString(),
                    RetentionClass = "template_source",
                    ManagedScanBytes = false,
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 1,
                });
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = sourceReferenceId,
                    FileObjectId = sourceObjectId,
                    OwnerType = "upload_session",
                    OwnerId = corruptReferenceProvenance
                        ? UlidId.New(now.AddMilliseconds(7))
                        : sourceUploadId,
                    Purpose = "template_source",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
                db.TemplateSources.Add(new TemplateSourceEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(6)),
                    TemplateVersionId = versionId,
                    UploadSessionId = sourceUploadId,
                    FileReferenceId = sourceReferenceId,
                    SourceRole = "blank_test",
                    DisplayName = "template.pdf",
                    Ordinal = 0,
                    UploadedByStaffUserId = TeacherId,
                    CreatedAt = now,
                });
                db.TestSessions.Add(new TestSessionEntity
                {
                    Id = sessionId,
                    TemplateVersionId = versionId,
                    TestDate = DateOnly.FromDateTime(now.UtcDateTime),
                    Priority = "economy",
                    State = "open",
                    CreatedByStaffUserId = TeacherId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync();
            });

            var pages = Enumerable.Range(0, scanGroupCount)
                .SelectMany(group => Enumerable.Range(1, expectedPageCount)
                    .Select(role => CreatePdf([(role, group + 1)])))
                .ToArray();
            return new SeededSession(sessionId, pages);
        }

        public async Task<BatchSnapshot> CreateBatchAsync(
            string sessionId,
            IReadOnlyList<ManifestItem> manifest)
        {
            using var response = await SendCreateBatchAsync(sessionId, manifest);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await ParseBatchAsync(response);
        }

        public Task<HttpResponseMessage> SendCreateBatchAsync(
            string sessionId,
            IReadOnlyList<ManifestItem> manifest)
        {
            var request = Authorized(
                HttpMethod.Post,
                $"/api/v1/test-sessions/{sessionId}/ordered-scan-batches",
                OwnerId);
            request.Content = JsonContent.Create(new
            {
                items = manifest.Select(item => new
                {
                    item.ClientItemId,
                    item.FileName,
                    item.InputOrdinal,
                }),
            });
            return SendAndDisposeRequestAsync(request);
        }

        public async Task<BatchSnapshot> GetBatchAsync(string batchId)
        {
            using var request = Authorized(
                HttpMethod.Get,
                $"/api/v1/ordered-scan-batches/{batchId}",
                OwnerId);
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await ParseBatchAsync(response);
        }

        public async Task FinalizeBatchAsync(string batchId, long rowVersion)
        {
            using var request = Authorized(
                HttpMethod.Post,
                $"/api/v1/ordered-scan-batches/{batchId}:finalize",
                OwnerId);
            request.Content = JsonContent.Create(new
            {
                expectedRowVersion = rowVersion,
            });
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        public Task<HttpResponseMessage> CancelBatchAsync(
            string batchId,
            long rowVersion)
        {
            var request = Authorized(
                HttpMethod.Post,
                $"/api/v1/ordered-scan-batches/{batchId}:cancel",
                OwnerId);
            request.Content = JsonContent.Create(new
            {
                expectedRowVersion = rowVersion,
            });
            return SendAndDisposeRequestAsync(request);
        }

        public async Task UploadPageAsync(
            string sessionId,
            string batchId,
            ManifestItem item,
            byte[] bytes)
        {
            using var created = await CreateUploadAsync(
                sessionId,
                batchId,
                item,
                bytes,
                OwnerId);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var uploadId = (await ReadJsonAsync(created)).RootElement
                .GetProperty("uploadId")
                .GetString()!;

            await AppendUploadAsync(uploadId, bytes);
            using var finalized = await FinalizeUploadAsync(uploadId);
            Assert.Equal(HttpStatusCode.Accepted, finalized.StatusCode);
        }

        public async Task AppendUploadAsync(string uploadId, byte[] bytes)
        {
            using var patch = Authorized(
                HttpMethod.Patch,
                $"/api/v1/uploads/{uploadId}/content",
                OwnerId);
            patch.Headers.TryAddWithoutValidation("Upload-Offset", "0");
            patch.Content = new ByteArrayContent(bytes);
            patch.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/offset+octet-stream");
            using var response = await Client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public Task<HttpResponseMessage> FinalizeUploadAsync(string uploadId)
        {
            var request = Authorized(
                HttpMethod.Post,
                $"/api/v1/uploads/{uploadId}:finalize",
                OwnerId);
            request.Content = JsonContent.Create(new { });
            return SendAndDisposeRequestAsync(request);
        }

        public Task<HttpResponseMessage> CreateUploadAsync(
            string sessionId,
            string batchId,
            ManifestItem item,
            byte[] bytes,
            string staffId,
            string? expectedSha256 = null)
        {
            var request = Authorized(
                HttpMethod.Post,
                "/api/v1/uploads/",
                staffId);
            request.Content = JsonContent.Create(new
            {
                purpose = "completedTestPage",
                fileName = item.FileName,
                declaredMimeType = "application/pdf",
                length = bytes.LongLength,
                expectedSha256 = expectedSha256
                    ?? Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant(),
                testSessionId = sessionId,
                orderedScanBatchId = batchId,
                inputOrdinal = item.InputOrdinal,
                clientItemId = item.ClientItemId,
            });
            return SendAndDisposeRequestAsync(request);
        }

        public async Task WithDatabaseAsync(
            Func<OokiGraderDbContext, Task> operation)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            await operation(db);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private async Task<ContentWriteResult> PutAsync(
            byte[] bytes,
            ContentStorageClass storageClass)
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            return await _host.Services.GetRequiredService<IContentStore>()
                .PutAsync(stream, storageClass, "pdf");
        }

        private static HttpRequestMessage Authorized(
            HttpMethod method,
            string path,
            string staffId,
            string role = "scanOperator")
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(TestAuthHandler.StaffHeader, staffId);
            request.Headers.Add(TestAuthHandler.RoleHeader, role);
            return request;
        }

        private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(
            HttpRequestMessage request)
        {
            using (request)
            {
                return await Client.SendAsync(request);
            }
        }

        private static async Task<BatchSnapshot> ParseBatchAsync(
            HttpResponseMessage response)
        {
            using var json = await ReadJsonAsync(response);
            var root = json.RootElement;
            return new BatchSnapshot(
                root.GetProperty("id").GetString()!,
                root.GetProperty("expectedPageCount").GetInt32(),
                root.GetProperty("status").GetString()!,
                root.GetProperty("rowVersion").GetInt64(),
                root.GetProperty("items").EnumerateArray()
                    .Select(item => new BatchItemSnapshot(
                        item.GetProperty("clientItemId").GetString()!,
                        item.GetProperty("inputOrdinal").GetInt32(),
                        item.GetProperty("status").GetString()!))
                    .ToArray(),
                root.GetProperty("groups").EnumerateArray()
                    .Select(item => new BatchGroupSnapshot(
                        item.GetProperty("groupOrdinal").GetInt32(),
                        item.GetProperty("status").GetString()!))
                    .ToArray(),
                root.GetProperty("submissionIds").EnumerateArray()
                    .Select(item => item.GetString()!)
                    .ToArray(),
                root.GetProperty("issues").EnumerateArray()
                    .Select(item => new BatchIssueSnapshot(
                        item.GetProperty("code").GetString()!))
                    .ToArray());
        }

        private static StaffUserEntity Staff(
            string id,
            string username,
            DateTimeOffset now) =>
            new()
            {
                Id = id,
                Username = username,
                UsernameNormalized = username,
                DisplayName = username,
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };

        private static byte[] CreatePdf(
            IEnumerable<(int Role, int Group)> pages)
        {
            using var output = new MemoryStream();
            using var document = SKDocument.CreatePdf(output)
                ?? throw new InvalidOperationException("PDF creation failed.");
            foreach (var (role, group) in pages)
            {
                var canvas = document.BeginPage(200, 280)
                    ?? throw new InvalidOperationException("PDF page failed.");
                canvas.Clear(SKColors.White);
                DrawRolePattern(canvas, role);
                if (group > 0)
                {
                    using var mark = new SKPaint
                    {
                        Color = SKColors.DarkBlue,
                        Style = SKPaintStyle.Fill,
                    };
                    canvas.DrawRect(
                        new SKRect(175, 250 - (group * 5), 181, 256 - (group * 5)),
                        mark);
                }

                document.EndPage();
            }

            document.Close();
            return output.ToArray();
        }

        private static void DrawRolePattern(SKCanvas canvas, int role)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke,
                IsAntialias = false,
            };
            canvas.DrawRect(new SKRect(8, 8, 192, 272), paint);
            using var signaturePaint = new SKPaint
            {
                Color = SKColors.Black,
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
            };
            var signatureIndex = role - 1;
            var signatureColumn = signatureIndex % 10;
            var signatureRow = signatureIndex / 10;
            var signatureX = 20 + (signatureColumn * 16);
            var signatureY = 155 + (signatureRow * 18);
            canvas.DrawRect(
                new SKRect(signatureX, 86, signatureX + 12, 136),
                signaturePaint);
            canvas.DrawRect(
                new SKRect(24, signatureY, 176, signatureY + 12),
                signaturePaint);
            var left = role switch
            {
                1 => 18,
                2 => 108,
                3 => 18,
                _ => 108,
            };
            var top = role switch
            {
                1 or 2 => 22,
                _ => 148,
            };
            for (var index = 0; index < 7; index++)
            {
                var x = left + ((index % 3) * 22);
                var y = top + ((index / 3) * 28);
                if (role % 2 == 0)
                {
                    canvas.DrawCircle(x + 8, y + 8, 7 + (index % 2), paint);
                    canvas.DrawLine(x, y + 18, x + 18, y, paint);
                }
                else
                {
                    canvas.DrawRect(new SKRect(x, y, x + 17, y + 15), paint);
                    canvas.DrawLine(x, y, x + 17, y + 15, paint);
                }
            }

            canvas.DrawLine(
                15,
                40 + (role * 12),
                185,
                40 + (role * 12),
                paint);
        }
    }

    private sealed class FaultInjectingContentStore(IContentStore inner)
        : IContentStore
    {
        private int _failManagedScanWrites;

        public void FailNextManagedScanWrite() =>
            Interlocked.Exchange(ref _failManagedScanWrites, 1);

        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            if (storageClass == ContentStorageClass.ManagedScanOriginal
                && Interlocked.Exchange(ref _failManagedScanWrites, 0) == 1)
            {
                throw new IOException("Injected managed-scan promotion failure.");
            }

            return inner.PutAsync(
                source,
                storageClass,
                verifiedExtension,
                cancellationToken);
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(locator, cancellationToken);

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(locator, cancellationToken);

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(locator, cancellationToken);
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string SchemeName = "ordered-test";
        public const string StaffHeader = "X-Test-Staff";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var staffId = Request.Headers[StaffHeader].FirstOrDefault();
            var role = Request.Headers[RoleHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(staffId)
                || string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, staffId),
                    new Claim(ClaimTypes.Role, role),
                ],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    SchemeName)));
        }
    }

    private sealed record ManifestItem(
        string ClientItemId,
        string FileName,
        int InputOrdinal);

    private sealed record SeededSession(
        string SessionId,
        byte[][] Pages);

    private sealed record BatchSnapshot(
        string Id,
        int ExpectedPageCount,
        string Status,
        long RowVersion,
        BatchItemSnapshot[] Items,
        BatchGroupSnapshot[] Groups,
        string[] SubmissionIds,
        BatchIssueSnapshot[] Issues);

    private sealed record BatchItemSnapshot(
        string ClientItemId,
        int InputOrdinal,
        string Status);

    private sealed record BatchGroupSnapshot(
        int GroupOrdinal,
        string Status);

    private sealed record BatchIssueSnapshot(string Code);
}

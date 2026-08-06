using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Jobs;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Infrastructure.Tests;

public sealed class DurableJobStoreTests
{
    [Fact]
    public async Task EnqueueAndCompletionAreLocallyIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var coordinator = new SemaphoreWriteCoordinator();
        var store = new EfBackgroundJobStore(
            database.Factory,
            coordinator,
            database.Clock);
        var request = new EnqueueJobRequest(
            "PreprocessSubmission",
            1,
            "submission:01JTEST:preprocess:hash:pipeline-v1",
            """{"submissionId":"01JTEST"}""");

        var first = await store.EnqueueAsync(request);
        var duplicate = await store.EnqueueAsync(request);
        var lease = await store.LeaseNextAsync("worker-a", TimeSpan.FromMinutes(2));

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.NotNull(lease);
        Assert.Equal(first.JobId, lease.JobId);
        Assert.True(await store.CompleteAsync(
            lease.JobId,
            "worker-a",
            lease.Revision));
        Assert.True(await store.CompleteAsync(
            lease.JobId,
            "worker-a",
            lease.Revision));

        var snapshot = await store.GetAsync(first.JobId);
        Assert.Equal(DurableJobState.Succeeded, snapshot!.State);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimedWithoutDuplicatingTheJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var coordinator = new SemaphoreWriteCoordinator();
        var store = new EfBackgroundJobStore(
            database.Factory,
            coordinator,
            database.Clock);
        var enqueued = await store.EnqueueAsync(new EnqueueJobRequest(
            "RenderResultPdf",
            1,
            "export:01JTEST:source:renderer",
            """{"exportId":"01JTEST"}"""));

        var firstLease = await store.LeaseNextAsync("worker-a", TimeSpan.FromMinutes(1));
        database.Clock.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = await store.LeaseNextAsync("worker-b", TimeSpan.FromMinutes(1));

        Assert.Equal(enqueued.JobId, firstLease!.JobId);
        Assert.Equal(enqueued.JobId, reclaimed!.JobId);
        Assert.Equal("worker-b", reclaimed.LeaseOwner);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.True(reclaimed.Revision > firstLease.Revision);
        Assert.False(await store.CompleteAsync(
            firstLease.JobId,
            "worker-a",
            firstLease.Revision));
    }

    [Fact]
    public async Task ReusedDeduplicationKeyWithDifferentPayloadIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var coordinator = new SemaphoreWriteCoordinator();
        var store = new EfBackgroundJobStore(
            database.Factory,
            coordinator,
            database.Clock);
        await store.EnqueueAsync(new EnqueueJobRequest(
            "ValidateUpload",
            1,
            "upload:01JTEST:final",
            """{"uploadId":"one"}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(new EnqueueJobRequest(
                "ValidateUpload",
                1,
                "upload:01JTEST:final",
                """{"uploadId":"different"}""")));
    }
}

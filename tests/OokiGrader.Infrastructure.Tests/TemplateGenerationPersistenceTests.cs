using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Templates;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class TemplateGenerationPersistenceTests
{
    private static readonly string[] ExpectedStepSuffixes = ["-1", "-2", "-3"];

    [Fact]
    public async Task StepBatchPersistsIndependentUnitsAndDerivedSourceProvenance()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = database.Clock.UtcNow;
        var staffId = UlidId.New(now);
        var uploadId = UlidId.New(now.AddMilliseconds(1));
        var batchId = UlidId.New(now.AddMilliseconds(2));
        var unitIds = Enumerable.Range(1, 3)
            .Select(index => UlidId.New(now.AddMilliseconds(index + 2)))
            .ToArray();

        await using (var context = database.Factory.CreateDbContext())
        {
            SeedSource(context, staffId, uploadId, now);
            context.TemplateGenerationBatches.Add(new TemplateGenerationBatchEntity
            {
                Id = batchId,
                Status = TemplateGenerationBatchStatus.Draft,
                TestType = TestType.Step,
                Subject = "算数",
                PromptSystem = TemplatePromptSystem.Standard,
                SourceId = uploadId,
                SourcePageCount = 6,
                ExpectedUnitCount = 3,
                PlanHash = new string('a', 64),
                CreatedByUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            for (var variation = 1; variation <= 3; variation++)
            {
                var firstPage = ((variation - 1) * 2) + 1;
                context.TemplateGenerationUnits.Add(new TemplateGenerationUnitEntity
                {
                    Id = unitIds[variation - 1],
                    BatchId = batchId,
                    Sequence = variation,
                    Status = TemplateGenerationUnitStatus.Pending,
                    TestType = TestType.Step,
                    FirstPage = firstPage,
                    LastPage = firstPage + 1,
                    StepSetIndex = 1,
                    StepVariationIndex = variation,
                    DeterministicSuffix = $"-{variation}",
                    PromptSystem = TemplatePromptSystem.Standard,
                    GenerationProfileJson = "{}",
                    GenerationProfileHash = new string(
                        (char)('b' + variation - 1),
                        64),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            context.TemplateGenerationDerivedSources.Add(
                new TemplateGenerationDerivedSourceEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(10)),
                    UnitId = unitIds[0],
                    ParentSourceId = uploadId,
                    ParentFirstPage = 1,
                    ParentLastPage = 2,
                    OriginalContentSha256 = new string('d', 64),
                    DerivationType = "pageRangeAndRotation",
                    AppliedRotationsJson = "[{\"page\":2,\"degrees\":90}]",
                    DerivationPolicyVersion = "page-range-quarter-turn-v1",
                    DerivedContentSha256 = new string('e', 64),
                    CreatedAt = now,
                });
            await context.SaveChangesAsync();
        }

        await using var verify = database.Factory.CreateDbContext();
        var batch = await verify.TemplateGenerationBatches
            .AsNoTracking()
            .Include(entity => entity.Units)
            .SingleAsync(entity => entity.Id == batchId);
        var derived = await verify.TemplateGenerationDerivedSources
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(TestType.Step, batch.TestType);
        Assert.Equal(TemplatePromptSystem.Standard, batch.PromptSystem);
        Assert.Equal(3, batch.Units.Count);
        Assert.Equal(
            ExpectedStepSuffixes,
            batch.Units
                .OrderBy(entity => entity.Sequence)
                .Select(entity => entity.DeterministicSuffix));
        Assert.Equal(unitIds[0], derived.UnitId);
        Assert.Equal("pageRangeAndRotation", derived.DerivationType);
        Assert.Equal(new string('d', 64), derived.OriginalContentSha256);
    }

    [Fact]
    public async Task UnitConstraintsRejectInvalidStepRangeAndDuplicateSequence()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = database.Clock.UtcNow;
        var staffId = UlidId.New(now);
        var uploadId = UlidId.New(now.AddMilliseconds(1));
        var batchId = UlidId.New(now.AddMilliseconds(2));

        await using (var seed = database.Factory.CreateDbContext())
        {
            SeedSource(seed, staffId, uploadId, now);
            seed.TemplateGenerationBatches.Add(CreateStepBatch(
                batchId,
                uploadId,
                staffId,
                now));
            await seed.SaveChangesAsync();
        }

        await using (var invalidRange = database.Factory.CreateDbContext())
        {
            invalidRange.TemplateGenerationUnits.Add(CreateStepUnit(
                UlidId.New(now.AddMilliseconds(3)),
                batchId,
                sequence: 1,
                firstPage: 1,
                lastPage: 3,
                variation: 1,
                now));
            await Assert.ThrowsAsync<DbUpdateException>(
                () => invalidRange.SaveChangesAsync());
        }

        await using (var valid = database.Factory.CreateDbContext())
        {
            valid.TemplateGenerationUnits.Add(CreateStepUnit(
                UlidId.New(now.AddMilliseconds(4)),
                batchId,
                sequence: 1,
                firstPage: 1,
                lastPage: 2,
                variation: 1,
                now));
            await valid.SaveChangesAsync();
        }

        await using (var duplicate = database.Factory.CreateDbContext())
        {
            duplicate.TemplateGenerationUnits.Add(CreateStepUnit(
                UlidId.New(now.AddMilliseconds(5)),
                batchId,
                sequence: 1,
                firstPage: 3,
                lastPage: 4,
                variation: 2,
                now));
            await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicate.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task BatchRevisionRejectsStaleGenerationTransition()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = database.Clock.UtcNow;
        var staffId = UlidId.New(now);
        var uploadId = UlidId.New(now.AddMilliseconds(1));
        var batchId = UlidId.New(now.AddMilliseconds(2));
        await using (var seed = database.Factory.CreateDbContext())
        {
            SeedSource(seed, staffId, uploadId, now);
            seed.TemplateGenerationBatches.Add(CreateStepBatch(
                batchId,
                uploadId,
                staffId,
                now));
            await seed.SaveChangesAsync();
        }

        await using var first = database.Factory.CreateDbContext();
        await using var stale = database.Factory.CreateDbContext();
        var firstCopy = await first.TemplateGenerationBatches
            .SingleAsync(entity => entity.Id == batchId);
        var staleCopy = await stale.TemplateGenerationBatches
            .SingleAsync(entity => entity.Id == batchId);

        firstCopy.Status = TemplateGenerationBatchStatus.Validating;
        await first.SaveChangesAsync();
        Assert.Equal(2, firstCopy.Revision);

        staleCopy.Status = TemplateGenerationBatchStatus.Cancelled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => stale.SaveChangesAsync());
    }

    private static TemplateGenerationBatchEntity CreateStepBatch(
        string batchId,
        string uploadId,
        string staffId,
        DateTimeOffset now) =>
        new()
        {
            Id = batchId,
            Status = TemplateGenerationBatchStatus.Draft,
            TestType = TestType.Step,
            Subject = "算数",
            PromptSystem = TemplatePromptSystem.Standard,
            SourceId = uploadId,
            SourcePageCount = 6,
            ExpectedUnitCount = 3,
            PlanHash = new string('a', 64),
            CreatedByUserId = staffId,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static TemplateGenerationUnitEntity CreateStepUnit(
        string id,
        string batchId,
        int sequence,
        int firstPage,
        int lastPage,
        int variation,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            BatchId = batchId,
            Sequence = sequence,
            Status = TemplateGenerationUnitStatus.Pending,
            TestType = TestType.Step,
            FirstPage = firstPage,
            LastPage = lastPage,
            StepSetIndex = 1,
            StepVariationIndex = variation,
            DeterministicSuffix = $"-{variation}",
            PromptSystem = TemplatePromptSystem.Standard,
            GenerationProfileJson = "{}",
            GenerationProfileHash = new string('f', 64),
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static void SeedSource(
        Microsoft.EntityFrameworkCore.DbContext context,
        string staffId,
        string uploadId,
        DateTimeOffset now)
    {
        context.Add(new StaffUserEntity
        {
            Id = staffId,
            Username = "batch.teacher",
            UsernameNormalized = "batch.teacher",
            DisplayName = "生成担当",
            PasswordHash = "argon2id:test",
            PasswordAlgorithm = "argon2id",
            PasswordAlgorithmVersion = 1,
            Status = "active",
            CredentialChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        context.Add(new UploadSessionEntity
        {
            Id = uploadId,
            CreatedByStaffUserId = staffId,
            Purpose = "template_source",
            DestinationType = "template_source",
            OriginalFileName = "STEP算数_小学4年.pdf",
            DeclaredMimeType = "application/pdf",
            ExpectedBytes = 100,
            CurrentBytes = 100,
            FinalSha256 = new string('d', 64),
            IncomingRelativePath = "incoming/test.part",
            State = "completed",
            ExpiresAt = now.AddHours(24),
            CreatedAt = now,
            UpdatedAt = now,
        });
    }
}

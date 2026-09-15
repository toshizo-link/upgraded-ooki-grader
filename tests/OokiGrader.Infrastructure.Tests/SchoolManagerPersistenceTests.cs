using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class SchoolManagerPersistenceTests
{
    [Fact]
    public async Task PersistsEncryptedSettingsImportsAndPrivacySafeDeliveryQueue()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = database.Clock.UtcNow;
        var studentId = UlidId.New(now);
        var importId = UlidId.New(now.AddMilliseconds(1));
        var deliveryId = UlidId.New(now.AddMilliseconds(2));

        await using (var db = database.Factory.CreateDbContext())
        {
            db.Students.Add(new StudentEntity
            {
                Id = studentId,
                StudentNumber = "10000001",
                StudentNumberNormalized = "10000001",
                FamilyName = "山田",
                GivenName = "太郎",
                FamilyNameNormalized = "山田",
                GivenNameNormalized = "太郎",
                DisplayName = "山田 太郎",
                SchoolClass = "A",
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.SchoolManagerSettings.Add(new SchoolManagerSettingsEntity
            {
                Id = "school-manager",
                BaseUrl = "https://fsm.flens.jp/",
                Username = "automation-user",
                ActivationStartedAt = now,
                PasswordSecretReference = "dpapi-v1:opaque",
                CredentialRevision = 1,
                Enabled = true,
                DryRun = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.PassFailImports.Add(new PassFailImportEntity
            {
                Id = importId,
                SourceSha256 = new string('a', 64),
                SourceFileName = "合否表.xls",
                SourceLastWriteAt = now,
                State = "processed",
                RowCount = 75,
                DeliveryCount = 1,
                CreatedAt = now,
                UpdatedAt = now,
                ProcessedAt = now,
            });
            db.GuardianDeliveries.Add(new GuardianDeliveryEntity
            {
                Id = deliveryId,
                Kind = "pass_fail_table",
                StudentId = studentId,
                PassFailImportId = importId,
                FileReferenceId = null,
                SourceKey = $"{new string('a', 64)}:{studentId}",
                AttachmentName = "合否表.pdf",
                State = "pending",
                NotBeforeAt = now,
                MaxAttempts = 5,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await using var verify = database.Factory.CreateDbContext();
        var delivery = await verify.GuardianDeliveries
            .AsNoTracking()
            .Include(item => item.Student)
            .Include(item => item.PassFailImport)
            .SingleAsync();

        Assert.Equal("A", delivery.Student.SchoolClass);
        Assert.Equal(new string('a', 64), delivery.PassFailImport!.SourceSha256);
        Assert.Equal("pending", delivery.State);
        Assert.True((await verify.SchoolManagerSettings.SingleAsync()).DryRun);
    }
}

using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.SchoolManager;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Security;
using OokiGrader.Infrastructure.Storage;
using OokiGrader.SchoolManager.Delivery;
using OokiGrader.SchoolManager.PassFail;

namespace OokiGrader.IntegrationTests;

public sealed class SchoolManagerAutomationTests
{
    [Fact]
    public async Task FinalizedResultQueuesOneCombinedPdfAndOneDelivery()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.ConfigureAsync(dryRun: true);
        var submissionId = await fixture.SeedFinalizedResultAsync();

        Assert.True(await fixture.ResultPlanner.ProcessNextAsync());
        Assert.False(await fixture.ResultPlanner.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var export = await db.ExportRecords.AsNoTracking().SingleAsync();
        var delivery = await db.GuardianDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(submissionId, export.SubmissionId);
        Assert.Equal("queued", export.State);
        Assert.Equal("result_pdf", delivery.Kind);
        Assert.Equal("pending", delivery.State);
        Assert.Equal(export.Id, delivery.ExportRecordId);
        Assert.Single(await db.BackgroundJobs
            .Where(item => item.Type == "result_pdf.render")
            .ToArrayAsync());
    }

    [Fact]
    public async Task FinalizedResultWithoutOriginalPdfIsNotQueued()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.ConfigureAsync(dryRun: true);
        await fixture.SeedFinalizedResultAsync(includeOriginalPdf: false);

        Assert.False(await fixture.ResultPlanner.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        Assert.Empty(await db.ExportRecords.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.GuardianDeliveries.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ImportCreatesOnePrivacySafeDeliveryPerMatchedStudent()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.SeedStudentAsync("student-a", "10000001", "小6", "A");
        await fixture.SeedStudentAsync("student-b", "10000002", "小6", "B");
        await fixture.SeedStudentAsync(
            "student-unmatched",
            "99999999",
            "小6",
            "A");
        await fixture.ConfigureAsync(dryRun: true);
        var workbookPath = Path.Combine(fixture.InputRoot, "合否表.xlsx");
        CreateWorkbook(
            workbookPath,
            [
                ["小６ 社会暗記テスト合格表"],
                [],
                ["四谷大塚ＩＤ", "氏名", "クラス", "第1回"],
                ["10000001", "山田太郎", "A", "○"],
                ["10000002", "佐藤花子", "B", "●"],
            ]);
        File.SetLastWriteTimeUtc(workbookPath, fixture.Now.AddMinutes(-1).UtcDateTime);

        Assert.True(await fixture.Importer.ProcessNextAsync());
        Assert.False(await fixture.Importer.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var imported = await db.PassFailImports.AsNoTracking().SingleAsync();
        var deliveries = await db.GuardianDeliveries
            .AsNoTracking()
            .OrderBy(item => item.StudentId)
            .ToArrayAsync();
        Assert.Equal("processed", imported.State);
        Assert.Equal(2, imported.DeliveryCount);
        Assert.Equal(2, deliveries.Length);
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal("pass_fail_table", delivery.Kind);
            Assert.Equal("ready", delivery.State);
            Assert.NotNull(delivery.FileReferenceId);
        });
        Assert.DoesNotContain(deliveries, delivery =>
            delivery.StudentId == "student-unmatched");
        Assert.Equal(2, await db.FileReferences.CountAsync());
    }

    [Fact]
    public async Task InvalidWorkbookIsRecordedAndDoesNotBlockNextWorkbook()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.SeedStudentAsync("student-a", "10000001", "小6", "A");
        await fixture.ConfigureAsync(dryRun: true);
        var invalidPath = Path.Combine(fixture.InputRoot, "01-invalid.xlsx");
        await File.WriteAllBytesAsync(
            invalidPath,
            [0x50, 0x4b, 0x03, 0x04, 0x00, 0x00, 0x00]);
        File.SetLastWriteTimeUtc(invalidPath, fixture.Now.AddSeconds(-50).UtcDateTime);
        var validPath = Path.Combine(fixture.InputRoot, "02-valid.xlsx");
        CreateWorkbook(
            validPath,
            [
                ["小６ 合否表"],
                ["四谷大塚ID", "氏名", "クラス", "第1回"],
                ["10000001", "山田太郎", "A", "○"],
            ]);
        File.SetLastWriteTimeUtc(validPath, fixture.Now.AddSeconds(-40).UtcDateTime);

        Assert.True(await fixture.Importer.ProcessNextAsync());
        Assert.True(await fixture.Importer.ProcessNextAsync());
        Assert.False(await fixture.Importer.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var imports = await db.PassFailImports
            .AsNoTracking()
            .OrderBy(item => item.SourceFileName)
            .ToArrayAsync();
        Assert.Equal(["failed", "processed"], imports.Select(item => item.State));
        Assert.Equal("pass_fail_workbook_invalid", imports[0].ErrorCode);
        Assert.Single(await db.GuardianDeliveries.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ActivationBoundaryExcludesHistoricalFinalizedResults()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.ConfigureAsync(
            dryRun: true,
            activationStartedAt: fixture.Now.AddMinutes(1));
        await fixture.SeedFinalizedResultAsync();

        Assert.False(await fixture.ResultPlanner.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        Assert.Empty(await db.GuardianDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.ExportRecords.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task DryRunValidatesOnceWithoutMarkingDeliverySent()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.SeedStudentAsync("student-a", "10000001", "小6", "A");
        await fixture.SeedReadyPassFailDeliveryAsync("student-a");
        await fixture.ConfigureAsync(dryRun: true);

        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());
        Assert.False(await fixture.DeliveryProcessor.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var delivery = await db.GuardianDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal("ready", delivery.State);
        Assert.NotNull(delivery.LastDryRunAt);
        Assert.Null(delivery.SentAt);
        var settings = await db.SchoolManagerSettings.AsNoTracking().SingleAsync();
        Assert.NotNull(settings.LastCredentialTestedAt);
        var request = Assert.Single(fixture.Client.Requests);
        Assert.True(request.DryRun);
        Assert.Equal("10000001", request.StudentNumber);
        Assert.Equal("https://fsm.flens.jp/", request.BaseUri.AbsoluteUri);
    }

    [Fact]
    public async Task UnknownPassFailSendReservesDailyLimit()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.SeedStudentAsync("student-a", "10000001", "小6", "A");
        await fixture.SeedReadyPassFailDeliveryAsync("student-a");
        await fixture.ConfigureAsync(dryRun: false);
        fixture.Client.FailWithUnknownOutcome = true;

        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());

        var workbookPath = Path.Combine(fixture.InputRoot, "合否表.xlsx");
        CreateWorkbook(
            workbookPath,
            [
                ["小６ 合否表"],
                ["四谷大塚ID", "氏名", "クラス", "第1回"],
                ["10000001", "山田太郎", "A", "○"],
            ]);
        File.SetLastWriteTimeUtc(workbookPath, fixture.Now.AddSeconds(-10).UtcDateTime);
        Assert.True(await fixture.Importer.ProcessNextAsync());
        Assert.False(await fixture.DeliveryProcessor.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var deliveries = await db.GuardianDeliveries
            .AsNoTracking()
            .ToArrayAsync();
        Assert.Equal(2, deliveries.Length);
        var unknown = Assert.Single(deliveries, item => item.State == "failed");
        var next = Assert.Single(deliveries, item => item.State == "ready");
        Assert.Equal("message_send_outcome_unknown", unknown.LastErrorCode);
        Assert.Equal(GuardianDeliverySchedule.TokyoDate(fixture.Now), unknown.SentLocalDate);
        Assert.Equal(
            GuardianDeliverySchedule.NextPassFailNotBefore(fixture.Now, fixture.Now),
            next.NotBeforeAt);
        Assert.Single(fixture.Client.Requests);
    }

    [Fact]
    public async Task InterruptedPassFailSendReservesDailyLimit()
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.SeedStudentAsync("student-a", "10000001", "小6", "A");
        await fixture.SeedReadyPassFailDeliveryAsync("student-a");
        await fixture.ConfigureAsync(dryRun: false);
        await using (var db = await fixture.CreateDbContextAsync())
        {
            var interrupted = await db.GuardianDeliveries.SingleAsync();
            interrupted.State = "sending";
            interrupted.LastAttemptAt = fixture.Now;
            interrupted.SentLocalDate = GuardianDeliverySchedule.TokyoDate(fixture.Now);
            await db.SaveChangesAsync();
        }

        var workbookPath = Path.Combine(fixture.InputRoot, "合否表.xlsx");
        CreateWorkbook(
            workbookPath,
            [
                ["小６ 合否表"],
                ["四谷大塚ID", "氏名", "クラス", "第1回"],
                ["10000001", "山田太郎", "A", "○"],
            ]);
        File.SetLastWriteTimeUtc(workbookPath, fixture.Now.AddSeconds(-10).UtcDateTime);

        Assert.True(await fixture.Importer.ProcessNextAsync());

        await using var verificationDb = await fixture.CreateDbContextAsync();
        var deliveries = await verificationDb.GuardianDeliveries
            .AsNoTracking()
            .ToArrayAsync();
        Assert.Single(deliveries, item => item.State == "sending");
        var next = Assert.Single(deliveries, item => item.State == "ready");
        Assert.Equal(
            GuardianDeliverySchedule.NextPassFailNotBefore(fixture.Now, fixture.Now),
            next.NotBeforeAt);
    }

    private static void CreateWorkbook(string path, IReadOnlyList<string[]> rows)
    {
        using var archive = new ZipArchive(
            File.Create(path),
            ZipArchiveMode.Create);
        Write(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        Write(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Write(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="合否表" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Write(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        var sheet = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            """);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            sheet.Append("<row r=\"").Append(rowIndex + 1).Append("\">");
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Length; columnIndex++)
            {
                sheet.Append("<c r=\"")
                    .Append(ColumnName(columnIndex)).Append(rowIndex + 1)
                    .Append("\" t=\"inlineStr\"><is><t>")
                    .Append(System.Security.SecurityElement.Escape(rows[rowIndex][columnIndex]))
                    .Append("</t></is></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        Write(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ColumnName(int index)
    {
        var value = index + 1;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + (value % 26)) + result;
            value /= 26;
        }

        return result;
    }

    private sealed class AutomationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly string _root;

        private AutomationFixture(
            SqliteConnection connection,
            ServiceProvider services,
            string root,
            DateTimeOffset now)
        {
            _connection = connection;
            _services = services;
            _root = root;
            Now = now;
            InputRoot = Path.Combine(root, "incoming");
            Importer = services.GetRequiredService<PassFailImportProcessor>();
            DeliveryProcessor = services.GetRequiredService<GuardianDeliveryProcessor>();
            ResultPlanner = services.GetRequiredService<AutomaticResultDeliveryPlanner>();
            Client = services.GetRequiredService<FakeSchoolManagerClient>();
        }

        public DateTimeOffset Now { get; }
        public string InputRoot { get; }
        public PassFailImportProcessor Importer { get; }
        public GuardianDeliveryProcessor DeliveryProcessor { get; }
        public AutomaticResultDeliveryPlanner ResultPlanner { get; }
        public FakeSchoolManagerClient Client { get; }

        public static async Task<AutomationFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ooki-school-manager-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "incoming"));
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var now = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddSingleton<IClock>(new FixedClock(now));
            services.AddSingleton<IWriteCoordinator, SemaphoreWriteCoordinator>();
            services.AddDbContextFactory<OokiGraderDbContext>(options =>
                options.UseSqlite(connection));
            services.AddSingleton<IContentStore>(new NtfsContentStore(
                new ContentStoreOptions
                {
                    RootPath = Path.Combine(root, "objects"),
                }));
            services.AddSingleton<IAiSecretStore, InMemoryAiSecretStore>();
            services.AddSingleton(Options.Create(new SchoolManagerAutomationOptions
            {
                PassFailInputDirectory = Path.Combine(root, "incoming"),
                StableFileAge = TimeSpan.FromSeconds(5),
            }));
            services.AddSingleton<FakeSchoolManagerClient>();
            services.AddSingleton<ISchoolManagerClient>(provider =>
                provider.GetRequiredService<FakeSchoolManagerClient>());
            services.AddSingleton<PassFailImportProcessor>();
            services.AddSingleton<GuardianDeliveryProcessor>();
            services.AddSingleton<AutomaticResultDeliveryPlanner>();
            var provider = services.BuildServiceProvider();
            await using var db = await provider
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return new AutomationFixture(connection, provider, root, now);
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync() => _services
            .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
            .CreateDbContextAsync();

        public async Task SeedStudentAsync(
            string id,
            string number,
            string gradeLabel,
            string classLabel)
        {
            await using var db = await CreateDbContextAsync();
            db.Students.Add(new StudentEntity
            {
                Id = id,
                StudentNumber = number,
                StudentNumberNormalized = number,
                FamilyName = "テスト",
                GivenName = id,
                FamilyNameNormalized = "テスト",
                GivenNameNormalized = id,
                DisplayName = $"テスト {id}",
                GradeLabel = gradeLabel,
                SchoolClass = classLabel,
                Status = "active",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync();
        }

        public async Task<string> SeedFinalizedResultAsync(bool includeOriginalPdf = true)
        {
            var staffId = UlidId.New(Now);
            var studentId = UlidId.New(Now.AddMilliseconds(1));
            var templateId = UlidId.New(Now.AddMilliseconds(2));
            var versionId = UlidId.New(Now.AddMilliseconds(3));
            var questionId = UlidId.New(Now.AddMilliseconds(4));
            var sessionId = UlidId.New(Now.AddMilliseconds(5));
            var submissionId = UlidId.New(Now.AddMilliseconds(6));
            var runId = UlidId.New(Now.AddMilliseconds(7));
            var resultId = UlidId.New(Now.AddMilliseconds(8));
            var revisionId = UlidId.New(Now.AddMilliseconds(9));
            var originalObjectId = UlidId.New(Now.AddMilliseconds(10));
            await using var db = await CreateDbContextAsync();
            db.SiteSettings.Add(new SiteSettingsEntity
            {
                Id = "site",
                SchoolName = "テスト校",
                DataRoot = _root,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "teacher",
                UsernameNormalized = "teacher",
                DisplayName = "先生",
                PasswordHash = "test",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
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
                GradeLabel = "小6",
                SchoolClass = "A",
                Status = "active",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "社会テスト",
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            db.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "published",
                PipelineVersion = "test",
                PublishedByStaffUserId = staffId,
                PublishedAt = Now,
                ContentHash = new string('b', 64),
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            db.Questions.Add(new QuestionEntity
            {
                Id = questionId,
                TemplateVersionId = versionId,
                LogicalQuestionId = UlidId.New(Now.AddMilliseconds(11)),
                OrderIndex = 0,
                DisplayLabel = "1",
                MiddleQuestionLabel = "1",
                HierarchyPathKey = "\u001f1\u001f",
                QuestionText = "首都を答えなさい。",
                QuestionType = "exact_short_text",
                GradingMode = "deterministic",
                MaxPointsMilli = 1_000,
                TeacherVerified = true,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = new DateOnly(2026, 9, 15),
                State = "closed",
                CreatedByStaffUserId = staffId,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            if (includeOriginalPdf)
            {
                var store = _services.GetRequiredService<IContentStore>();
                await using var original = new MemoryStream(
                    "%PDF-1.4\n%%EOF\n"u8.ToArray(),
                    writable: false);
                var stored = await store.PutAsync(
                    original,
                    ContentStorageClass.ManagedScanOriginal,
                    "pdf");
                db.FileObjects.Add(new FileObjectEntity
                {
                    Id = originalObjectId,
                    Sha256 = stored.Locator.Sha256,
                    Bytes = stored.Locator.Bytes,
                    VerifiedMime = "application/pdf",
                    Extension = "pdf",
                    RelativeObjectPath = stored.RelativePath,
                    StorageClass = ContentStorageClass.ManagedScanOriginal.ToString(),
                    RetentionClass = "submission_scan",
                    ManagedScanBytes = true,
                    State = "available",
                    CreatedAt = Now,
                    VerifiedAt = Now,
                    ReferenceCountCache = 1,
                });
            }

            var submission = new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "finalized",
                ScanPayloadState = includeOriginalPdf
                    ? "scan_available"
                    : "scan_deleted",
                AssignedStudentId = studentId,
                AssignmentMethod = "teacher",
                CanonicalForSession = true,
                UploadedByStaffUserId = staffId,
                OriginalFileObjectId = includeOriginalPdf ? originalObjectId : null,
                CurrentGradingRunId = null,
                FinalizedByStaffUserId = staffId,
                FinalizedAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            db.Submissions.Add(submission);
            await db.SaveChangesAsync();
            submission.CurrentGradingRunId = runId;
            db.GradingRuns.Add(new GradingRunEntity
            {
                Id = runId,
                SubmissionId = submissionId,
                RunNumber = 1,
                TemplateVersionId = versionId,
                Reason = "initial",
                State = "finalized",
                PipelineVersion = "test",
                CanonicalInputManifestHash = new string('c', 64),
                EarnedPointsMilli = 1_000,
                PossiblePointsMilli = 1_000,
                ResultSourceRevision = 1,
                FinalizedAt = Now,
                FinalizedByStaffUserId = staffId,
                CreatedAt = Now,
            });
            var questionResult = new QuestionResultEntity
            {
                Id = resultId,
                GradingRunId = runId,
                QuestionId = questionId,
                TranscribedAnswer = "東京",
                NormalizedAnswer = "東京",
                ProposedPointsMilli = 1_000,
                MaximumPointsMilli = 1_000,
                Outcome = "correct",
                Method = "deterministic",
                ConfidenceBasisPoints = 10_000,
                ReviewStatus = "resolved",
                CurrentRevisionId = null,
                CreatedAt = Now,
            };
            db.QuestionResults.Add(questionResult);
            await db.SaveChangesAsync();
            questionResult.CurrentRevisionId = revisionId;
            db.ResultRevisions.Add(new ResultRevisionEntity
            {
                Id = revisionId,
                QuestionResultId = resultId,
                RevisionNumber = 1,
                AwardedPointsMilli = 1_000,
                Outcome = "correct",
                Source = "initial",
                CreatedAt = Now,
            });
            await db.SaveChangesAsync();
            return submissionId;
        }

        public async Task ConfigureAsync(
            bool dryRun,
            DateTimeOffset? activationStartedAt = null)
        {
            var secretStore = _services.GetRequiredService<IAiSecretStore>();
            var reference = await secretStore.WriteAsync(
                "00000000000000000000000001",
                1,
                "test-password".AsMemory());
            await using var db = await CreateDbContextAsync();
            db.SchoolManagerSettings.Add(new SchoolManagerSettingsEntity
            {
                Id = "school-manager",
                BaseUrl = "https://fsm.flens.jp/",
                Username = "test-user",
                ActivationStartedAt = activationStartedAt ?? Now.AddMinutes(-1),
                PasswordSecretReference = reference.Value,
                CredentialRevision = 1,
                Enabled = true,
                DryRun = dryRun,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedReadyPassFailDeliveryAsync(string studentId)
        {
            var projection = new PassFailProjection(
                "合否表",
                "小6",
                "A",
                ["第1回"],
                [new PassFailProjectionRow("本人", ["○"])],
                "safe");
            var pdf = PassFailPdfRenderer.Render(projection, Now);
            var store = _services.GetRequiredService<IContentStore>();
            await using var stream = new MemoryStream(pdf.Bytes, writable: false);
            var stored = await store.PutAsync(
                stream,
                ContentStorageClass.ResultReport,
                "pdf");
            await using var db = await CreateDbContextAsync();
            var importId = UlidId.New(Now);
            var deliveryId = UlidId.New(Now.AddMilliseconds(1));
            var objectId = UlidId.New(Now.AddMilliseconds(2));
            var referenceId = UlidId.New(Now.AddMilliseconds(3));
            db.PassFailImports.Add(new PassFailImportEntity
            {
                Id = importId,
                SourceSha256 = new string('a', 64),
                SourceFileName = "合否表.xlsx",
                SourceLastWriteAt = Now,
                State = "processed",
                RowCount = 1,
                DeliveryCount = 1,
                ProcessedAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = objectId,
                Sha256 = stored.Locator.Sha256,
                Bytes = stored.Locator.Bytes,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = stored.RelativePath,
                StorageClass = ContentStorageClass.ResultReport.ToString(),
                RetentionClass = "result_report",
                State = "available",
                CreatedAt = Now,
                VerifiedAt = Now,
                ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = referenceId,
                FileObjectId = objectId,
                OwnerType = "guardian_delivery",
                OwnerId = deliveryId,
                Purpose = "pass_fail_table_pdf",
                RetentionAnchorAt = Now,
                CreatedAt = Now,
            });
            db.GuardianDeliveries.Add(new GuardianDeliveryEntity
            {
                Id = deliveryId,
                Kind = "pass_fail_table",
                StudentId = studentId,
                PassFailImportId = importId,
                FileReferenceId = referenceId,
                SourceKey = $"{new string('a', 64)}:{studentId}",
                AttachmentName = "合否表_20260915.pdf",
                State = "ready",
                NotBeforeAt = Now,
                MaxAttempts = 5,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FakeSchoolManagerClient : ISchoolManagerClient
    {
        public List<SchoolManagerDeliveryRequest> Requests { get; } = [];
        public bool FailWithUnknownOutcome { get; set; }

        public Task<SchoolManagerDeliveryResult> DeliverAsync(
            SchoolManagerDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (FailWithUnknownOutcome)
            {
                throw new SchoolManagerClientException(
                    "message_send_response_invalid",
                    "Test send outcome is unknown.",
                    outcomeUnknown: true);
            }

            return Task.FromResult(new SchoolManagerDeliveryResult(
                request.DryRun,
                request.DryRun ? null : "thread-1"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}

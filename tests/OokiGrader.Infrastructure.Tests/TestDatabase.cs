using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Infrastructure.Tests;

internal sealed class TestClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public void Advance(TimeSpan amount)
    {
        UtcNow = UtcNow.Add(amount);
    }
}

internal sealed class TestDbContextFactory(
    DbContextOptions<OokiGraderDbContext> options,
    IClock clock) : IDbContextFactory<OokiGraderDbContext>
{
    public OokiGraderDbContext CreateDbContext()
    {
        return new OokiGraderDbContext(options, clock);
    }

    public Task<OokiGraderDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}

internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(
        string rootPath,
        string databasePath,
        TestClock clock,
        TestDbContextFactory factory)
    {
        RootPath = rootPath;
        DatabasePath = databasePath;
        Clock = clock;
        Factory = factory;
    }

    public string RootPath { get; }
    public string DatabasePath { get; }
    public TestClock Clock { get; }
    public TestDbContextFactory Factory { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ooki-grader-infrastructure-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var databasePath = Path.Combine(rootPath, "ooki-grader.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        var interceptor = new SqlitePragmaConnectionInterceptor();
        var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;
        var clock = new TestClock(new DateTimeOffset(
            2026,
            7,
            27,
            3,
            0,
            0,
            TimeSpan.Zero));
        var factory = new TestDbContextFactory(options, clock);

        await using var context = factory.CreateDbContext();
        var initializer = new OokiDatabaseInitializer(context, clock);
        await initializer.InitializeAsync(new OokiDatabaseInitializationOptions(
            rootPath,
            SchoolName: "Ooki Test School",
            BootstrapTokenHash: new string('a', 64),
            BootstrapTokenExpiresAt: clock.UtcNow.AddHours(24)));
        return new TestDatabase(rootPath, databasePath, clock, factory);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

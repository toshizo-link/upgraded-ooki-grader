using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OokiGrader.Infrastructure.Persistence;

public sealed class OokiGraderDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<OokiGraderDbContext>
{
    public OokiGraderDbContext CreateDbContext(string[] args)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "ooki-grader-design-time.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new OokiGraderDbContext(options);
    }
}

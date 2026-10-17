using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Tests;

public sealed class TestSessionPassCriteriaMigrationTests
{
    [Fact]
    public async Task CriterionColumnCanBeRolledBackAndReappliedWithoutRebuildingSessionTable()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        var rootPage = await db.Database.SqlQueryRaw<long>(
            "SELECT rootpage AS Value FROM sqlite_master WHERE name = 'test_session'").SingleAsync();
        await migrator.MigrateAsync("20260915062614_0025_SchoolManagerGuardianDelivery");
        await migrator.MigrateAsync();
        Assert.Equal(rootPage, await db.Database.SqlQueryRaw<long>(
            "SELECT rootpage AS Value FROM sqlite_master WHERE name = 'test_session'").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("ok", Assert.Single(await db.Database.SqlQueryRaw<string>(
            "PRAGMA integrity_check").ToArrayAsync()));
    }
}

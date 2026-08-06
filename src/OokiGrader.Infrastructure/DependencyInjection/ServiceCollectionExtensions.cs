using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Grading;
using OokiGrader.Infrastructure.Auditing;
using OokiGrader.Infrastructure.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Storage;

namespace OokiGrader.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOokiPersistence(
        this IServiceCollection services,
        OokiPersistenceOptions persistenceOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(persistenceOptions);

        if (!Path.IsPathFullyQualified(persistenceOptions.DatabasePath))
        {
            throw new ArgumentException(
                "The SQLite database path must be absolute.",
                nameof(persistenceOptions));
        }

        if (!Path.IsPathFullyQualified(persistenceOptions.ContentRootPath))
        {
            throw new ArgumentException(
                "The content root path must be absolute.",
                nameof(persistenceOptions));
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(persistenceOptions.DatabasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = SqlitePragmaConnectionInterceptor.BusyTimeoutMilliseconds / 1000,
            Pooling = true
        }.ToString();

        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IWriteCoordinator, SemaphoreWriteCoordinator>();
        services.TryAddSingleton<SqlitePragmaConnectionInterceptor>();

        services.AddDbContextFactory<OokiGraderDbContext>((serviceProvider, options) =>
        {
            options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(30));
            options.AddInterceptors(
                serviceProvider.GetRequiredService<SqlitePragmaConnectionInterceptor>());
        });
        services.AddScoped(serviceProvider =>
            serviceProvider
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContext());
        services.AddScoped<IUnitOfWork>(serviceProvider =>
            serviceProvider.GetRequiredService<OokiGraderDbContext>());
        services.AddScoped<OokiDatabaseInitializer>();

        services.TryAddSingleton<IContentStore>(_ =>
            new NtfsContentStore(new ContentStoreOptions
            {
                RootPath = persistenceOptions.ContentRootPath
            }));
        services.TryAddSingleton<IBackgroundJobStore, EfBackgroundJobStore>();
        services.TryAddSingleton<IAuditSink, EfAuditSink>();
        services.TryAddSingleton<IProviderFreeGradingStore, EfProviderFreeGradingStore>();
        return services;
    }
}

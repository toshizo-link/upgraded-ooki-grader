using OokiGrader.Host.Api;
using OokiGrader.Infrastructure.Backups;

namespace OokiGrader.Host.Jobs;

internal static class BackupServiceCollectionExtensions
{
    public static IServiceCollection AddOokiBackups(
        this IServiceCollection services,
        BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<SqliteOnlineBackupArchiveService>();
        services.AddSingleton<IBackupArchiveService>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteOnlineBackupArchiveService>());
        services.AddSingleton<IBackupRetentionService>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteOnlineBackupArchiveService>());
        services.AddSingleton<BackupJobCoordinator>();
        services.AddSingleton<BackupHealthService>();
        services.AddSingleton<BackupJobWorker>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<BackupJobWorker>());
        return services;
    }
}

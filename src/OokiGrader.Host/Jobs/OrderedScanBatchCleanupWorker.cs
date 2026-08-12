using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Services;

namespace OokiGrader.Host.Jobs;

public sealed partial class OrderedScanBatchCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IWriteCoordinator writeCoordinator,
    ILogger<OrderedScanBatchCleanupWorker> logger) : BackgroundService
{
    public Task<int> ProcessOnceAsync(
        CancellationToken cancellationToken = default)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<OrderedScanBatchService>();
            return await service.ExpireAndReleaseAsync(token)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogCleanupFailure(logger, exception);
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken)
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 5750,
        Level = LogLevel.Error,
        Message = "Ordered scan batch cleanup failed.")]
    private static partial void LogCleanupFailure(
        ILogger logger,
        Exception exception);
}

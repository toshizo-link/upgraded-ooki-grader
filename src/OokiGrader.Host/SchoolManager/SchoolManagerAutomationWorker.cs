namespace OokiGrader.Host.SchoolManager;

public sealed partial class SchoolManagerAutomationWorker(
    AutomaticResultDeliveryPlanner resultPlanner,
    PassFailImportProcessor passFailImporter,
    GuardianDeliveryProcessor deliveryProcessor,
    ILogger<SchoolManagerAutomationWorker> logger) : BackgroundService
{
    public async Task<bool> ProcessCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var changed = false;
        changed |= await resultPlanner.ProcessNextAsync(cancellationToken)
            .ConfigureAwait(false);
        changed |= await passFailImporter.ProcessNextAsync(cancellationToken)
            .ConfigureAwait(false);
        changed |= await deliveryProcessor.ProcessNextAsync(cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changed = await ProcessCycleAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (!changed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCycleFailure(exception);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        EventId = 7401,
        Level = LogLevel.Error,
        Message = "The School Manager automation cycle failed safely.")]
    private partial void LogCycleFailure(Exception exception);
}

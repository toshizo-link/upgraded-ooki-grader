using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;

namespace OokiGrader.Host.Jobs;

public static class GeminiBatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the direct-Gemini Batch REST adapter, durable request stager,
    /// and batch state-machine worker.
    /// </summary>
    public static IServiceCollection AddGeminiBatchProcessing(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runWorker = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<AiBatchJobWorkerOptions>(
            configuration.GetSection("Workers:AiBatch"));
        services.AddHttpClient<IAiBatchProviderClient, GeminiBatchClient>(
                client =>
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "OokiGrader/0.1");
                })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression =
                    System.Net.DecompressionMethods.GZip
                    | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            });
        services.AddSingleton<AiBatchRequestStager>();
        services.AddSingleton<GeminiBatchCapabilityProbe>();
        services.AddSingleton<AiBatchJobWorker>();
        if (runWorker)
        {
            services.AddHostedService(serviceProvider =>
                serviceProvider.GetRequiredService<AiBatchJobWorker>());
        }

        return services;
    }
}

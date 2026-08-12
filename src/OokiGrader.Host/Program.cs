using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Ai.OpenRouter;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Api;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Security;
using OokiGrader.Host.Services;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.DependencyInjection;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Security;
using OokiGrader.Preprocessing;
using OokiGrader.Reports.Pdf;
using OokiGrader.Domain.Templates;
using OokiGrader.Domain.Grading;

var (filteredArgs, externalConfigurationPath) =
    ExtractExternalConfigurationArgument(args);
var hostArgs = filteredArgs
    .Append("--hostBuilder:reloadConfigOnChange=false")
    .ToArray();
var builder = WebApplication.CreateBuilder(hostArgs);
if (externalConfigurationPath is not null)
{
    builder.Configuration.AddJsonFile(
        externalConfigurationPath,
        optional: false,
        reloadOnChange: false);
}
var geminiDirectEnabled = builder.Configuration.GetValue(
    "Features:Ai.GeminiDirect",
    false);
var openRouterEnabled = builder.Configuration.GetValue(
    "Features:Ai.OpenRouter",
    false);
var standardAiEnabled = geminiDirectEnabled || openRouterEnabled;
var semanticGradingEnabled = builder.Configuration.GetValue(
    "Features:Grading.Semantic",
    false);
var adjudicationEnabled = builder.Configuration.GetValue(
    "Features:Ai.Adjudication",
    false);
var templateGenerationEnabled = builder.Configuration.GetValue(
    "Features:Ai.TemplateGeneration",
    false);
var pdfReportsEnabled = builder.Configuration.GetValue(
    "Features:Reports.Pdf",
    false);

builder.Host.UseWindowsService(options => options.ServiceName = "Ooki Grader");

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi("v1");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IClock>(OokiGrader.Application.Abstractions.SystemClock.Instance);
builder.Services.AddSingleton<IUlidGenerator, UlidGenerator>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<ISessionTokenService, SessionTokenService>();
builder.Services.AddSingleton<UploadLockProvider>();
builder.Services.AddSingleton<ContentObjectLockProvider>();
builder.Services.AddSingleton<IdempotencyLockProvider>();
builder.Services.AddSingleton<ProtectedCursorCodec>();
builder.Services.AddSingleton<IAiProviderFeaturePolicy>(
    new AiProviderFeaturePolicy(
        geminiDirectEnabled,
        openRouterEnabled));
builder.Services.AddSingleton<HostCertificateHealthService>();
builder.Services.AddSingleton<RosterImportStore>();
builder.Services.AddSingleton<IPreprocessingService, PreprocessingService>();
builder.Services.AddSingleton<IPdfPageCountReader, LocalPdfPageCountReader>();
builder.Services.AddSingleton<IPdfPageRangeExtractor, PdfPageRangeExtractor>();
builder.Services.AddSingleton<ITemplateUnitPlanner, TemplateUnitPlanner>();
builder.Services.AddSingleton<IOrderedScanAssemblyPlanner, OrderedScanAssemblyPlanner>();
builder.Services.Configure<TemplateGenerationBatchOptions>(
    builder.Configuration.GetSection("TemplateGeneration"));
builder.Services.AddScoped<TemplateGenerationBatchService>();
builder.Services.AddScoped<TemplateGenerationFinalizationService>();
builder.Services.AddScoped<OrderedScanBatchService>();
builder.Services.AddSingleton<OrderedScanAssemblyWorker>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<OrderedScanAssemblyWorker>());
builder.Services.AddHostedService<OrderedScanBatchCleanupWorker>();
builder.Services.Configure<SubmissionPreprocessingWorkerOptions>(
    builder.Configuration.GetSection("Workers:SubmissionPreprocessing"));
builder.Services.AddSingleton<SubmissionPreprocessingWorker>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<SubmissionPreprocessingWorker>());
builder.Services.Configure<AiInitialGradingJobWorkerOptions>(
    builder.Configuration.GetSection("Workers:AiInitialGrading"));
builder.Services.Configure<AiAdjudicationJobWorkerOptions>(
    builder.Configuration.GetSection("Workers:AiAdjudication"));
if (adjudicationEnabled)
{
    builder.Services.AddSingleton<AiAdjudicationJobScheduler>();
}

builder.Services.AddSingleton<AiInitialGradingJobWorker>();
if (standardAiEnabled && semanticGradingEnabled)
{
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<AiInitialGradingJobWorker>());
}
builder.Services.AddSingleton<AiAdjudicationJobWorker>();
if (standardAiEnabled && semanticGradingEnabled && adjudicationEnabled)
{
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<AiAdjudicationJobWorker>());
}

builder.Services.Configure<AiNameTranscriptionJobWorkerOptions>(
    builder.Configuration.GetSection("Workers:AiNameTranscription"));
builder.Services.AddSingleton<AiNameTranscriptionJobWorker>();
if (standardAiEnabled)
{
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<AiNameTranscriptionJobWorker>());
}
builder.Services.Configure<TemplateExtractionJobWorkerOptions>(
    builder.Configuration.GetSection("Workers:TemplateExtraction"));
builder.Services.AddSingleton<TemplateExtractionJobWorker>();
builder.Services.Configure<TemplateGenerationUnitJobWorkerOptions>(
    builder.Configuration.GetSection("Workers:TemplateGenerationUnit"));
builder.Services.AddSingleton<TemplateGenerationUnitJobWorker>();
if (standardAiEnabled && templateGenerationEnabled)
{
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<TemplateExtractionJobWorker>());
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<TemplateGenerationUnitJobWorker>());
}
builder.Services.AddSingleton<IResultPdfRenderer, ResultPdfRenderer>();
builder.Services.AddSingleton<ResultPdfJobWorker>();
builder.Services.AddSingleton<BulkTranscriptExportJobWorker>();
if (pdfReportsEnabled)
{
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<ResultPdfJobWorker>());
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<BulkTranscriptExportJobWorker>());
}
builder.Services.AddSingleton<IAiPromptBundleCatalog, ApprovedPromptBundleCatalog>();
builder.Services.AddHttpClient<IAiProviderClient, GeminiDirectClient>(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OokiGrader/0.1");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression =
            System.Net.DecompressionMethods.GZip
            | System.Net.DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    });
builder.Services.AddHttpClient<IAiProviderClient, OpenRouterClient>(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OokiGrader/0.1");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression =
            System.Net.DecompressionMethods.GZip
            | System.Net.DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    });
builder.Services.AddSingleton<IAiProviderClientResolver, AiProviderClientResolver>();
builder.Services.AddGeminiBatchProcessing(
    builder.Configuration,
    runWorker: geminiDirectEnabled && semanticGradingEnabled);
builder.Services.AddHostedService<ProviderFreeJobWorker>();
builder.Services.AddHostedService<RetentionJobWorker>();
builder.Services.AddHostedService<UploadCleanupWorker>();

var configuredDataRoot = builder.Configuration["Data:Root"] ?? ".data";
var dataRoot = Path.IsPathFullyQualified(configuredDataRoot)
    ? configuredDataRoot
    : Path.GetFullPath(configuredDataRoot, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataRoot);
var databasePath = Path.Combine(dataRoot, "ooki-grader.db");
var secretRoot = Path.Combine(dataRoot, "secrets");
var dataProtectionKeyRoot = Path.Combine(dataRoot, "data-protection-keys");
Directory.CreateDirectory(dataProtectionKeyRoot);
var testingEnvironment = builder.Environment.IsEnvironment("Testing");
var useDevelopmentDataProtectionSecretStore =
    OperatingSystem.IsMacOS()
    && builder.Environment.IsDevelopment()
    && !testingEnvironment;
if (useDevelopmentDataProtectionSecretStore)
{
    RestrictDevelopmentKeyRing(dataProtectionKeyRoot);
}

var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("OokiGrader")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyRoot));
if (OperatingSystem.IsWindows())
{
#pragma warning disable CA1416 // The product's supported production host is Windows.
    dataProtection.ProtectKeysWithDpapi();
#pragma warning restore CA1416
}
else if (!builder.Environment.IsDevelopment()
         && !testingEnvironment)
{
    throw new PlatformNotSupportedException(
        "Production secret and Data Protection key storage require Windows DPAPI.");
}

builder.Services.AddSingleton<IAiSecretStore>(serviceProvider =>
{
    if (testingEnvironment)
    {
        return new InMemoryAiSecretStore();
    }

    if (OperatingSystem.IsWindows())
    {
        return new WindowsDpapiAiSecretStore(new WindowsDpapiAiSecretStoreOptions
        {
            RootPath = secretRoot,
        });
    }

    if (useDevelopmentDataProtectionSecretStore)
    {
        return new DataProtectionFileAiSecretStore(
            new DataProtectionFileAiSecretStoreOptions
            {
                RootPath = secretRoot,
            },
            serviceProvider.GetRequiredService<IDataProtectionProvider>());
    }

    return new InMemoryAiSecretStore();
});

var configuredObjectStore = builder.Configuration["Data:ObjectStore"];
var objectStoreRoot = string.IsNullOrWhiteSpace(configuredObjectStore)
    ? Path.Combine(dataRoot, "objects")
    : Path.IsPathFullyQualified(configuredObjectStore)
        ? configuredObjectStore
        : Path.GetFullPath(configuredObjectStore, builder.Environment.ContentRootPath);
builder.Services.AddOokiPersistence(new OokiPersistenceOptions
{
    DatabasePath = databasePath,
    ContentRootPath = objectStoreRoot,
});
var configuredBackupRoot = builder.Configuration["Backup:DestinationRoot"];
var backupRoot = string.IsNullOrWhiteSpace(configuredBackupRoot)
    ? null
    : Path.IsPathFullyQualified(configuredBackupRoot)
        ? configuredBackupRoot
        : Path.GetFullPath(
            configuredBackupRoot,
            builder.Environment.ContentRootPath);
builder.Services.AddOokiBackups(new BackupOptions
{
    DatabasePath = databasePath,
    ContentRootPath = objectStoreRoot,
    SecretEnvelopeRootPath = secretRoot,
    DestinationRootPath = backupRoot,
    Enabled = builder.Configuration.GetValue("Backup:Enabled", false),
    DestinationEncryptionConfirmed = builder.Configuration.GetValue(
        "Backup:DestinationEncryptionConfirmed",
        false),
    IncludeManagedScans = builder.Configuration.GetValue(
        "Backup:IncludeManagedScans",
        false),
    IncludeReports = builder.Configuration.GetValue(
        "Backup:IncludeReports",
        true),
    ScheduleLocalHour = builder.Configuration.GetValue(
        "Backup:ScheduleLocalHour",
        2),
    ScheduleLocalMinute = builder.Configuration.GetValue(
        "Backup:ScheduleLocalMinute",
        0),
    DailyRetentionDays = builder.Configuration.GetValue(
        "Backup:DailyRetentionDays",
        14),
    WeeklyRetentionWeeks = builder.Configuration.GetValue(
        "Backup:WeeklyRetentionWeeks",
        8),
    MonthlyRetentionMonths = builder.Configuration.GetValue(
        "Backup:MonthlyRetentionMonths",
        12),
});
builder.Services.AddScoped<IStaffAuthenticationService, StaffAuthenticationService>();
builder.Services.AddScoped<IBootstrapService, BootstrapService>();
builder.Services
    .AddAuthentication(OokiAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, OokiSessionAuthenticationHandler>(
        OokiAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(OokiAuthenticationDefaults.Scheme)
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(
        "administrator",
        policy => policy.RequireRole("administrator"))
    .AddPolicy(
        "teacher",
        policy => policy.RequireRole("administrator", "teacher"))
    .AddPolicy(
        "upload",
        policy => policy.RequireRole("administrator", "teacher", "scanOperator"))
    .AddPolicy(
        "review",
        policy => policy.RequireRole("administrator", "teacher"))
    .AddPolicy(
        "results",
        policy => policy.RequireRole(
            "administrator",
            "teacher",
            "readOnlyReviewer"));
var loginRateLimitPermitLimit = builder.Configuration.GetValue(
    "Security:LoginRateLimit:PermitLimit",
    5);
var loginRateLimitWindow = builder.Configuration.GetValue(
    "Security:LoginRateLimit:Window",
    TimeSpan.FromMinutes(15));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = loginRateLimitPermitLimit,
                Window = loginRateLimitWindow,
                SegmentsPerWindow = 3,
                QueueLimit = 0,
            }));
    options.AddPolicy("search", context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.Identity?.Name
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 60,
                TokensPerPeriod = 30,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueLimit = 0,
            }));
    options.AddPolicy(
        BulkTranscriptExportEndpoints.CreateRateLimitPolicy,
        _ => RateLimitPartition.GetTokenBucketLimiter(
            "bulk-transcript-export-site",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 3,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueLimit = 0,
            }));
});

var app = builder.Build();

var defaultCulture = CultureInfo.GetCultureInfo("ja-JP");
CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
CultureInfo.DefaultThreadCurrentUICulture = defaultCulture;

await InitializePersistenceAsync(app, dataRoot);

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestGuardsMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();
app.UseMiddleware<MaintenanceModeMiddleware>();
app.UseMiddleware<CsrfValidationMiddleware>();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.MapGet("/health/live", () => Results.Ok(new
{
    state = "healthy",
    checkedAt = DateTimeOffset.UtcNow,
})).AllowAnonymous();

app.MapGet(
    "/health/ready",
    async (
        OokiGraderDbContext database,
        HostCertificateHealthService certificateHealthService,
        CancellationToken cancellationToken) =>
    {
        var canConnect = await database.Database.CanConnectAsync(cancellationToken);
        var dataRootWritable = IsDirectoryWritable(dataRoot);
        var schemaCurrent = canConnect
            && !(await database.Database
                .GetPendingMigrationsAsync(cancellationToken)).Any();
        var reserveHealthy = false;
        if (canConnect)
        {
            var reserveBytes = await database.SiteSettings
                .AsNoTracking()
                .Select(settings => settings.PhysicalFreeReserveBytes)
                .SingleAsync(cancellationToken);
            reserveHealthy = HasPhysicalReserve(dataRoot, reserveBytes);
        }

        var certificate = certificateHealthService.Read();
        var certificateUsable = certificate.State != "unavailable";
        return canConnect
            && schemaCurrent
            && dataRootWritable
            && reserveHealthy
            && certificateUsable
            ? Results.Ok(new
            {
                state = "healthy",
                database = "healthy",
                schema = "healthy",
                storage = "healthy",
                physicalStorage = "healthy",
                certificate = certificate.State,
                checkedAt = DateTimeOffset.UtcNow,
            })
            : Results.Json(
                new
                {
                    state = "unhealthy",
                    database = canConnect ? "healthy" : "unhealthy",
                    schema = schemaCurrent ? "healthy" : "unhealthy",
                    storage = dataRootWritable ? "healthy" : "unhealthy",
                    physicalStorage = reserveHealthy
                        ? "healthy"
                        : "unhealthy",
                    certificate = certificate.State,
                    checkedAt = DateTimeOffset.UtcNow,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }).AllowAnonymous();

app.MapAuthEndpoints();
app.MapCapabilitiesEndpoints();
app.MapStaffEndpoints();
app.MapStudentsEndpoints();
app.MapRosterImportEndpoints();
app.MapTemplatesEndpoints();
app.MapTemplateAutomationEndpoints();
app.MapTemplateGenerationBatchEndpoints();
app.MapTestSessionsEndpoints();
app.MapUploadsEndpoints();
app.MapOrderedScanBatchEndpoints();
app.MapSubmissionsEndpoints();
app.MapReviewEndpoints();
app.MapResultsEndpoints();
app.MapAdminEndpoints(dataRoot);
app.MapBackupAdminEndpoints();
app.MapAiAdminEndpoints();
app.MapAiBatchAdminEndpoints();
app.MapReportsEndpoints();
app.MapBulkTranscriptExportEndpoints();
app.MapEventsEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html").AllowAnonymous();

await app.RunAsync();

static async Task InitializePersistenceAsync(WebApplication application, string root)
{
    await using var scope = application.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<OokiDatabaseInitializer>();
    await initializer.InitializeAsync(
        new OokiDatabaseInitializationOptions(root),
        application.Lifetime.ApplicationStopping);
    var promptCatalog =
        scope.ServiceProvider.GetRequiredService<IAiPromptBundleCatalog>();
    var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var providerFeaturePolicy = scope.ServiceProvider
        .GetRequiredService<IAiProviderFeaturePolicy>();
    _ = await AiAdminEndpoints.EnsureCurrentProfilesAsync(
        scope.ServiceProvider.GetRequiredService<OokiGraderDbContext>(),
        promptCatalog,
        timeProvider,
        providerFeaturePolicy,
        application.Lifetime.ApplicationStopping);
    var bootstrap = scope.ServiceProvider.GetRequiredService<IBootstrapService>();
    await bootstrap.EnsureTokenAsync(application.Lifetime.ApplicationStopping);
}

static bool IsDirectoryWritable(string root)
{
    try
    {
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".write-probe-{Guid.NewGuid():N}");
        using (File.Create(probe, 1, FileOptions.DeleteOnClose))
        {
        }

        return true;
    }
    catch (Exception exception) when (
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
    {
        return false;
    }
}

static bool HasPhysicalReserve(string root, long reserveBytes)
{
    try
    {
        var pathRoot = Path.GetPathRoot(Path.GetFullPath(root));
        return !string.IsNullOrEmpty(pathRoot)
            && new DriveInfo(pathRoot).AvailableFreeSpace >= reserveBytes;
    }
    catch (Exception exception) when (
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
    {
        return false;
    }
}

static void RestrictDevelopmentKeyRing(string root)
{
    if (OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Development key-ring permissions are available only on Unix hosts.");
    }

    const UnixFileMode directoryMode =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;
    const UnixFileMode fileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
    {
        throw new IOException(
            "The development Data Protection key ring cannot be a symbolic link.");
    }

    File.SetUnixFileMode(root, directoryMode);
    foreach (var path in Directory.EnumerateFiles(root))
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The development Data Protection key ring cannot contain symbolic links.");
        }

        File.SetUnixFileMode(path, fileMode);
    }
}

static (string[] FilteredArgs, string? ConfigurationPath)
    ExtractExternalConfigurationArgument(string[] arguments)
{
    const string option = "--ooki-config";
    var filtered = new List<string>(arguments.Length);
    string? configurationPath = null;
    for (var index = 0; index < arguments.Length; index++)
    {
        if (!string.Equals(
            arguments[index],
            option,
            StringComparison.OrdinalIgnoreCase))
        {
            filtered.Add(arguments[index]);
            continue;
        }

        if (configurationPath is not null
            || index + 1 >= arguments.Length
            || string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new InvalidOperationException(
                "The external Ooki Grader configuration argument is invalid.");
        }

        var configuredPath = arguments[++index];
        if (!Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException(
                "The external Ooki Grader configuration must use an absolute path.");
        }

        configurationPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(configurationPath)
            || (File.GetAttributes(configurationPath)
                & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The external Ooki Grader configuration must be an existing absolute regular file.");
        }
    }

    return (filtered.ToArray(), configurationPath);
}

public partial class Program;

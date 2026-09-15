using Arkimentum.AppMonitor.Api.Data;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        var options = ServerOptions.FromConfiguration(configuration);
        services.AddSingleton(options);

        // Application Insights (connection string comes from APPLICATIONINSIGHTS_CONNECTION_STRING).
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Azure SQL through the Function App's managed identity: the connection string carries
        // "Authentication=Active Directory Default" and no secret. EnableRetryOnFailure covers the
        // cold start of a serverless database that has auto-paused.
        var connectionString = configuration["SqlConnection"] ?? configuration.GetConnectionString("SqlConnection");
        var useSqlite = string.Equals(configuration["SqlProvider"], "Sqlite", StringComparison.OrdinalIgnoreCase);
        services.AddDbContext<AppMonitorDbContext>(builder =>
        {
            if (useSqlite)
                builder.UseSqlite(connectionString ?? "Data Source=appmonitor-local.db");
            else
                builder.UseSqlServer(connectionString, sql =>
                {
                    sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                    sql.CommandTimeout(60);
                });
        });
        if (useSqlite) services.AddHostedService<LocalDatabaseInitializer>();

        services.AddSingleton<IEnrollRateLimiter, EnrollRateLimiter>();

        services.AddSingleton<IReportArchive>(sp =>
        {
            var serverOptions = sp.GetRequiredService<ServerOptions>();
            if (string.IsNullOrWhiteSpace(serverOptions.BlobServiceUri) ||
                !Uri.TryCreate(serverOptions.BlobServiceUri, UriKind.Absolute, out _))
            {
                sp.GetRequiredService<ILogger<NullReportArchive>>()
                    .LogWarning("BlobServiceUri is not configured; raw device reports are not archived");
                return new NullReportArchive();
            }
            return new BlobReportArchive(serverOptions, sp.GetRequiredService<ILogger<BlobReportArchive>>());
        });

        // Local development only: AZURE_FUNCTIONS_ENVIRONMENT=Development plus DevBypassAdminAuth=true accepts
        // an unsigned "Bearer dev:{tenantId}:{upn}:{roles}" token so the console can be exercised without Entra ID.
        var development = string.Equals(configuration["AZURE_FUNCTIONS_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);
        var bypass = string.Equals(configuration["DevBypassAdminAuth"], "true", StringComparison.OrdinalIgnoreCase);
        if (development && bypass)
            services.AddSingleton<IAdminTokenValidator, DevelopmentAdminTokenValidator>();
        else
            services.AddSingleton<IAdminTokenValidator, EntraAdminTokenValidator>();

        services.AddScoped<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddScoped<DeviceService>();
        services.AddScoped<AdminService>();
    })
    .Build();

host.Run();

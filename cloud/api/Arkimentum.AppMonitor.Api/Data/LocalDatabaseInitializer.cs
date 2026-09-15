using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Api.Data;

/// <summary>
/// LOCAL DEVELOPMENT ONLY. Registered from Program.cs when SqlProvider=Sqlite, so `func start` works on a machine
/// without SQL Server: it creates the schema from the model instead of running the SQL Server migrations. Azure
/// always uses the SQL Server provider and the migrations in Data/Migrations.
/// </summary>
public sealed class LocalDatabaseInitializer(IServiceProvider services, ILogger<LocalDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppMonitorDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        logger.LogWarning("Using the local SQLite database at {DataSource}", db.Database.GetConnectionString());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Arkimentum.AppMonitor.Api.Data;

/// <summary>
/// Used by <c>dotnet ef migrations add</c> / <c>dotnet ef database update</c> / <c>dotnet ef migrations bundle</c>.
/// The connection string comes from the APPMONITOR_SQL environment variable; when it is missing a placeholder is
/// used, which is enough to scaffold a migration (nothing connects).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppMonitorDbContext>
{
    public AppMonitorDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("APPMONITOR_SQL")
                               ?? "Server=(localdb)\\MSSQLLocalDB;Database=appmon-design;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<AppMonitorDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new AppMonitorDbContext(options);
    }
}

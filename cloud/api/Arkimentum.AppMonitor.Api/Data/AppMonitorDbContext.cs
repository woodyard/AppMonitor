using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore;

namespace Arkimentum.AppMonitor.Api.Data;

public sealed class AppMonitorDbContext(DbContextOptions<AppMonitorDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationConfig> OrganizationConfigs => Set<OrganizationConfig>();
    public DbSet<OrganizationConfigHistory> OrganizationConfigHistory => Set<OrganizationConfigHistory>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceApp> DeviceApps => Set<DeviceApp>();
    public DbSet<DeviceUpdate> DeviceUpdates => Set<DeviceUpdate>();
    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();
    public DbSet<DeviceCommandRow> DeviceCommands => Set<DeviceCommandRow>();
    public DbSet<ReleaseManifestRow> ReleaseManifests => Set<ReleaseManifestRow>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>(e =>
        {
            e.ToTable("Organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.EntraTenantId).HasMaxLength(64);
            e.Property(x => x.EnrollmentKeyHash).HasMaxLength(32).IsRequired();
            // Unique, but many organizations may have no tenant mapping yet: EF emits
            // "WHERE [EntraTenantId] IS NOT NULL" on SQL Server, and SQLite treats NULLs as distinct.
            e.HasIndex(x => x.EntraTenantId).IsUnique();
            e.HasIndex(x => x.Name);
            e.HasOne(x => x.Config).WithOne(c => c.Organization!).HasForeignKey<OrganizationConfig>(c => c.OrganizationId);
        });

        b.Entity<OrganizationConfig>(e =>
        {
            e.ToTable("OrganizationConfigs");
            e.HasKey(x => x.OrganizationId);
            e.Property(x => x.ConfigVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.SettingsJson).IsRequired();          // nvarchar(max)
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        b.Entity<OrganizationConfigHistory>(e =>
        {
            e.ToTable("OrganizationConfigHistory");
            e.HasKey(x => x.Id);
            e.Property(x => x.ConfigVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.SettingsJson).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.HasIndex(x => new { x.OrganizationId, x.UpdatedUtc });
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("Devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.MachineGuid).HasMaxLength(64).IsRequired();
            e.Property(x => x.DeviceName).HasMaxLength(256).IsRequired();
            e.Property(x => x.EntraTenantId).HasMaxLength(64);
            e.Property(x => x.EntraDeviceId).HasMaxLength(64);
            e.Property(x => x.OsVersion).HasMaxLength(128);
            e.Property(x => x.AgentVersion).HasMaxLength(64);
            e.Property(x => x.DeviceKeyHash).HasMaxLength(32).IsRequired();
            e.Property(x => x.ConfigVersionApplied).HasMaxLength(64);
            e.Property(x => x.LastLogonUser).HasMaxLength(256);
            e.HasIndex(x => new { x.OrganizationId, x.MachineGuid }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.IsDeleted, x.DeviceName });
            e.HasIndex(x => new { x.OrganizationId, x.LastSeenUtc });
            e.HasOne(x => x.Organization).WithMany(o => o.Devices).HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceApp>(e =>
        {
            e.ToTable("DeviceApps");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(400).IsRequired();
            e.Property(x => x.Version).HasMaxLength(128);
            e.Property(x => x.Publisher).HasMaxLength(256);
            e.Property(x => x.WingetId).HasMaxLength(256);
            e.Property(x => x.AvailableVersion).HasMaxLength(128);
            e.Property(x => x.CatalogAppId).HasMaxLength(128);
            e.Property(x => x.MonitoredAppId).HasMaxLength(128);
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => new { x.OrganizationId, x.WingetId });
            e.HasIndex(x => new { x.OrganizationId, x.DisplayName });
            e.HasOne(x => x.Device).WithMany(d => d.Apps).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceUpdate>(e =>
        {
            e.ToTable("DeviceUpdates");
            e.HasKey(x => x.Id);
            e.Property(x => x.AppId).HasMaxLength(128).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(400);
            e.Property(x => x.InstalledVersion).HasMaxLength(128);
            e.Property(x => x.AvailableVersion).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => new { x.OrganizationId, x.State });
            e.HasOne(x => x.Device).WithMany(d => d.Updates).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceEvent>(e =>
        {
            e.ToTable("DeviceEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.AppId).HasMaxLength(128);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.FromVersion).HasMaxLength(128);
            e.Property(x => x.ToVersion).HasMaxLength(128);
            e.HasIndex(x => new { x.OrganizationId, x.OccurredUtc });
            e.HasIndex(x => new { x.DeviceId, x.OccurredUtc });
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceCommandRow>(e =>
        {
            e.ToTable("DeviceCommands");
            e.HasKey(x => x.Id);
            e.Property(x => x.Argument).HasMaxLength(1000);
            e.Property(x => x.IssuedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.DeviceId, x.AcknowledgedUtc });
            e.HasOne(x => x.Device).WithMany(d => d.Commands).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReleaseManifestRow>(e =>
        {
            e.ToTable("ReleaseManifests");
            e.HasKey(x => x.Channel);
            e.Property(x => x.Channel).HasMaxLength(32);
            e.Property(x => x.Json).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(256).IsRequired();
            e.Property(x => x.Action).HasMaxLength(128).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.TargetId).HasMaxLength(128);
            e.Property(x => x.Details).HasMaxLength(4000);
            e.HasIndex(x => new { x.OrganizationId, x.Utc });
        });
        // Enums are stored as their integer value (EF Core's default), which matches the numeric values in
        // Core's Models/Enums.cs - so a column value read back from SQL means the same thing on the device.

        // SQLite (unit tests and local development) has no native DateTimeOffset, and its provider refuses to
        // ORDER BY or compare one. Store them as the order-preserving binary form there; Azure SQL keeps the
        // native datetimeoffset column type.
        if (Database.IsSqlite())
        {
            foreach (var entityType in b.Model.GetEntityTypes())
                foreach (var property in entityType.GetProperties())
                    if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                        property.SetValueConverter(typeof(DateTimeOffsetToBinaryConverter));
        }
    }
}

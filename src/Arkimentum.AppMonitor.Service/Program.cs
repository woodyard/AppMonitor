using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Logging;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service;
using Arkimentum.AppMonitor.Service.Cloud;
using Arkimentum.AppMonitor.Service.State;
using Arkimentum.AppMonitor.Service.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.EventLog;

// ---------------------------------------------------------------------------------------------------------------------
// Arkimentum AppMonitor Agent - Windows service entry point.
//
//   Arkimentum.AppMonitor.Service.exe                 run under the Service Control Manager (normal operation)
//   Arkimentum.AppMonitor.Service.exe --console       run interactively in a console (Ctrl+C to stop); requires elevation
//   Arkimentum.AppMonitor.Service.exe --scan-once     run one scan in the console and exit (for troubleshooting)
//   Arkimentum.AppMonitor.Service.exe --show-config   print the effective registry configuration and exit
//   Arkimentum.AppMonitor.Service.exe --no-delay      skip the configured startup delay (console/testing)
//   Arkimentum.AppMonitor.Service.exe --user-config   TESTING ONLY: read configuration from HKCU instead of HKLM and keep
//                                                 logs/state under %LOCALAPPDATA%, so the pipeline can be exercised
//                                                 without administrative rights
//   Arkimentum.AppMonitor.Service.exe --prerequisites check and, if needed, install/repair winget (App Installer) as the
//                                                 current elevated user or SYSTEM, then exit: 0 healthy, 2 unhealthy, 1 error
//   Arkimentum.AppMonitor.Service.exe --cloud-status  print cloud-status.json and whether a device credential exists, then exit
//   Arkimentum.AppMonitor.Service.exe --cloud-enroll  enroll (or re-enroll) with the cloud service now, then exit: 0 ok, 1 failed
//   Arkimentum.AppMonitor.Service.exe --report-now    send one device report now, then exit: 0 ok, 1 failed
//   Arkimentum.AppMonitor.Service.exe --check-update  check the agent release feed, then exit: 0 up to date, 2 update available, 1 error
//   Arkimentum.AppMonitor.Service.exe --update-now    download, verify and start the agent update now, then exit: 0 ok, 1 failed
//   Arkimentum.AppMonitor.Service.exe --version
// ---------------------------------------------------------------------------------------------------------------------

var console = args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase));
var scanOnce = args.Any(a => a.Equals("--scan-once", StringComparison.OrdinalIgnoreCase));
var showConfig = args.Any(a => a.Equals("--show-config", StringComparison.OrdinalIgnoreCase));
var noDelay = args.Any(a => a.Equals("--no-delay", StringComparison.OrdinalIgnoreCase));
var userConfig = args.Any(a => a.Equals("--user-config", StringComparison.OrdinalIgnoreCase));
if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine($"{AgentSettings.ProductName} service {UpdateCoordinator.ServiceVersion}");
    return 0;
}

var configHive = userConfig ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;

// Bootstrap: read the configuration once to configure logging (log level/directory need a restart to change).
var catalog = new JsonCatalogProvider(NullLogger<JsonCatalogProvider>.Instance);
var bootstrapReader = new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, catalog, configHive);
AgentSettings bootstrap;
try { bootstrap = bootstrapReader.Read(); } catch { bootstrap = new AgentSettings(); }
if (userConfig)
{
    // Never write to ProgramData in the unprivileged testing mode.
    var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arkimentum", "AppMonitor");
    if (bootstrap.LogDirectory == AgentSettings.DefaultLogDirectory) bootstrap = bootstrap with { LogDirectory = Path.Combine(local, "Logs") };
    if (bootstrap.StateDirectory == AgentSettings.DefaultStateDirectory) bootstrap = bootstrap with { StateDirectory = local };
}

if (showConfig)
{
    Console.WriteLine($"{AgentSettings.ProductName} effective configuration from {configHive.Name} (policy > preference > catalog > default)");
    Console.WriteLine();
    foreach (var p in typeof(AgentSettings).GetProperties().Where(p => p.Name is not ("Apps" or "ValueSources")))
    {
        var v = p.GetValue(bootstrap);
        var text = v is System.Collections.IEnumerable e and not string ? string.Join(",", e.Cast<object>()) : v?.ToString();
        bootstrap.ValueSources.TryGetValue(p.Name, out var src);
        Console.WriteLine($"  {p.Name,-34} {text,-40} {src ?? ""}");
    }
    Console.WriteLine();
    Console.WriteLine($"Apps ({bootstrap.Apps.Count}):");
    foreach (var a in bootstrap.Apps)
    {
        Console.WriteLine($"  [{a.AppId}] {a.DisplayName}  source={a.Source} context={a.Context} enabled={a.Enabled} mandatory={a.Mandatory} deadlineHours={a.DeadlineHours} maxDeferrals={a.MaxDeferrals} deferrals={string.Join("/", a.DeferralOptionsMinutes)} autoInstall={a.AutoInstall} processes={string.Join(",", a.ProcessNames)}");
        if (a.IsWinget) Console.WriteLine($"      winget: {a.WingetId} ({a.WingetSourceName}) {a.WingetExtraArgs}");
        if (a.IsWeb) Console.WriteLine($"      web: {a.VersionUrl} => {a.VersionRegex}\n           {a.DownloadUrl} [{a.InstallerType}] {a.InstallerArgs}");
    }
    return 0;
}

var prerequisitesOnly = args.Any(a => a.Equals("--prerequisites", StringComparison.OrdinalIgnoreCase));
var cloudStatusOnly = args.Any(a => a.Equals("--cloud-status", StringComparison.OrdinalIgnoreCase));
var cloudEnrollOnly = args.Any(a => a.Equals("--cloud-enroll", StringComparison.OrdinalIgnoreCase));
var reportNowOnly = args.Any(a => a.Equals("--report-now", StringComparison.OrdinalIgnoreCase));
var checkUpdateOnly = args.Any(a => a.Equals("--check-update", StringComparison.OrdinalIgnoreCase));
var updateNowOnly = args.Any(a => a.Equals("--update-now", StringComparison.OrdinalIgnoreCase));
var cliOnly = prerequisitesOnly || cloudStatusOnly || cloudEnrollOnly || reportNowOnly || checkUpdateOnly || updateNowOnly;
var runAsService = !console && !scanOnce && !cliOnly && WindowsServiceHelpers.IsWindowsService();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(FileLoggerExtensions.ParseLevel(bootstrap.LogLevel));
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddArkimentumFile(new FileLoggerOptions
{
    Directory = bootstrap.LogDirectory,
    FilePrefix = "Arkimentum.AppMonitor.Service",
    RetentionDays = bootstrap.LogRetentionDays,
    MaxFileSizeMb = bootstrap.MaxLogFileSizeMb,
    MinimumLevel = FileLoggerExtensions.ParseLevel(bootstrap.LogLevel),
});
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(new EventLogSettings { SourceName = AgentSettings.EventLogSource, LogName = "Application" });
    builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Warning);
}
if (!runAsService) builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddWindowsService(o => o.ServiceName = AgentSettings.ServiceName);
builder.Services.AddSingleton<ICatalogProvider>(catalog);
// Organization configuration from the cloud (cached cloud-config.json in the state directory) is layered between the
// policy key and the local preferences; the CloudSyncService keeps the cache fresh.
builder.Services.AddSingleton(sp => new Arkimentum.AppMonitor.Cloud.CloudConfigCache(sp.GetRequiredService<ILogger<Arkimentum.AppMonitor.Cloud.CloudConfigCache>>(), bootstrap.StateDirectory));
builder.Services.AddSingleton(sp => new Arkimentum.AppMonitor.Cloud.DeviceCredentialStore(sp.GetRequiredService<ILogger<Arkimentum.AppMonitor.Cloud.DeviceCredentialStore>>(), bootstrap.StateDirectory));
builder.Services.AddSingleton(sp =>
{
    var cache = sp.GetRequiredService<Arkimentum.AppMonitor.Cloud.CloudConfigCache>();
    return new RegistryConfigurationReader(sp.GetRequiredService<ILogger<RegistryConfigurationReader>>(), catalog, configHive, () => cache.CurrentSettings());
});
builder.Services.AddSingleton(sp => new SettingsProvider(sp.GetRequiredService<RegistryConfigurationReader>(), sp.GetRequiredService<ILogger<SettingsProvider>>())
{
    Overrides = userConfig ? s => s with { LogDirectory = bootstrap.LogDirectory, StateDirectory = bootstrap.StateDirectory } : null,
});
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<InstalledAppScanner>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddSingleton(sp => new TrayLauncher(sp.GetRequiredService<ILogger<TrayLauncher>>(), sp.GetRequiredService<PipeServer>()) { IsInteractiveHost = !runAsService });
builder.Services.AddSingleton<Arkimentum.AppMonitor.Prerequisites.PrerequisiteManager>();
builder.Services.AddSingleton<UpdateCoordinator>();
var workerOptions = new WorkerOptions { ScanOnce = scanOnce, IgnoreStartupDelay = noDelay || console || scanOnce };
builder.Services.AddSingleton(workerOptions);
builder.Services.AddHostedService<Worker>();

// Cloud connection and agent self-update. Both stay idle until they are configured (CloudServerUrl/CloudOrganizationId,
// AgentUpdateFeedUrl or the cloud mirror). In --user-config testing mode the self-updater verifies packages but never
// launches the installer.
builder.Services.AddSingleton(new AgentUpdaterOptions { StateDirectory = bootstrap.StateDirectory, DryRun = userConfig });
builder.Services.AddSingleton(new CloudSyncOptions { StateDirectory = bootstrap.StateDirectory, DryRun = userConfig, CliOnly = cliOnly });
builder.Services.AddSingleton<AgentUpdater>();
builder.Services.AddSingleton<CloudSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CloudSyncService>());

using var host = builder.Build();

var log = host.Services.GetRequiredService<ILogger<Program>>();
AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
TaskScheduler.UnobservedTaskException += (_, e) => { log.LogError(e.Exception, "Unobserved task exception"); e.SetObserved(); };

if (!runAsService && !cliOnly)
{
    Console.WriteLine($"{AgentSettings.ProductName} service {UpdateCoordinator.ServiceVersion} running in console mode. Press Ctrl+C to stop.");
    Console.WriteLine($"Configuration: {configHive.Name}\\{AgentSettings.RegistryRoot}   Logs: {bootstrap.LogDirectory}");
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var elevated = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    if (!elevated)
        log.LogWarning("Console mode is running WITHOUT administrative rights: machine-wide installs and per-user inventory of other users will fail. Start from an elevated prompt for a faithful test.");
    try
    {
        using var sc = new System.ServiceProcess.ServiceController(AgentSettings.ServiceName);
        if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            log.LogWarning("The {Service} Windows service is RUNNING. Tray agents are connected to it, not to this console instance, so per-user applications " +
                           "(user-context installs such as Store/MSIX packages) will show as 'not installed' here and installs may collide. " +
                           "Stop it first for a faithful test: Stop-Service {Service}", AgentSettings.ServiceName, AgentSettings.ServiceName);
    }
    catch (InvalidOperationException) { /* service not installed */ }
}

if (prerequisitesOnly)
{
    // Installer / troubleshooting entry point: check and repair winget once. 0 = healthy, 2 = still unhealthy, 1 = error.
    try
    {
        var manager = host.Services.GetRequiredService<Arkimentum.AppMonitor.Prerequisites.PrerequisiteManager>();
        var settings = host.Services.GetRequiredService<SettingsProvider>().Reload();
        var status = await manager.EnsureAsync(settings, UpdateCoordinator.IsRunningAsSystem, CancellationToken.None, force: false);
        Console.WriteLine($"Prerequisites: {status.Summary}{(status.LastAction is null ? "" : " - " + status.LastAction)}{(status.LastError is null ? "" : " - " + status.LastError)}");
        return status.IsHealthy ? 0 : 2;
    }
    catch (Exception ex)
    {
        log.LogCritical(ex, "Prerequisite check failed");
        return 1;
    }
}

if (cloudStatusOnly)
{
    // Read-only: safe to run while the Windows service is running.
    var sync = host.Services.GetRequiredService<CloudSyncService>();
    var statusFile = new CloudStatusFile(log, bootstrap.StateDirectory);
    var settings = host.Services.GetRequiredService<SettingsProvider>().Reload();
    Console.WriteLine($"{AgentSettings.ProductName} cloud status");
    Console.WriteLine($"  Configured        : {(settings.CloudConfigured ? "yes" : "no")} (CloudServerUrl='{settings.CloudServerUrl}', CloudOrganizationId='{settings.CloudOrganizationId}')");
    Console.WriteLine($"  Config layer      : {(settings.CloudConfigEnabled ? "enabled" : "disabled")}   Reporting: {(settings.CloudReportingEnabled ? "enabled" : "disabled")}   Sync every {settings.CloudSyncIntervalMinutes} min");
    Console.WriteLine($"  Device credential : {(sync.HasCredential ? "present" : "not enrolled")}");
    Console.WriteLine($"  Status file       : {statusFile.FilePath}");
    if (statusFile.Load() is { } status)
    {
        Console.WriteLine($"  Server URL        : {status.ServerUrl ?? "-"}");
        Console.WriteLine($"  Organization      : {status.OrganizationName ?? "-"}");
        Console.WriteLine($"  Device id         : {status.DeviceId?.ToString() ?? "-"}");
        Console.WriteLine($"  Enrolled (UTC)    : {status.EnrolledUtc?.ToString("u") ?? "-"}");
        Console.WriteLine($"  Last config       : {status.LastConfigVersion ?? "-"} at {status.LastConfigUtc?.ToString("u") ?? "-"}");
        Console.WriteLine($"  Last report (UTC) : {status.LastReportUtc?.ToString("u") ?? "-"}");
        Console.WriteLine($"  Last error        : {status.LastError ?? "-"}");
    }
    else
    {
        Console.WriteLine("  (no status file yet - the service has not talked to the cloud service)");
    }
    var cached = host.Services.GetRequiredService<Arkimentum.AppMonitor.Cloud.CloudConfigCache>().Load();
    Console.WriteLine("  Cached config     : " + (cached is null ? "none" : $"{cached.ConfigVersion} from {cached.OrganizationName} fetched {cached.FetchedUtc:u} ({cached.Settings.Global.Count} global value(s), {cached.Settings.Apps.Count} app(s))"));
    return 0;
}

if (cloudEnrollOnly)
{
    var sync = host.Services.GetRequiredService<CloudSyncService>();
    var ok = await sync.EnrollNowAsync(CancellationToken.None);
    Console.WriteLine(ok ? "Enrollment succeeded." : "Enrollment failed - see the log for details.");
    return ok ? 0 : 1;
}

if (reportNowOnly)
{
    var sync = host.Services.GetRequiredService<CloudSyncService>();
    host.Services.GetRequiredService<UpdateCoordinator>().LoadStateForCli();
    var ok = await sync.ReportNowAsync(CancellationToken.None);
    Console.WriteLine(ok ? "Report sent." : "Report failed - see the log for details.");
    return ok ? 0 : 1;
}

if (checkUpdateOnly || updateNowOnly)
{
    var updater = host.Services.GetRequiredService<AgentUpdater>();
    _ = host.Services.GetRequiredService<CloudSyncService>();   // wires the cloud mirror resolver
    host.Services.GetRequiredService<SettingsProvider>().Reload();
    updater.ProcessStartupMarker();
    var outcome = checkUpdateOnly
        ? await updater.CheckAsync("--check-update", null, CancellationToken.None)
        : await updater.UpdateAsync("--update-now", null, CancellationToken.None);
    Console.WriteLine($"Agent {UpdateCoordinator.ServiceVersion}: {outcome.Action} - {outcome.Reason}");
    if (outcome.Manifest is { } m) Console.WriteLine($"  Manifest {m.Version} ({m.Channel}) {m.PackageUrl} sha256={m.Sha256}");
    if (outcome.PackageDirectory is { } dir) Console.WriteLine($"  Package: {dir}");
    return outcome.Action switch
    {
        AgentUpdateAction.Failed => 1,
        AgentUpdateAction.UpdateAvailable => 2,
        _ => 0,
    };
}

try
{
    // Resolve before RunAsync: the host disposes its service provider when it stops, and resolving afterwards
    // threw ObjectDisposedException on every service stop ("Service terminated unexpectedly" in the log).
    var coordinator = host.Services.GetRequiredService<UpdateCoordinator>();
    await host.RunAsync();
    await coordinator.DisposeAsync();
    return 0;
}
catch (Exception ex)
{
    log.LogCritical(ex, "Service terminated unexpectedly");
    return 1;
}

public partial class Program { }

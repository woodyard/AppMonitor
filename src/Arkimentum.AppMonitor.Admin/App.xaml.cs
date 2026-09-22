using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Admin.ViewModels;
using Arkimentum.AppMonitor.Admin.Views;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Logging;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin;

/// <summary>
/// Composition root of the administrative console.
///
/// <para>
/// Startup order matters: the brand theme first (so even the elevation failure message is branded), then the
/// elevation check, then the registry write probe, then the host.
/// </para>
///
/// <para>
/// Elevation and the write probe belong to the <b>deprecated</b> per-machine work only — <c>--local</c> and the
/// headless <c>--export</c>/<c>--import</c>. The console's normal job is to manage an organization in the cloud,
/// which changes nothing here, so it runs as a standard user and never raises a UAC prompt. <c>--user-config</c>
/// stays what it was: everything bound to HKCU for testing, unprivileged, with a banner that never goes away.
/// </para>
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _log;
    private CommandLineOptions _options = CommandLineOptions.Parse([]);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Merges the brand palette over the Fluent theme and keeps following the system light/dark setting.
        BrandTheme.Initialize();
        ApplyFluentBrandFixes();

        _options = CommandLineOptions.Parse(e.Args);

        if (!EnsureElevated()) return;

        _host = BuildHost();
        _log = _host.Services.GetRequiredService<ILogger<App>>();
        HookExceptionHandlers();

        _log.LogInformation("{Product} {Version} started as {User} (elevated={Elevated}, userConfig={UserConfig}, local={Local}).",
            Strings.ProductName, AdminAppInfo.Version, AdminAppInfo.UserName, AdminAppInfo.IsElevated,
            _options.UserConfig, _options.Local);
        if (_options.UserConfig)
        {
            _log.LogWarning("TESTING MODE: --user-config is active. Configuration is read from and written to HKCU, " +
                            "and service control is disabled. Nothing here affects the machine configuration.");
        }
        if (_options.Local)
        {
            _log.LogWarning("--local is deprecated. The per-machine pages will be removed; manage settings centrally " +
                            "from the organization pages or the browser admin console.");
        }
        foreach (var unknown in _options.Unknown) _log.LogWarning("Ignoring unknown command-line argument '{Argument}'.", unknown);

        // Only the per-machine work writes a configuration layer, and only it may refuse to start over one it
        // cannot open. Organization mode never touches the registry here, so it must not be probed for it.
        if (_options.ManagesThisMachine)
        {
            var store = _host.Services.GetRequiredService<SettingsStoreService>();
            if (!store.CanWrite(out var error))
            {
                _log.LogError("The configuration key {Key} cannot be opened for writing: {Error}", store.PreferencePath, error);
                _host.Services.GetRequiredService<IDialogService>().ShowMessage(
                    Strings.RegistryNotWritableTitle, Strings.RegistryNotWritable(store.PreferencePath, error), DialogTone.Critical);
                Shutdown(1);
                return;
            }
        }

        if (_options.IsHeadless)
        {
            var code = _host.Services.GetRequiredService<HeadlessRunner>().Run(_options);
            _log.LogInformation("Headless run finished with exit code {Code}.", code);
            Shutdown(code);
            return;
        }

        _host.Start();

        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        _host.Services.GetRequiredService<IDialogService>().Owner = window;
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        // With the window on screen, pick up the previous cloud sign-in from the token cache (silently; never a browser).
        _ = viewModel.RestoreSessionAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("Exiting with code {Code}.", e.ApplicationExitCode);
        try
        {
            if (_host is not null)
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Shutting the host down failed.");
        }
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Merges <c>Themes/FluentBrandFixes.xaml</c> last, and again after every theme swap. The Fluent CheckBox and
    /// RadioButton templates use their own check-state keys rather than the accent brushes the brand palette
    /// overrides, so only a dictionary merged after WPF's own Fluent dictionary turns a ticked box olive.
    /// </summary>
    private static void ApplyFluentBrandFixes()
    {
        var fixes = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Arkimentum.AppMonitor.Admin;component/Themes/FluentBrandFixes.xaml"),
        };

        void Append()
        {
            var merged = Current?.Resources.MergedDictionaries;
            if (merged is null) return;
            merged.Remove(fixes);
            merged.Add(fixes);
        }

        Append();
        BrandTheme.Changed += Append;
    }

    // ---------------------------------------------------------------- elevation

    /// <summary>
    /// True when the process may continue. Otherwise it has already asked Windows to start an elevated copy (or
    /// told the user why it cannot) and called <see cref="Application.Shutdown()"/>.
    ///
    /// <para>
    /// Elevation is asked for only when this run actually works on this machine's configuration — the deprecated
    /// <c>--local</c> pages, or a headless export/import. The organization console changes nothing here, so it
    /// opens as a standard user with no UAC prompt at all.
    /// </para>
    /// </summary>
    private bool EnsureElevated()
    {
        if (!_options.RequiresElevation || AdminAppInfo.IsElevated) return true;

        var result = Elevation.Relaunch(_options.RawArguments, waitForExit: _options.IsHeadless, out var exitCode, out _);
        switch (result)
        {
            case ElevationResult.Relaunched:
                Shutdown(_options.IsHeadless ? exitCode : 0);
                return false;
            default:
                new DialogService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DialogService>.Instance)
                    .ShowMessage(Strings.ProductName, Strings.MustRunElevated + "\n\n" + Strings.MustRunElevatedDetail, DialogTone.Critical);
                Shutdown(1);
                return false;
        }
    }

    // ---------------------------------------------------------------- composition

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        // A standard user cannot write under %ProgramData%; the organization console runs as one, and a console
        // without a log is a console nobody can troubleshoot.
        var logDirectory = AdminAppInfo.LogDirectory(_options.UserConfig);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddArkimentumFile(new FileLoggerOptions
        {
            Directory = logDirectory,
            FilePrefix = "Arkimentum.AppMonitor.Admin",
            MinimumLevel = LogLevel.Information,
        });

        var services = builder.Services;
        services.AddSingleton(_options);
        services.AddSingleton(Dispatcher.CurrentDispatcher);
        services.AddSingleton(sp => new PipeClient(sp.GetRequiredService<ILogger<PipeClient>>()));

        services.AddSingleton<SettingsStoreService>();
        services.AddSingleton<ServiceControlService>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<ICatalogProvider>(sp => sp.GetRequiredService<CatalogService>());
        services.AddSingleton<IDetectionTester, DetectionTester>();
        services.AddSingleton<IAppDiscoveryService, AppDiscoveryService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<HeadlessRunner>();
        services.AddSingleton<AdminIpcService>();
        // The pipe to the local service belongs to the deprecated Overview page. Without --local nothing in the
        // console talks to this machine's service, so the client is never started and never connects.
        if (_options.Local) services.AddHostedService(sp => sp.GetRequiredService<AdminIpcService>());

        // The local pages edit the registry; the organization pages edit the document the cloud API serves. Both
        // go through ConfigurationEditor, which only knows an IConfigurationStore — see IConfigurationStore.cs.
        services.AddSingleton<RegistryConfigurationStore>();
        services.AddSingleton<OrganizationConfigurationStore>();
        services.AddSingleton(sp => new ConfigurationEditor(
            sp.GetRequiredService<ILogger<ConfigurationEditor>>(),
            sp.GetRequiredService<RegistryConfigurationStore>(),
            sp.GetRequiredService<CatalogService>(),
            sp.GetRequiredService<IDetectionTester>()));

        // Cloud: one session for the whole console, MSAL for the tokens.
        services.AddSingleton<AdminPreferences>();
        services.AddSingleton<CloudStatusReader>();
        services.AddSingleton<ICloudClientFactory, CloudClientFactory>();
        services.AddSingleton<IAuthenticator, MsalAuthenticator>();
        services.AddSingleton<CloudSession>();

        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<ApplicationsViewModel>();
        services.AddSingleton<ExportImportViewModel>();
        services.AddSingleton<CloudConnectViewModel>();
        services.AddSingleton<OrganizationConfigViewModel>();
        services.AddSingleton<OrganizationDevicesViewModel>();
        services.AddSingleton<OrganizationActivityViewModel>();
        services.AddSingleton<OrganizationInventoryViewModel>();
        services.AddSingleton<OrganizationSettingsViewModel>();
        services.AddSingleton<OrganizationApplicationsViewModel>();
        services.AddSingleton<OrganizationEnrollmentViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<AboutViewModel>();

        return builder.Build();
    }

    // ---------------------------------------------------------------- failure handling

    private void HookExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log(args.ExceptionObject as Exception, "Unhandled application exception");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void Log(Exception? exception, string message)
    {
        try { _log?.LogError(exception, "{Message}", message); }
        catch { /* nothing left to do */ }
    }
}

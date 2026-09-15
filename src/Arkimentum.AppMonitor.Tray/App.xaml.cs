using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Logging;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Services;
using Arkimentum.AppMonitor.Tray.ViewModels;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arkimentum.AppMonitor.Tray;

/// <summary>
/// Composition root. One agent per interactive session; it lives in the tray until the session ends and is
/// driven entirely by the service over the named pipe.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _instance;
    private ILogger<App>? _log;
    private CommandLineOptions _options = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Merges the brand palette over the Fluent theme and keeps following the system light/dark setting.
        BrandTheme.Initialize();

        _options = CommandLineOptions.Parse(e.Args);
        AppInfo.EnsureDirectories();

        // Must happen before any window exists: it is what the notification platform and the taskbar identify us by.
        ToastRegistration.SetProcessAumid(AgentSettings.ToastAppUserModelId);

        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            // Another agent already owns this session: ask it to come forward and leave.
            SingleInstance.SignalPrimary(e.Args);
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        _instance.Activated += OnSecondInstance;
        _instance.StartListening();

        HookExceptionHandlers();

        var settings = ReadRegistrySettings();
        _host = BuildHost(settings);
        _host.Start();

        _log = _host.Services.GetRequiredService<ILogger<App>>();
        _log.LogInformation(
            "{Product} tray agent {Version} started in session {Session} for {User} (show={Show}, minimized={Minimized}, debug={Debug}, toastActivated={Toast})",
            AgentSettings.ProductName, AppInfo.Version, AppInfo.SessionId, AppInfo.UserName,
            _options.Show, _options.Minimized, _options.Debug, _options.ToastActivated);

        if (_options.Show) _host.Services.GetRequiredService<IWindowService>().ShowMain();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("Tray agent exiting with code {Code}", e.ApplicationExitCode);
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
            _log?.LogWarning(ex, "Shutting the host down failed");
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- composition

    private IHost BuildHost(AgentSettings settings)
    {
        var builder = Host.CreateApplicationBuilder();
        var level = _options.Debug ? LogLevel.Debug : FileLoggerExtensions.ParseLevel(settings.LogLevel);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(level);
        builder.Logging.AddArkimentumFile(new FileLoggerOptions
        {
            Directory = AppInfo.LogDirectory,
            FilePrefix = "Arkimentum.AppMonitor.Tray",
            MinimumLevel = level,
            RetentionDays = settings.LogRetentionDays,
            MaxFileSizeMb = settings.MaxLogFileSizeMb,
        });

        var services = builder.Services;
        services.AddSingleton(_options);
        services.AddSingleton(settings);
        services.AddSingleton(Dispatcher.CurrentDispatcher);
        services.AddSingleton(sp => new PipeClient(sp.GetRequiredService<ILogger<PipeClient>>()));

        services.AddSingleton<IpcClientService>();
        services.AddSingleton<AgentStateStore>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<CloseAppsCoordinator>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<UserContextExecutor>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<AgentUpdateViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<AboutViewModel>();

        // Order matters: everything that listens must be subscribed before the pipe client starts delivering.
        services.AddHostedService(sp => sp.GetRequiredService<AgentStateStore>());
        services.AddHostedService(sp => sp.GetRequiredService<CloseAppsCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());
        services.AddHostedService(sp => sp.GetRequiredService<UserContextExecutor>());
        services.AddHostedService(sp => sp.GetRequiredService<TrayIconService>());
        services.AddHostedService(sp => sp.GetRequiredService<IpcClientService>());
        services.AddHostedService<WingetUserRegistrationService>();

        return builder.Build();
    }

    /// <summary>HKLM settings, read-only, purely to pick the log level before the logger exists.</summary>
    private static AgentSettings ReadRegistrySettings()
    {
        try
        {
            var reader = new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, catalog: null);
            return reader.Read();
        }
        catch
        {
            return new AgentSettings();
        }
    }

    // ---------------------------------------------------------------- second instance

    private void OnSecondInstance(string[] args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _log?.LogInformation("A second instance asked this one to come forward ({Args})", string.Join(" ", args));
            _host?.Services.GetRequiredService<IWindowService>().ShowMain();
        });
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

    /// <summary>Failures go to the log, never to a dialog: the agent must not interrupt the user with a crash box.</summary>
    private void Log(Exception? exception, string message)
    {
        try { _log?.LogError(exception, "{Message}", message); }
        catch { /* nothing left to do */ }
    }
}

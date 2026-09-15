using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.ViewModels;
using Arkimentum.AppMonitor.Tray.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>Shows and hides the agent's windows, and opens folders for the user.</summary>
public interface IWindowService
{
    /// <summary>Shows (or un-hides and activates) the main window.</summary>
    void ShowMain();

    void ShowAbout();

    void OpenLogFolder();

    void OpenFolder(string path);

    /// <summary>An owner for modeless dialogs; null when the main window has never been shown.</summary>
    Window? OwnerWindow { get; }

    void Shutdown();
}

/// <inheritdoc />
public sealed class WindowService : IWindowService
{
    private readonly ILogger<WindowService> _log;
    private readonly IServiceProvider _services;

    private MainWindow? _main;
    private AboutWindow? _about;
    private bool _shuttingDown;

    public WindowService(ILogger<WindowService> log, IServiceProvider services)
    {
        _log = log;
        _services = services;
    }

    public Window? OwnerWindow => _main is { IsVisible: true } ? _main : null;

    public void ShowMain()
    {
        if (_main is null)
        {
            _log.LogInformation("Opening the main window");
            _main = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
            _main.Closing += OnMainClosing;
        }

        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;

        // The service may have new information since the window was last open.
        _services.GetRequiredService<MainViewModel>().OnWindowShown();
    }

    public void ShowAbout()
    {
        if (_about is null)
        {
            _about = new AboutWindow { DataContext = _services.GetRequiredService<AboutViewModel>() };
            _about.Closed += (_, _) => _about = null;
            _about.Owner = OwnerWindow;
            if (_about.Owner is null)
            {
                // Opened straight from the tray menu: it needs to stand on its own.
                _about.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                _about.ShowInTaskbar = true;
            }
        }
        _about.Show();
        _about.Activate();
    }

    public void OpenLogFolder() => OpenFolder(AppInfo.LogDirectory);

    public void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            _log.LogInformation("Opening folder {Path}", path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open folder {Path}", path);
        }
    }

    public void Shutdown()
    {
        _log.LogInformation("Shutdown requested");
        _shuttingDown = true;
        Application.Current?.Shutdown();
    }

    /// <summary>Closing the window only hides it: the agent lives in the tray until the session ends.</summary>
    private void OnMainClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown || Application.Current is null) return;
        e.Cancel = true;
        _main?.Hide();
        _log.LogDebug("Main window hidden");
    }
}

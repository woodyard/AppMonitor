using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.UI;
using H.NotifyIcon;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// The notification-area icon: three variants (normal, updates available, attention), a live tooltip and the
/// context menu. Left click opens the main window.
/// </summary>
public sealed class TrayIconService : IHostedService
{
    private enum IconVariant
    {
        Normal,
        Updates,
        Attention,
    }

    private readonly ILogger<TrayIconService> _log;
    private readonly AgentStateStore _store;
    private readonly IpcClientService _ipc;
    private readonly IWindowService _windows;
    private readonly CommandLineOptions _options;
    private readonly Dispatcher _dispatcher;

    private TaskbarIcon? _icon;
    private MenuItem? _checkNowItem;
    private MenuItem? _updateAllItem;
    private IconVariant? _currentVariant;
    private ImageSource? _normal;
    private ImageSource? _updates;
    private ImageSource? _attention;

    public TrayIconService(
        ILogger<TrayIconService> log,
        AgentStateStore store,
        IpcClientService ipc,
        IWindowService windows,
        CommandLineOptions options,
        Dispatcher dispatcher)
    {
        _log = log;
        _store = store;
        _ipc = ipc;
        _windows = windows;
        _options = options;
        _dispatcher = dispatcher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _dispatcher.Invoke(Create);
        _store.Changed += Refresh;
        Refresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.Changed -= Refresh;
        _dispatcher.Invoke(() =>
        {
            _icon?.Dispose();
            _icon = null;
        });
        return Task.CompletedTask;
    }

    private void Create()
    {
        var size = TrayIconPixelSize();
        _normal = LoadIcon("tray-normal", size);
        _updates = LoadIcon("tray-updates", size);
        _attention = LoadIcon("tray-attention", size);

        _icon = new TaskbarIcon
        {
            Id = new Guid("2d6a4f1e-9c6b-4f2a-9a0f-2b5c5b4d9b01"),
            IconSource = _normal,
            ToolTipText = Strings.TrayTooltipDisconnected,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => _windows.ShowMain()),
            ContextMenu = BuildMenu(),
        };
        _icon.ForceCreate(false);
        _log.LogInformation("Tray icon created ({Size} px variants)", size);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = Strings.TrayMenuOpen,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Command = new RelayCommand(() => _windows.ShowMain()),
        });

        _checkNowItem = new MenuItem
        {
            Header = Strings.TrayMenuCheckNow,
            Command = new RelayCommand(CheckNow, () => _store.IsConnected && !_store.ScanInProgress),
        };
        menu.Items.Add(_checkNowItem);

        // Same action as the window's "Update all" button, reachable without opening the window.
        _updateAllItem = new MenuItem
        {
            Header = Strings.UpdateAll,
            Command = new RelayCommand(UpdateAll, () => _store.IsConnected && _store.InstallableUpdates.Count > 0),
        };
        menu.Items.Add(_updateAllItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = Strings.TrayMenuOpenLogFolder,
            Command = new RelayCommand(() => _windows.OpenLogFolder()),
        });
        menu.Items.Add(new MenuItem
        {
            Header = Strings.TrayMenuAbout,
            Command = new RelayCommand(() => _windows.ShowAbout()),
        });

        // The service keeps the agent running, so there is no Exit item outside developer mode.
        if (_options.Debug)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = Strings.TrayMenuExit,
                Command = new RelayCommand(() => _windows.Shutdown()),
            });
        }

        return menu;
    }

    private void CheckNow()
    {
        _log.LogInformation("User requested a scan from the tray menu");
        _ = _ipc.RequestScanAsync();
    }

    private void Refresh()
    {
        if (_icon is null) return;

        var count = _store.ActiveUpdateCount;
        var variant = !_store.IsConnected || count == 0
            ? IconVariant.Normal
            : _store.NeedsAttention ? IconVariant.Attention : IconVariant.Updates;

        if (_currentVariant != variant)
        {
            _currentVariant = variant;
            _icon.IconSource = variant switch
            {
                IconVariant.Attention => _attention,
                IconVariant.Updates => _updates,
                _ => _normal,
            };
            _log.LogDebug("Tray icon variant is now {Variant}", variant);
        }

        var tooltip = !_store.IsConnected
            ? Strings.TrayTooltipDisconnected
            : count == 0
                ? Strings.TrayTooltipUpToDate
                : Strings.TrayTooltipUpdates(count);
        if (!string.Equals(_icon.ToolTipText, tooltip, StringComparison.Ordinal)) _icon.ToolTipText = tooltip;

        (_checkNowItem?.Command as RelayCommand)?.RaiseCanExecuteChanged();
        if (_updateAllItem is not null)
        {
            _updateAllItem.Header = Strings.UpdateAllCount(_store.IsConnected ? _store.InstallableUpdates.Count : 0);
            (_updateAllItem.Command as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void UpdateAll()
    {
        var updates = _store.InstallableUpdates;
        if (updates.Count == 0) return;
        _log.LogInformation("User chose Update all from the tray menu: {Count} update(s)", updates.Count);
        _ = _ipc.InstallAllAsync(updates.Select(u => u.Key).ToList());
    }

    /// <summary>Picks the frame of the .ico that matches the notification area at the current system DPI.</summary>
    private static ImageSource LoadIcon(string name, int size)
    {
        var uri = new Uri($"pack://application:,,,/Arkimentum.AppMonitor.Tray;component/Assets/{name}.ico", UriKind.Absolute);
        var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames
            .OrderBy(f => Math.Abs(f.PixelWidth - size))
            .ThenByDescending(f => f.PixelWidth)
            .First();
        frame.Freeze();
        return frame;
    }

    private static int TrayIconPixelSize()
    {
        try
        {
            var dpi = GetDpiForSystem();
            if (dpi is < 96 or > 960) dpi = 96;
            return (int)Math.Round(16.0 * dpi / 96.0);
        }
        catch { return 16; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}

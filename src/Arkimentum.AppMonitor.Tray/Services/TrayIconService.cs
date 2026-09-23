using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
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
    private readonly NotificationService _notifications;

    /// <summary>Brand terracotta: the badge has to read as "attention" against both light and dark taskbars.</summary>
    private static readonly Brush BadgeFill = Freeze(new SolidColorBrush(Color.FromRgb(0xc7, 0x62, 0x39)));
    private static readonly Brush BadgeText = Brushes.White;
    private static readonly Typeface BadgeTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _badgeIcon;
    private MenuItem? _checkNowItem;
    private MenuItem? _updateAllItem;
    private MenuItem? _agentUpdateItem;
    private IconVariant? _currentVariant;
    private int _currentBadge = -1;
    private int _iconSize = 16;
    private ImageSource? _normal;
    private ImageSource? _updates;
    private ImageSource? _attention;

    public TrayIconService(
        ILogger<TrayIconService> log,
        AgentStateStore store,
        IpcClientService ipc,
        IWindowService windows,
        CommandLineOptions options,
        Dispatcher dispatcher,
        NotificationService notifications)
    {
        _notifications = notifications;
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
        _store.AgentUpdateAnswered += OnAgentUpdateAnswered;
        // Refresh renders the badge and touches the icon, both of which belong to the UI thread.
        _dispatcher.Invoke(Refresh);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.Changed -= Refresh;
        _store.AgentUpdateAnswered -= OnAgentUpdateAnswered;
        _dispatcher.Invoke(() =>
        {
            _icon?.Dispose();
            _icon = null;
            _badgeIcon?.Dispose();
            _badgeIcon = null;
        });
        return Task.CompletedTask;
    }

    private void Create()
    {
        var size = _iconSize = TrayIconPixelSize();
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
            Command = new RelayCommand(CheckNow, () => _store.IsConnected && !_store.IsScanning),
        };
        menu.Items.Add(_checkNowItem);

        // Same action as the window's "Update all" button, reachable without opening the window.
        _updateAllItem = new MenuItem
        {
            Header = Strings.UpdateAll,
            Command = new RelayCommand(UpdateAll,
                () => _store.IsConnected && !_store.UpdateAllPending && _store.InstallableUpdates.Count > 0),
        };
        menu.Items.Add(_updateAllItem);

        // The agent's own update: a check, or - once the service knows a newer release - the update itself.
        _agentUpdateItem = new MenuItem
        {
            Header = Strings.TrayMenuCheckAgentUpdate,
            Command = new RelayCommand(RequestAgentUpdate, CanRequestAgentUpdate),
        };
        menu.Items.Add(_agentUpdateItem);
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

    private async void CheckNow()
    {
        _log.LogInformation("User requested a scan from the tray menu");
        _store.MarkScanRequested();
        if (!await _ipc.RequestScanAsync().ConfigureAwait(true))
        {
            _log.LogWarning("The scan request was not sent: the service pipe is not connected");
            _store.CancelScanRequest();
        }
    }

    /// <summary>
    /// Asks the service to check for a newer agent - and to install it when the state already says one is available,
    /// which is what the item's wording then promises. The service refuses when policy or an install says no.
    /// </summary>
    private async void RequestAgentUpdate()
    {
        // One click checks and, when a newer release exists, installs it: from the menu the window is usually closed,
        // so a check-only first step changed nothing the user could see and needed a second click. The service
        // answers either way (up to date, updating, or why not now), and that answer comes back as a toast.
        _log.LogInformation("User chose the agent update from the tray menu");
        var messageId = await _ipc.RequestAgentUpdateAsync(checkOnly: false).ConfigureAwait(true);
        if (messageId is null)
        {
            _log.LogWarning("The agent update request was not sent: the service pipe is not connected");
            _notifications.ShowFeedback(Strings.AgentUpdateToastTitle, Strings.StatusDisconnected);
            return;
        }
        _menuAgentUpdateRequests.Add(messageId);
        _store.TrackAgentUpdateRequest(messageId);
    }

    /// <summary>Agent update requests sent from this menu, whose answer is shown as a toast.</summary>
    private readonly HashSet<string> _menuAgentUpdateRequests = new(StringComparer.Ordinal);

    private void OnAgentUpdateAnswered(string messageId, bool ok, string? message)
    {
        if (!_menuAgentUpdateRequests.Remove(messageId)) return;
        _notifications.ShowFeedback(Strings.AgentUpdateToastTitle, message ?? Strings.AgentUpdateNoAnswer);
    }

    private bool CanRequestAgentUpdate() =>
        _store.IsConnected && _store.AgentUpdate is { Enabled: true, InProgress: false } && !_store.AgentUpdateRequested;

    private void Refresh()
    {
        if (_icon is null) return;

        var count = _store.ActiveUpdateCount;
        var variant = !_store.IsConnected || count == 0
            ? IconVariant.Normal
            : _store.NeedsAttention ? IconVariant.Attention : IconVariant.Updates;

        // The badge is painted onto the icon at runtime, so it is re-rendered only when the number or the variant moves.
        var badge = variant == IconVariant.Normal ? 0 : count;
        if (_currentVariant != variant || _currentBadge != badge)
        {
            _currentVariant = variant;
            _currentBadge = badge;
            var baseIcon = variant switch
            {
                IconVariant.Attention => _attention,
                IconVariant.Updates => _updates,
                _ => _normal,
            };
            if (badge > 0 && baseIcon is not null)
            {
                // The notification-icon library only converts ImageSources that come from a URI (a pack BitmapFrame or
                // a BitmapImage); a bitmap rendered at runtime made it throw "RenderTargetBitmap is not supported" on
                // the dispatcher every time the count changed. The badge therefore goes in as a GDI icon, and the
                // ImageSource is cleared first so that switching back to a plain icon is seen as a change.
                var previous = _badgeIcon;
                _badgeIcon = ToGdiIcon(WithBadge(baseIcon, badge, _iconSize));
                _icon.IconSource = null;
                _icon.Icon = _badgeIcon;
                previous?.Dispose();
            }
            else
            {
                _icon.IconSource = baseIcon;
                var previous = _badgeIcon;
                _badgeIcon = null;
                previous?.Dispose();
            }
            _log.LogDebug("Tray icon variant is now {Variant} with badge {Badge}", variant, badge);
        }

        // A running install is the most useful thing the tooltip can say; the counts come back when it is done.
        var tooltip = !_store.IsConnected
            ? Strings.TrayTooltipDisconnected
            : _store.CurrentInstall is { } installing
                ? Strings.TrayTooltipInstalling(installing.DisplayName)
                : _store.IsScanning
                    ? Strings.TrayTooltipChecking
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
        if (_agentUpdateItem is not null)
        {
            // Offer the update itself as soon as the service knows of one, so the item says what pressing it does.
            _agentUpdateItem.Header = _store.AgentUpdate is { UpdateAvailable: true, LatestVersion: { } version }
                ? Strings.TrayMenuUpdateAgent(version)
                : Strings.TrayMenuCheckAgentUpdate;
            (_agentUpdateItem.Command as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void UpdateAll()
    {
        var updates = _store.InstallableUpdates;
        if (updates.Count == 0) return;
        _log.LogInformation("User chose Update all from the tray menu: {Count} update(s)", updates.Count);
        var keys = updates.Select(u => u.Key).ToList();
        _store.BeginUpdateAll(keys);
        _ = _ipc.InstallAllAsync(keys);
    }

    /// <summary>
    /// Draws the number of pending updates onto the icon as a round badge in the lower right corner. The composite is
    /// rendered at exactly the notification area's pixel size (and at 96 dpi, so one drawing unit is one device pixel)
    /// to keep the glyph as crisp as the .ico frame it started from.
    /// </summary>
    private static BitmapSource WithBadge(ImageSource baseIcon, int count, int size)
    {
        var text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        var diameter = size * 0.62;
        var centre = new Point(size - diameter / 2.0, size - diameter / 2.0);
        // Three characters have to fit into the same circle as one, so the type shrinks with the digit count.
        var emSize = diameter * text.Length switch { 1 => 0.68, 2 => 0.56, _ => 0.42 };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseIcon, new Rect(0, 0, size, size));
            // A thin light ring separates the badge from whatever part of the glyph sits underneath it.
            dc.DrawEllipse(BadgeFill, new Pen(Brushes.White, Math.Max(1.0, size / 16.0)), centre, diameter / 2.0, diameter / 2.0);
            var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, BadgeTypeface, emSize, BadgeText, 1.0);
            dc.DrawText(label, new Point(centre.X - label.Width / 2.0, centre.Y - label.Height / 2.0));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Turns a rendered badge into a <see cref="System.Drawing.Icon"/> the notification area accepts: PNG-encoded,
    /// decoded by GDI+, then copied into an icon the caller owns (the HICON from <c>GetHicon</c> is destroyed here).
    /// </summary>
    private static System.Drawing.Icon ToGdiIcon(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var gdi = new System.Drawing.Bitmap(stream);
        var handle = gdi.GetHicon();
        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
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

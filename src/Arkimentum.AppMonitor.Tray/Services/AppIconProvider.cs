using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>What the tray knows about an application when it asks for its icon.</summary>
/// <param name="AppId">The cache key: one icon per application for the tray's lifetime.</param>
/// <param name="DisplayName">Matched against MSIX package names; also the monogram's source.</param>
/// <param name="IconPath">The scan's <see cref="PendingUpdate.IconPath"/> (raw <c>DisplayIcon</c> or an executable), if any.</param>
/// <param name="ProcessNames">The configured process names, looked up in App Paths.</param>
public sealed record AppIconRequest(string AppId, string DisplayName, string? IconPath, IReadOnlyList<string> ProcessNames)
{
    public static AppIconRequest For(PendingUpdate update) =>
        new(update.AppId, string.IsNullOrWhiteSpace(update.DisplayName) ? update.AppId : update.DisplayName, update.IconPath, update.ProcessNames);

    /// <summary>
    /// A history entry, completed from the tracked update of the same application when there is one: backfilled entries
    /// carry no icon path, and history has no process names at all.
    /// </summary>
    public static AppIconRequest For(InstallHistoryEntry entry, PendingUpdate? current) =>
        new(entry.AppId, string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.AppId : entry.DisplayName,
            entry.IconPath ?? current?.IconPath, current?.ProcessNames ?? []);

    /// <summary>The hints this request carries, as one comparable string.</summary>
    internal string Hints => $"{DisplayName}|{IconPath}|{string.Join(",", ProcessNames)}";
}

/// <summary>
/// The applications' own icons for the tray window and the toasts. The winget catalog carries no icons, so they come from
/// the device, first hit wins: the icon the scan found (the uninstall entry's <c>DisplayIcon</c> or the application's
/// executable), the configured process names in App Paths, then an MSIX package with the application's display name. When
/// none of them gives an icon the view shows a monogram instead. Every application is resolved once per tray lifetime,
/// off the UI thread, and the result - including "nothing found" - is cached; a later request with more hints (a tracked
/// update after a history row) tries again. Icons never break anything: every failure is logged at Debug and reads as
/// "no icon".
/// </summary>
public sealed class AppIconProvider
{
    /// <summary>The pixel size of the PNG written for toasts (their app logo shows at 48 logical pixels).</summary>
    private const int ToastLogoPixels = 64;

    private static readonly string[] BitmapExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    private readonly ILogger<AppIconProvider> _log;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Lazy<Task<IReadOnlyDictionary<string, string>>> _packageLogos;
    private readonly double _scale;

    public AppIconProvider(ILogger<AppIconProvider> log)
    {
        _log = log;
        _packageLogos = new(() => Task.Run(LoadPackageLogos), LazyThreadSafetyMode.ExecutionAndPublication);
        _scale = SystemScale();
    }

    /// <summary>%LOCALAPPDATA%\Arkimentum\AppMonitor\Icons: one PNG per application, for the toasts.</summary>
    public static string CacheDirectory { get; } = Path.Combine(AppInfo.LocalRoot, "Icons");

    /// <summary>
    /// The icon at <paramref name="size"/> logical pixels (rendered for the system DPI), or null when the device has none
    /// for the application. The same task is handed out for the same application and size, so callers may ask freely.
    /// </summary>
    public Task<ImageSource?> GetAsync(AppIconRequest request, int size)
    {
        try
        {
            var entry = EntryFor(request);
            return entry.Images.GetOrAdd(size, px => entry.Source.ContinueWith(
                t => t.Result is { } source ? Load(source, (int)Math.Round(px * _scale), request.AppId) : null,
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No icon for {App}", request.AppId);
            return Task.FromResult<ImageSource?>(null);
        }
    }

    /// <summary>
    /// Hands the icon to <paramref name="apply"/>: at once when it is already cached, otherwise on the caller's
    /// synchronization context (the UI thread for a view model) once it has been resolved. Only a found icon is delivered.
    /// </summary>
    public void Deliver(AppIconRequest request, int size, Action<ImageSource> apply)
    {
        var task = GetAsync(request, size);
        if (task.IsCompleted)
        {
            if (task.IsCompletedSuccessfully && task.Result is { } ready) apply(ready);
            return;
        }
        var scheduler = SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();
        task.ContinueWith(t =>
        {
            try
            {
                if (t.IsCompletedSuccessfully && t.Result is { } icon) apply(icon);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Showing the icon of {App} failed", request.AppId);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, scheduler);
    }

    /// <summary>Starts resolving an application's icon (and writing its toast PNG) ahead of need; returns at once.</summary>
    public void Prefetch(AppIconRequest request)
    {
        try { _ = EnsurePng(EntryFor(request), request.AppId); }
        catch (Exception ex) { _log.LogDebug(ex, "Could not prefetch the icon of {App}", request.AppId); }
    }

    /// <summary>
    /// The cached PNG of the application's icon for a toast's app logo, or null. A PNG from an earlier run is used at once;
    /// otherwise this waits up to <paramref name="wait"/> for the icon to be resolved. Never throws.
    /// </summary>
    public string? ToastLogoPath(AppIconRequest request, TimeSpan wait)
    {
        try
        {
            var path = PngPath(request.AppId);
            var png = EnsurePng(EntryFor(request), request.AppId);
            if (File.Exists(path)) return path;
            return png.Wait(wait) && png.Result is { } written && File.Exists(written) ? written : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No toast logo for {App}", request.AppId);
            return null;
        }
    }

    // ---------------------------------------------------------------- cache

    private sealed class Entry(string hints, Task<IconSource?> source)
    {
        public string Hints { get; } = hints;

        public Task<IconSource?> Source { get; } = source;

        public ConcurrentDictionary<int, Task<ImageSource?>> Images { get; } = new();

        public Task<string?>? Png { get; set; }
    }

    /// <summary>Where one application's icon is: a file with an icon index to extract, or a bitmap to decode.</summary>
    private sealed record IconSource(string Path, int Index, bool IsBitmap, string Origin);

    private Entry EntryFor(AppIconRequest request)
    {
        lock (_gate)
        {
            var hints = request.Hints;
            // A finished "nothing found" is retried only when this request knows something the earlier one did not.
            if (_entries.TryGetValue(request.AppId, out var existing) &&
                !(existing.Source.IsCompletedSuccessfully && existing.Source.Result is null && existing.Hints != hints))
                return existing;

            var entry = new Entry(hints, Task.Run(() => Resolve(request)));
            _entries[request.AppId] = entry;
            return entry;
        }
    }

    private Task<string?> EnsurePng(Entry entry, string appId)
    {
        lock (_gate)
        {
            return entry.Png ??= entry.Source.ContinueWith(t => t.Result is { } source ? WritePng(source, appId) : null,
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
    }

    private static string PngPath(string appId) => Path.Combine(CacheDirectory, AppIconLookup.SafeFileName(appId) + ".png");

    private string? WritePng(IconSource source, string appId)
    {
        try
        {
            if (Load(source, ToastLogoPixels, appId) is not BitmapSource bitmap) return null;
            Directory.CreateDirectory(CacheDirectory);
            var path = PngPath(appId);
            var temp = path + ".tmp";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(temp)) encoder.Save(stream);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not write the toast logo of {App}", appId);
            return null;
        }
    }

    // ---------------------------------------------------------------- resolution chain

    private IconSource? Resolve(AppIconRequest request)
    {
        try
        {
            var source = FromIconPath(request) ?? FromAppPaths(request) ?? FromPackage(request);
            if (source is null) _log.LogDebug("No icon found for {App}; the monogram is shown", request.AppId);
            else _log.LogDebug("Icon of {App} from {Origin}: {Path},{Index}", request.AppId, source.Origin, source.Path, source.Index);
            return source;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Resolving the icon of {App} failed", request.AppId);
            return null;
        }
    }

    /// <summary>1. What the scan matched: the uninstall entry's <c>DisplayIcon</c> or an executable found for it.</summary>
    private IconSource? FromIconPath(AppIconRequest request) =>
        Usable(AppIconSource.ParseDisplayIcon(request.IconPath), "scan", request.AppId);

    /// <summary>2. The configured process names in App Paths, the current user's registration first.</summary>
    private IconSource? FromAppPaths(AppIconRequest request)
    {
        foreach (var process in request.ProcessNames)
        {
            if (AppIconLookup.AppPathsKeyName(process) is not { } exe) continue;
            foreach (var (hive, view) in new[]
                     {
                         (RegistryHive.CurrentUser, RegistryView.Default),
                         (RegistryHive.LocalMachine, RegistryView.Registry64),
                         (RegistryHive.LocalMachine, RegistryView.Registry32),
                     })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}", false);
                    if (key?.GetValue(null) is not string value) continue;
                    // The default value is a plain path, possibly quoted; parsed like DisplayIcon, it gets index 0.
                    if (Usable(AppIconSource.ParseDisplayIcon(value), "App Paths", request.AppId) is { } source) return source;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Reading App Paths for {Exe} failed", exe);
                }
            }
        }
        return null;
    }

    /// <summary>3. An MSIX package of the current user whose display name is the application's.</summary>
    private IconSource? FromPackage(AppIconRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName)) return null;
        IReadOnlyDictionary<string, string> logos;
        try { logos = _packageLogos.Value.GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogDebug(ex, "Enumerating packages failed"); return null; }
        if (!logos.TryGetValue(request.DisplayName.Trim(), out var logo)) return null;

        try
        {
            var folder = Path.GetDirectoryName(logo);
            if (folder is null || !Directory.Exists(folder)) return null;
            var file = AppIconLookup.ResolveScaledAsset(Path.GetFileName(logo),
                Directory.EnumerateFiles(folder, Path.GetFileNameWithoutExtension(logo) + "*").Select(f => Path.GetFileName(f)),
                (int)Math.Round(32 * _scale));
            return file is null ? null : new IconSource(Path.Combine(folder, file), 0, true, "MSIX package");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Resolving the package logo {Logo} failed", logo);
            return null;
        }
    }

    /// <summary>A parsed location, if it exists, is not a generic system host and really yields an icon.</summary>
    private IconSource? Usable(IconLocation? location, string origin, string appId)
    {
        if (location is not { } l || AppIconLookup.IsGenericHost(l.Path)) return null;
        try
        {
            if (!File.Exists(l.Path)) return null;
            var isBitmap = BitmapExtensions.Contains(Path.GetExtension(l.Path), StringComparer.OrdinalIgnoreCase);
            var source = new IconSource(l.Path, l.Index, isBitmap, origin);
            // An executable without icon resources gives nothing; the next link of the chain gets its turn.
            return Load(source, 16, appId) is null ? null : source;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Icon location {Path} is not usable", l.Path);
            return null;
        }
    }

    /// <summary>Every current-user package with a logo, by display name (the first one wins for a duplicate name).</summary>
    private IReadOnlyDictionary<string, string> LoadPackageLogos()
    {
        var logos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    var name = package.DisplayName?.Trim();
                    var logo = package.Logo;
                    if (string.IsNullOrEmpty(name) || logo is null || !logo.IsFile) continue;
                    logos.TryAdd(name, logo.LocalPath);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skipping a package while collecting logos");
                }
            }
            _log.LogDebug("Collected {Count} package logo(s)", logos.Count);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not enumerate the user's packages");
        }
        return logos;
    }

    // ---------------------------------------------------------------- loading

    private ImageSource? Load(IconSource source, int pixels, string appId)
    {
        try
        {
            return source.IsBitmap ? LoadBitmap(source.Path, pixels) : ExtractIcon(source.Path, source.Index, pixels);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Loading the icon of {App} from {Path} failed", appId, source.Path);
            return null;
        }
    }

    private static BitmapSource LoadBitmap(string path, int pixels)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = pixels;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The icon at exactly <paramref name="pixels"/> from an .exe, .dll or .ico (index, or negative resource id), copied into
    /// a frozen bitmap so the handle can go at once. Null when the file has no icon: no generic stand-in.
    /// </summary>
    private static BitmapSource? ExtractIcon(string path, int index, int pixels)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        var count = PrivateExtractIconsW(path, index, pixels, pixels, handles, ids, 1, 0);
        var icon = count is > 0 and not uint.MaxValue ? handles[0] : IntPtr.Zero;
        if (icon == IntPtr.Zero && index < 0)
        {
            // Resource ids are documented for SHDefExtractIcon; take its large icon at the requested size.
            if (SHDefExtractIconW(path, index, 0, out var large, out var small, (uint)pixels) == 0)
            {
                icon = large;
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
        }
        if (icon == IntPtr.Zero) return null;

        try
        {
            var interop = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(interop, PixelFormats.Pbgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixelsBuffer = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixelsBuffer, stride, 0);
            var copy = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixelsBuffer, stride);
            copy.Freeze();
            return copy;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    /// <summary>The system DPI as a scale factor, so a 32-pixel slot gets a 48-pixel icon at 150 %.</summary>
    private static double SystemScale()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi >= 96 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        [Out] IntPtr[] phicon, [Out] uint[] piconid, uint nIcons, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge,
        out IntPtr phiconSmall, uint nIconSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}

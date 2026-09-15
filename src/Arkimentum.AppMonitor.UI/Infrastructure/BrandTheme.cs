using System.Windows;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.UI;

/// <summary>
/// Keeps exactly one brand palette (<c>Themes/Brand.Light.xaml</c> or <c>Themes/Brand.Dark.xaml</c>) merged into
/// <see cref="Application.Resources"/>, matching the effective theme.
///
/// WPF's Fluent theme lives in <c>Application.Resources.MergedDictionaries[0]</c>; the brand dictionary is always
/// appended last, so its keys — both the <c>Brand*</c> tokens and the Fluent overrides such as
/// <c>AccentFillColorDefaultBrush</c> — win the lookup. Every consumer uses <c>DynamicResource</c>, so swapping the
/// dictionary repaints the whole UI without recreating a window.
///
/// The effective theme is <see cref="Application.ThemeMode"/> when it is explicitly Light or Dark; under
/// <c>System</c> (the shipping setting) it is HKCU\…\Themes\Personalize\AppsUseLightTheme. A system flip raises
/// <see cref="SystemEvents.UserPreferenceChanged"/>, which is also what WPF listens to, so the swap is queued at
/// Background priority — by then WPF has already exchanged its own Light/Dark dictionary.
///
/// Host apps: merge <c>Themes/Styles.xaml</c> (<see cref="BrandAssets.StylesUri"/>) in App.xaml and call
/// <see cref="Initialize"/> from <c>OnStartup</c>. <see cref="Initialize"/> merges Styles.xaml itself when the host
/// has not, so a host that forgets still gets a working theme — but merging it in App.xaml keeps the styles
/// available to anything created before startup finishes (and to the XAML designer).
/// </summary>
public static class BrandTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly Uri LightUri = new(BrandAssets.BrandLightUri, UriKind.Absolute);

    private static readonly Uri DarkUri = new(BrandAssets.BrandDarkUri, UriKind.Absolute);

    private static readonly Uri StylesUri = new(BrandAssets.StylesUri, UriKind.Absolute);

    private static ResourceDictionary? _applied;
    private static bool? _appliedIsDark;
    private static bool _hooked;

    /// <summary>True when the effective theme is dark.</summary>
    public static bool IsDark => Application.Current?.ThemeMode switch
    {
        { } mode when mode == ThemeMode.Dark => true,
        { } mode when mode == ThemeMode.Light => false,
        _ => !AppsUseLightTheme(),
    };

    /// <summary>Raised after the palette has been swapped, so anything that caches a colour can refresh.</summary>
    public static event Action? Changed;

    /// <summary>Applies the palette and starts following the system theme. Safe to call more than once.</summary>
    public static void Initialize()
    {
        EnsureStyles();
        Apply();
        if (_hooked) return;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _hooked = true;
    }

    /// <summary>Re-reads the effective theme and swaps the merged dictionary when it has actually changed.</summary>
    public static void Apply()
    {
        var app = Application.Current;
        if (app is null) return;

        var dark = IsDark;
        if (_applied is not null && _appliedIsDark == dark)
        {
            EnsureLast(app);
            return;
        }

        var next = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        if (_applied is not null) app.Resources.MergedDictionaries.Remove(_applied);
        app.Resources.MergedDictionaries.Add(next);
        _applied = next;
        _appliedIsDark = dark;
        Changed?.Invoke();
    }

    /// <summary>
    /// Merges <c>Themes/Styles.xaml</c> when the host app's App.xaml has not already done so, so a host that only
    /// calls <see cref="Initialize"/> still resolves WordmarkText, Card, BodyText and the rest.
    /// </summary>
    private static void EnsureStyles()
    {
        var app = Application.Current;
        if (app is null) return;
        if (ContainsStyles(app.Resources)) return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri });
    }

    private static bool ContainsStyles(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            // WPF may normalise the pack URI's casing, so compare the text rather than the Uri instances.
            if (merged.Source is { } source &&
                string.Equals(source.ToString(), BrandAssets.StylesUri, StringComparison.OrdinalIgnoreCase))
                return true;
            if (ContainsStyles(merged)) return true;
        }
        return false;
    }

    /// <summary>The brand dictionary must stay last or the Fluent palette would shadow the overrides.</summary>
    private static void EnsureLast(Application app)
    {
        var merged = app.Resources.MergedDictionaries;
        if (_applied is null || merged.Count == 0 || ReferenceEquals(merged[^1], _applied)) return;
        merged.Remove(_applied);
        merged.Add(_applied);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)) return;
        // Queue behind WPF's own theme swap so the brand dictionary ends up last again.
        Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, new Action(Apply));
    }

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}

namespace Arkimentum.AppMonitor.UI;

/// <summary>
/// The pack URIs and literals the brand ships, so host apps never have to spell the assembly name out.
/// Every value is a <c>const</c> so it can be used from XAML with <c>{x:Static ui:BrandAssets.…}</c>.
/// </summary>
public static class BrandAssets
{
    /// <summary>The wordmark text. Always "Arkimentum" — the product word goes on the line beneath it.</summary>
    public const string BrandName = "Arkimentum";

    /// <summary>The Arkimentum app icon, embedded as a WPF Resource. Usable as Window.Icon or Image.Source.</summary>
    public const string AppIconUri = "pack://application:,,,/Arkimentum.AppMonitor.UI;component/Assets/app.ico";

    /// <summary>The embedded Gelasio family (SIL OFL 1.1); the brand serif used by the wordmark and headings.</summary>
    public const string GelasioFontFamilyUri = "pack://application:,,,/Arkimentum.AppMonitor.UI;component/Fonts/#Gelasio";

    /// <summary>The style dictionary a host app must merge in App.xaml (or let <see cref="BrandTheme"/> merge).</summary>
    public const string StylesUri = "pack://application:,,,/Arkimentum.AppMonitor.UI;component/Themes/Styles.xaml";

    /// <summary>The light palette. Managed by <see cref="BrandTheme"/>; host apps should not merge it themselves.</summary>
    public const string BrandLightUri = "pack://application:,,,/Arkimentum.AppMonitor.UI;component/Themes/Brand.Light.xaml";

    /// <summary>The dark palette. Managed by <see cref="BrandTheme"/>; host apps should not merge it themselves.</summary>
    public const string BrandDarkUri = "pack://application:,,,/Arkimentum.AppMonitor.UI;component/Themes/Brand.Dark.xaml";
}

using System.Windows;
using System.Windows.Controls;

namespace Arkimentum.AppMonitor.UI;

/// <summary>
/// The Arkimentum wordmark block, identical in every app: the two brand bars (terracotta then sky), the
/// "Arkimentum" wordmark in Gelasio olive, and the product word beneath it in letter-spaced terracotta caps.
///
/// <code>
/// &lt;ui:BrandHeader ProductName="APPMONITOR ADMIN" Subtitle="{Binding StatusLine}" /&gt;
/// </code>
///
/// The defaults reproduce the tray main window's header exactly. Set <see cref="ShowBrandRule"/> to false and
/// <see cref="WordmarkFontSize"/> to 26 for the compact form used beside the app icon in the About dialog.
/// </summary>
public partial class BrandHeader : UserControl
{
    /// <summary>The wordmark text. Defaults to <see cref="BrandAssets.BrandName"/>; there is no reason to change it.</summary>
    public static readonly DependencyProperty BrandNameProperty = DependencyProperty.Register(
        nameof(BrandName), typeof(string), typeof(BrandHeader), new PropertyMetadata(BrandAssets.BrandName));

    /// <summary>The product word under the wordmark, e.g. "APPMONITOR" or "APPMONITOR ADMIN". Caps, un-spaced.</summary>
    public static readonly DependencyProperty ProductNameProperty = DependencyProperty.Register(
        nameof(ProductName), typeof(string), typeof(BrandHeader), new PropertyMetadata(string.Empty));

    /// <summary>Optional caption under the product word; hidden when null or blank.</summary>
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(BrandHeader), new PropertyMetadata(string.Empty));

    /// <summary>Margin of the subtitle line. The default matches the tray main window's status line.</summary>
    public static readonly DependencyProperty SubtitleMarginProperty = DependencyProperty.Register(
        nameof(SubtitleMargin), typeof(Thickness), typeof(BrandHeader),
        new PropertyMetadata(new Thickness(0, 12, 8, 0)));

    /// <summary>Whether the two brand bars are drawn above the wordmark. False beside the app icon, which carries them.</summary>
    public static readonly DependencyProperty ShowBrandRuleProperty = DependencyProperty.Register(
        nameof(ShowBrandRule), typeof(bool), typeof(BrandHeader), new PropertyMetadata(true));

    /// <summary>Width of each of the two brand bars.</summary>
    public static readonly DependencyProperty BrandRuleWidthProperty = DependencyProperty.Register(
        nameof(BrandRuleWidth), typeof(double), typeof(BrandHeader), new PropertyMetadata(28d));

    /// <summary>Size of the wordmark. 30 in a window header, 26 beside a 56 px app icon.</summary>
    public static readonly DependencyProperty WordmarkFontSizeProperty = DependencyProperty.Register(
        nameof(WordmarkFontSize), typeof(double), typeof(BrandHeader), new PropertyMetadata(30d));

    public BrandHeader() => InitializeComponent();

    public string BrandName
    {
        get => (string)GetValue(BrandNameProperty);
        set => SetValue(BrandNameProperty, value);
    }

    public string ProductName
    {
        get => (string)GetValue(ProductNameProperty);
        set => SetValue(ProductNameProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Thickness SubtitleMargin
    {
        get => (Thickness)GetValue(SubtitleMarginProperty);
        set => SetValue(SubtitleMarginProperty, value);
    }

    public bool ShowBrandRule
    {
        get => (bool)GetValue(ShowBrandRuleProperty);
        set => SetValue(ShowBrandRuleProperty, value);
    }

    public double BrandRuleWidth
    {
        get => (double)GetValue(BrandRuleWidthProperty);
        set => SetValue(BrandRuleWidthProperty, value);
    }

    public double WordmarkFontSize
    {
        get => (double)GetValue(WordmarkFontSizeProperty);
        set => SetValue(WordmarkFontSizeProperty, value);
    }
}

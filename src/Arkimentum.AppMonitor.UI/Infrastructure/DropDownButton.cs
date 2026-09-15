using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Arkimentum.AppMonitor.UI;

/// <summary>
/// Turns an ordinary button into a drop-down button: clicking it opens its own ContextMenu underneath it,
/// so the "Defer" choices get a real Fluent fly-out without a custom control.
/// </summary>
public static class DropDownButton
{
    public static readonly DependencyProperty OpensMenuProperty = DependencyProperty.RegisterAttached(
        "OpensMenu", typeof(bool), typeof(DropDownButton), new PropertyMetadata(false, OnOpensMenuChanged));

    public static void SetOpensMenu(DependencyObject element, bool value) => element.SetValue(OpensMenuProperty, value);

    public static bool GetOpensMenu(DependencyObject element) => (bool)element.GetValue(OpensMenuProperty);

    private static void OnOpensMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnClick;
        if (e.NewValue is true) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button || button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
    }
}

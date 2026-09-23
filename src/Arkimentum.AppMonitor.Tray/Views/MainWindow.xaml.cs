using System.Windows;
using System.Windows.Threading;

namespace Arkimentum.AppMonitor.Tray.Views;

/// <summary>The main window. Everything it shows comes from <see cref="ViewModels.MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Runs after every layout pass, so a banner appearing or the window being resized is picked up; it only sets
        // MaxHeight when the value moved, which keeps it from triggering further passes.
        LayoutRoot.LayoutUpdated += (_, _) => FitLowerSection();
    }

    /// <summary>
    /// "Recent updates" and "Details" share one scrolling panel below the update list. An Auto row would take their
    /// full height and push Details past the bottom of the window once both are open, so the panel is capped at
    /// everything the header, the banners and the update list's minimum height leave over, and scrolls inside that.
    /// </summary>
    private void FitLowerSection()
    {
        var rows = LayoutRoot.RowDefinitions;
        var max = Math.Max(0, LayoutRoot.ActualHeight - rows[0].ActualHeight - rows[1].ActualHeight - rows[2].MinHeight);
        if (Math.Abs(LowerSection.MaxHeight - max) > 0.5) LowerSection.MaxHeight = max;
    }

    /// <summary>An opened section is scrolled into view, so opening Details under an open "Recent updates" shows it.</summary>
    private void OnSectionExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement section)
            Dispatcher.BeginInvoke(() => section.BringIntoView(), DispatcherPriority.Loaded);
    }
}

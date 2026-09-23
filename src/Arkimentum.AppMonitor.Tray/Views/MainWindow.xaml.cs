using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Arkimentum.AppMonitor.Tray.Views;

/// <summary>The main window. Everything it shows comes from <see cref="ViewModels.MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    /// <summary>The update list never gets less than this while it shows update cards (they scroll inside it).</summary>
    private const double UpdateListMinHeight = 120;

    public MainWindow()
    {
        InitializeComponent();
        // The default height leaves "Recent updates" and Details some room each; on a small or highly scaled screen it
        // is capped so the window never starts taller than the work area.
        var workArea = SystemParameters.WorkArea.Height;
        if (workArea > 0)
        {
            MinHeight = Math.Min(MinHeight, workArea * 0.9);
            Height = Math.Min(Height, workArea * 0.9);
        }
        // Runs after every layout pass, so a banner appearing, a section opening or the window being resized is picked
        // up. It only writes a value when it moved, and every input it reads is independent of what it writes, so it
        // settles after one pass instead of looping.
        LayoutRoot.LayoutUpdated += (_, _) => FitSections();
    }

    /// <summary>
    /// Shares the window height between the update list and the two collapsible sections below it, so nothing is cut
    /// off by the bottom edge:
    /// <list type="bullet">
    ///   <item>The update list keeps what it needs: all of the "You're up to date" panel when that shows, otherwise
    ///   <see cref="UpdateListMinHeight"/> (the cards scroll).</item>
    ///   <item>"Recent updates" and Details each scroll on their own. One open section gets all the height that is left;
    ///   two open sections share it, and one that needs less than half gives the rest to the other.</item>
    /// </list>
    /// </summary>
    private void FitSections()
    {
        // The budget is the window's client area, not the grid: when its rows need more than the window has, the grid
        // grows past the bottom edge (and is clipped there), so its own height says nothing about the space available.
        if (VisualTreeHelper.GetParent(LayoutRoot) is not FrameworkElement host) return;
        var available = host.ActualHeight - LayoutRoot.Margin.Top - LayoutRoot.Margin.Bottom;
        if (available <= 0) return;

        var rows = LayoutRoot.RowDefinitions;
        var recentOpen = RecentExpander.IsExpanded;
        var detailsOpen = DetailsExpander.IsExpanded;
        var chrome = Chrome(rows[3], RecentScroll, recentOpen) + Chrome(rows[4], DetailsScroll, detailsOpen);
        var top = rows[0].ActualHeight + rows[1].ActualHeight;

        // "You're up to date" keeps its full height, unless the window is too small even for the section headers.
        var listMin = UpdateListMinHeight;
        if (EmptyStatePanel.IsVisible)
        {
            var needs = EmptyStateNaturalHeight();
            if (top + needs + chrome <= available) listMin = Math.Max(listMin, needs);
        }
        if (Math.Abs(rows[2].MinHeight - listMin) > 0.5) rows[2].MinHeight = listMin;

        var room = Math.Max(0, available - top - listMin - chrome);

        var recentMax = double.PositiveInfinity;
        var detailsMax = double.PositiveInfinity;
        if (recentOpen && detailsOpen)
        {
            // +1: a rounding difference must not produce a scrollbar on content that fits.
            var recentNeeds = RecentScroll.ExtentHeight + 1;
            var detailsNeeds = DetailsScroll.ExtentHeight + 1;
            var half = room / 2;
            if (recentNeeds + detailsNeeds <= room) { /* both fit as they are */ }
            else if (recentNeeds < half) { recentMax = recentNeeds; detailsMax = room - recentNeeds; }
            else if (detailsNeeds < half) { detailsMax = detailsNeeds; recentMax = room - detailsNeeds; }
            else { recentMax = half; detailsMax = half; }
        }
        else if (recentOpen) recentMax = room;
        else if (detailsOpen) detailsMax = room;

        SetMaxHeight(RecentScroll, recentMax);
        SetMaxHeight(DetailsScroll, detailsMax);
    }

    private double _emptyStateHeight;
    private double _emptyStateWidth = -1;

    /// <summary>
    /// The height the "You're up to date" panel needs. Its DesiredSize is capped at what its row already gives it, so it
    /// is measured once without a height limit, again only when the width changes (the text may wrap differently).
    /// </summary>
    private double EmptyStateNaturalHeight()
    {
        var width = LayoutRoot.ActualWidth;
        if (Math.Abs(width - _emptyStateWidth) > 0.5)
        {
            EmptyStatePanel.Measure(new Size(width, double.PositiveInfinity));
            _emptyStateHeight = EmptyStatePanel.DesiredSize.Height;
            _emptyStateWidth = width;
        }
        return _emptyStateHeight;
    }

    /// <summary>The part of a section's row that is not its scrolling content: header and margins.</summary>
    private static double Chrome(RowDefinition row, ScrollViewer content, bool open) =>
        open ? Math.Max(0, row.ActualHeight - content.ActualHeight) : row.ActualHeight;

    private static void SetMaxHeight(ScrollViewer viewer, double max)
    {
        var changed = double.IsPositiveInfinity(max)
            ? !double.IsPositiveInfinity(viewer.MaxHeight)
            : Math.Abs(viewer.MaxHeight - max) > 0.5;
        if (changed) viewer.MaxHeight = max;
    }
}

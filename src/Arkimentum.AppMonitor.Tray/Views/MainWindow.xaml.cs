using System.Windows;

namespace Arkimentum.AppMonitor.Tray.Views;

/// <summary>The main window. Everything it shows comes from <see cref="ViewModels.MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // On a small or highly scaled screen the window never starts taller than the work area. Each tab scrolls on its
        // own, so any height works.
        var workArea = SystemParameters.WorkArea.Height;
        if (workArea > 0)
        {
            MinHeight = Math.Min(MinHeight, workArea * 0.9);
            Height = Math.Min(Height, workArea * 0.9);
        }
    }
}

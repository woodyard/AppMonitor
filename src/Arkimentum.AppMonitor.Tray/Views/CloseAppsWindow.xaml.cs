using System.Windows;
using Arkimentum.AppMonitor.Tray.ViewModels;

namespace Arkimentum.AppMonitor.Tray.Views;

/// <summary>The "Close apps to update X" dialog. One per update key; the coordinator owns its lifetime.</summary>
public partial class CloseAppsWindow : Window
{
    public CloseAppsWindow(CloseAppsViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.CloseRequested -= Close;
    }

    public CloseAppsViewModel ViewModel { get; }
}

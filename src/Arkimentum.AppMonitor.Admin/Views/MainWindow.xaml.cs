using System.ComponentModel;
using System.Windows;
using Arkimentum.AppMonitor.Admin.ViewModels;

namespace Arkimentum.AppMonitor.Admin.Views;

/// <summary>The shell window. Its only job beyond wiring is to let the view model veto a close with unsaved changes.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.ConfirmClose()) e.Cancel = true;
    }
}

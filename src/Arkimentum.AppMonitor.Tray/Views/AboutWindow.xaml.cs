using System.Windows;

namespace Arkimentum.AppMonitor.Tray.Views;

/// <summary>The About dialog.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}

using System.Windows;

namespace Arkimentum.AppMonitor.Admin.Views;

/// <summary>Modal window; its view model closes it through DialogViewModel.RequestClose.</summary>
public partial class TextInputWindow : Window
{
    public TextInputWindow() => InitializeComponent();
}

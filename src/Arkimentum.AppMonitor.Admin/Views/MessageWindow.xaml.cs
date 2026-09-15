using System.Windows;

namespace Arkimentum.AppMonitor.Admin.Views;

/// <summary>Modal window; its view model closes it through DialogViewModel.RequestClose.</summary>
public partial class MessageWindow : Window
{
    public MessageWindow() => InitializeComponent();
}

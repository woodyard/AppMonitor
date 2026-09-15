using System.Windows.Input;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.Services;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>The About dialog.</summary>
public sealed class AboutViewModel : ObservableObject
{
    private readonly AgentStateStore _store;

    public AboutViewModel(AgentStateStore store, IWindowService windows, AgentUpdateViewModel agentUpdate)
    {
        _store = store;
        AgentUpdate = agentUpdate;
        OpenLogFolderCommand = new RelayCommand(() => windows.OpenFolder(LogDirectory));
    }

    /// <summary>The agent's own update state and actions - the same instance the main window's footer shows.</summary>
    public AgentUpdateViewModel AgentUpdate { get; }

    public string Title => Strings.AboutTitle;

    public string ProductName => Strings.ProductName;

    public string VersionText => Strings.AboutVersion(AppInfo.Version);

    public string Description => Strings.AboutDescription;

    public string Copyright => Strings.Copyright;

    public string ServiceVersion =>
        string.IsNullOrWhiteSpace(_store.ServiceVersion) ? Strings.DetailsNone : _store.ServiceVersion!;

    public string LogDirectory =>
        string.IsNullOrWhiteSpace(_store.Settings.LogDirectory) ? AppInfo.LogDirectory : _store.Settings.LogDirectory;

    public ICommand OpenLogFolderCommand { get; }
}

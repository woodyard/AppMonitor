using System.Collections.Generic;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>The About dialog: version, copyright, the command-line switches and a link to the log folder.</summary>
public sealed class AboutViewModel : DialogViewModel
{
    private readonly IDialogService _dialogs;

    public AboutViewModel(IDialogService dialogs, CommandLineOptions options, SettingsStoreService store, CatalogService catalog)
    {
        _dialogs = dialogs;
        LogDirectory = AdminAppInfo.LogDirectory(options.UserConfig);
        ConfigurationKey = store.PreferencePath;
        CatalogPath = catalog.Path ?? Strings.None;
        OpenLogFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(LogDirectory));
        CloseCommand = new RelayCommand(() => Close(true));
    }

    public string Title => Strings.AboutTitle;

    public string VersionText => Strings.AboutVersion(AdminAppInfo.Version);

    public string Copyright => Strings.Copyright;

    public string Description => Strings.AboutDescription;

    public string CommandLineHeader => Strings.AboutCommandLine;

    public IReadOnlyList<string> Switches { get; } =
    [
        Strings.AboutSwitchNone,
        Strings.AboutSwitchExport,
        Strings.AboutSwitchImport,
        Strings.AboutSwitchUserConfig,
    ];

    public string LogDirectory { get; }

    public string ConfigurationKey { get; }

    public string CatalogPath { get; }

    public string CloseText => Strings.ButtonClose;

    public string OpenText => Strings.AboutOpen;

    public ICommand OpenLogFolderCommand { get; }

    public ICommand CloseCommand { get; }
}

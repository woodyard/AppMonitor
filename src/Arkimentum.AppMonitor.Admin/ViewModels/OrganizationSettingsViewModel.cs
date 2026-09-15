using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// The Organization settings page. Exactly the Settings page, one document over: the rows come from the same
/// <see cref="Configuration.SettingsSchema.Global"/> through the same <see cref="ConfigurationEditor"/>, only the
/// editor behind it is the one bound to the organization document, so every overridden row badges "Organization"
/// and nothing is ever locked by policy.
/// </summary>
public sealed class OrganizationSettingsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public OrganizationSettingsViewModel(OrganizationConfigViewModel configuration, IDialogService dialogs)
    {
        Configuration = configuration;
        _dialogs = dialogs;
        BrowseCommand = new RelayCommand<SettingRowViewModel>(Browse, row => row is { IsEditorEnabled: true });
    }

    public OrganizationConfigViewModel Configuration { get; }

    public ConfigurationEditor Editor => Configuration.Editor;

    public string Title => Strings.OrganizationSettingsTitle;

    public string Subtitle => Strings.OrganizationSettingsSubtitle;

    public string ShowAdvancedText => Strings.ShowAdvanced;

    public bool ShowAdvanced
    {
        get => Editor.ShowAdvanced;
        set
        {
            if (Editor.ShowAdvanced == value) return;
            Editor.ShowAdvanced = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The generated Path rows look this command up on the page's own data context, so the organization pages
    /// each carry one exactly as the local pages do.
    /// </summary>
    public ICommand BrowseCommand { get; }

    private void Browse(SettingRowViewModel? row)
    {
        if (row is null) return;
        var picked = _dialogs.PickPath(row.TextValue, row.IsFolderPath);
        if (picked is not null) row.TextValue = picked;
    }
}

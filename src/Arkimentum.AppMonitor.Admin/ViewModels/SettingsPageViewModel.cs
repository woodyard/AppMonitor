using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// The Settings page. It owns no state of its own: every row comes from <see cref="ConfigurationEditor"/>, which is
/// generated from <see cref="Configuration.SettingsSchema.Global"/> and shared with the Applications page.
/// </summary>
public sealed class SettingsPageViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public SettingsPageViewModel(ConfigurationEditor editor, IDialogService dialogs)
    {
        Editor = editor;
        _dialogs = dialogs;
        BrowseCommand = new RelayCommand<SettingRowViewModel>(Browse, row => row is { IsEditorEnabled: true });
    }

    public ConfigurationEditor Editor { get; }

    public string Title => Strings.SettingsTitle;

    public string Subtitle => Strings.SettingsSubtitle;

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

    /// <summary>Shared by every generated Path row, on both pages.</summary>
    public ICommand BrowseCommand { get; }

    private void Browse(SettingRowViewModel? row)
    {
        if (row is null) return;
        var picked = _dialogs.PickPath(row.TextValue, row.IsFolderPath);
        if (picked is not null) row.TextValue = picked;
    }
}

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One category of generated setting rows, as rendered by the Settings and Applications pages.</summary>
public sealed class SettingGroupViewModel : ObservableObject
{
    public SettingGroupViewModel(string title, IReadOnlyList<SettingRowViewModel> rows)
    {
        Title = title;
        Rows = rows;
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    public string Title { get; }

    public IReadOnlyList<SettingRowViewModel> Rows { get; }

    /// <summary>Hidden when every row in it is hidden (advanced-only groups with "Show advanced" off).</summary>
    public bool IsShown => Rows.Any(r => r.IsShown);

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingRowViewModel.IsShown)) OnPropertyChanged(nameof(IsShown));
    }
}

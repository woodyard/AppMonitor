using System.Windows.Input;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>One entry of a "Defer" drop-down, e.g. "4 hours".</summary>
public sealed class DeferOptionViewModel
{
    public DeferOptionViewModel(int minutes, Action<int> execute)
    {
        Minutes = minutes;
        Label = TimeFormat.Duration(minutes);
        Command = new RelayCommand(() => execute(minutes));
    }

    public int Minutes { get; }

    public string Label { get; }

    public ICommand Command { get; }
}

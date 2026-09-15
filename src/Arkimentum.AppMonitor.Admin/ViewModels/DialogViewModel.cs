using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// Base for the view models behind the console's modal windows: the window subscribes to <see cref="RequestClose"/>
/// and sets its DialogResult, so no code-behind ever decides anything.
/// </summary>
public abstract class DialogViewModel : ObservableObject
{
    public event Action<bool>? RequestClose;

    protected void Close(bool accepted) => RequestClose?.Invoke(accepted);
}

using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>
/// Shows the "Close apps to update X" prompt (a toast) for an update the tray already holds. Implemented by
/// <see cref="Services.CloseAppsCoordinator"/> and injected into <see cref="MainViewModel"/>, which is how the
/// update card can bring the prompt back without the main view model and the coordinator depending on each other.
/// </summary>
public interface ICloseAppsLauncher
{
    void ShowFor(PendingUpdate update);
}

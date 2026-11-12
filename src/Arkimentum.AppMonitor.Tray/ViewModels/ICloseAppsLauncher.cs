namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>
/// Carries out "Close apps and update" for an update the tray already holds, exactly as the close-apps prompt's own
/// button does. Implemented by <see cref="Services.CloseAppsCoordinator"/> and injected into <see cref="MainViewModel"/>,
/// which is how the update card's button reaches the coordinator without the two depending on each other.
/// </summary>
public interface ICloseAppsLauncher
{
    /// <summary>
    /// Closes the update's blocking applications and asks the service to install it, without asking the user again:
    /// an open prompt for it is taken down as answered, and with none open the apps are closed all the same. Returns
    /// false when there was nothing to act on (the update is gone, installing, installed or deferred meanwhile).
    /// </summary>
    bool CloseAndUpdate(string updateKey);
}

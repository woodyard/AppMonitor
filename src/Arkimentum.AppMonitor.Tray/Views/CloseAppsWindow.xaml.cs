using System.ComponentModel;
using System.Windows;
using Arkimentum.AppMonitor.Tray.ViewModels;

namespace Arkimentum.AppMonitor.Tray.Views;

/// <summary>The "Close apps to update X" dialog. One per update key; the coordinator owns its lifetime.</summary>
public partial class CloseAppsWindow : Window
{
    /// <summary>
    /// True once something other than the user's X is closing this window: a button that has already answered, or the
    /// coordinator pruning a dialog whose update moved on. It is what keeps the X from sending a second answer.
    /// </summary>
    private bool _closedByApp;

    /// <summary>
    /// True once the window is on its way out. WPF refuses <see cref="Window.Close"/> while a close is in flight
    /// (it throws "Cannot ... while window is closing"), and the X path does call back into the view model, whose
    /// "Not now" ends with <see cref="CloseAppsViewModel.RequestClose"/> - so that second close has to be a no-op.
    /// </summary>
    private bool _closing;

    public CloseAppsWindow(CloseAppsViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += CloseFromApp;
        Closed += (_, _) => viewModel.CloseRequested -= CloseFromApp;
    }

    public CloseAppsViewModel ViewModel { get; }

    /// <summary>
    /// Closes the dialog without treating it as an answer from the user. Used by the view model's
    /// <see cref="CloseAppsViewModel.RequestClose"/> and by the coordinator when it prunes or shuts down.
    /// </summary>
    public void CloseFromApp()
    {
        _closedByApp = true;
        if (_closing) return;
        Close();
    }

    /// <summary>
    /// The X is an answer: the user does not want this now. Saying so ("Not now") is what lets the service take the
    /// update out of WaitingForClose; without it the card sat on "Waiting for you to close: pwsh" until the next
    /// notification interval - in Quiet mode, hours. The view model itself refuses to answer twice, so a button that
    /// closed the window through <see cref="CloseFromApp"/> is never followed by a dismissal.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        // Set before the callback: "Not now" ends with RequestClose, which must not try to close us again.
        _closing = true;
        if (_closedByApp) return;
        _closedByApp = true;
        ViewModel.UserClosedWindow();
    }
}

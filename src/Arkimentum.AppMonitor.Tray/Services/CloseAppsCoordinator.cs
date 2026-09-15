using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.ViewModels;
using Arkimentum.AppMonitor.Tray.Views;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Owns the "Close apps to update X" dialogs — at most one per update key — and performs the session-bound
/// process closing the service asks for.
/// </summary>
public sealed class CloseAppsCoordinator : IHostedService, ICloseAppsActions
{
    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(30);

    private readonly ILogger<CloseAppsCoordinator> _log;
    private readonly IpcClientService _ipc;
    private readonly AgentStateStore _store;
    private readonly IWindowService _windows;
    private readonly Dictionary<string, CloseAppsWindow> _dialogs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    public CloseAppsCoordinator(
        ILogger<CloseAppsCoordinator> log,
        IpcClientService ipc,
        AgentStateStore store,
        IWindowService windows)
    {
        _log = log;
        _ipc = ipc;
        _store = store;
        _windows = windows;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived += OnMessage;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived -= OnMessage;
        _cts.Cancel();
        foreach (var window in _dialogs.Values.ToList()) window.Close();
        _dialogs.Clear();
        _cts.Dispose();
        return Task.CompletedTask;
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case PromptCloseMessage prompt:
                ShowFor(prompt.Update);
                break;
            case CloseProcessesMessage close:
                _ = CloseProcessesAsync(close);
                break;
            case StateMessage:
                PruneFinishedDialogs();
                break;
        }
    }

    /// <summary>Opens the dialog for an update, or refreshes the one already open for it.</summary>
    public void ShowFor(PendingUpdate update)
    {
        if (_dialogs.TryGetValue(update.Key, out var existing))
        {
            _log.LogInformation("Refreshing the close-apps dialog for {App}", update.DisplayName);
            existing.ViewModel.Apply(update);
            existing.Activate();
            return;
        }

        _log.LogInformation("Showing the close-apps dialog for {App} (blocking: {Processes})",
            update.DisplayName, string.Join(", ", update.BlockingProcesses));

        var viewModel = new CloseAppsViewModel(update, this);
        // Deliberately not owned by the main window: WPF hides owned windows with their owner, and this dialog
        // must survive the user closing the main window while a forced close is counting down.
        var window = new CloseAppsWindow(viewModel);
        window.Closed += (_, _) =>
        {
            _dialogs.Remove(update.Key);
            viewModel.Dispose();
        };
        _dialogs[update.Key] = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Closes dialogs whose update has moved on (installed, or gone from the service's snapshot).</summary>
    private void PruneFinishedDialogs()
    {
        foreach (var key in _dialogs.Keys.ToList())
        {
            var update = _store.Find(key);
            if (update is null or { State: UpdateState.Installing or UpdateState.Installed })
            {
                _log.LogInformation("Closing the close-apps dialog for {Key}: the update moved on", key);
                _dialogs[key].Close();
            }
            else
            {
                _dialogs[key].ViewModel.Apply(update);
            }
        }
    }

    // ---------------------------------------------------------------- service-driven closing

    private async Task CloseProcessesAsync(CloseProcessesMessage message)
    {
        var wait = TimeSpan.FromSeconds(Math.Max(1, message.GracefulWaitSeconds));
        // A dialog is open for this update: the user is being asked nicely, so never kill from here.
        var force = message.Force && !_dialogs.ContainsKey(message.UpdateKey);
        if (message.Force && !force)
            _log.LogInformation("Downgrading a forced close of {Key} to graceful: the user has the dialog open", message.UpdateKey);

        _log.LogInformation("Closing {Processes} in session {Session} (force={Force}, wait={Wait}s)",
            string.Join(", ", message.ProcessNames), AppInfo.SessionId, force, wait.TotalSeconds);

        if (_dialogs.TryGetValue(message.UpdateKey, out var dialog))
        {
            dialog.ViewModel.IsBusy = true;
            dialog.ViewModel.StatusMessage = Strings.CloseAppsClosing;
        }

        IReadOnlyList<string> stillRunning;
        try
        {
            stillRunning = await Task.Run(
                () => ProcessHelper.CloseAsync(message.ProcessNames, AppInfo.SessionId, wait, force, _cts.Token),
                _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Closing processes for {Key} failed", message.UpdateKey);
            stillRunning = ProcessHelper.GetRunning(message.ProcessNames, AppInfo.SessionId);
        }

        _log.LogInformation("Close attempt for {Key} finished; still running: {StillRunning}",
            message.UpdateKey, stillRunning.Count == 0 ? "none" : string.Join(", ", stillRunning));

        if (_dialogs.TryGetValue(message.UpdateKey, out dialog))
        {
            dialog.ViewModel.IsBusy = false;
            dialog.ViewModel.StatusMessage = stillRunning.Count == 0
                ? Strings.CloseAppsAllClosed
                : Strings.CloseAppsStillRunning(Friendly(stillRunning));
            if (stillRunning.Count > 0) dialog.ViewModel.SetProcesses(stillRunning.ToList());
        }

        await _ipc.SendAsync(new ProcessesClosedMessage
        {
            UpdateKey = message.UpdateKey,
            StillRunning = stillRunning.ToList(),
            Declined = false,
        }).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- ICloseAppsActions

    public void CloseAndUpdate(CloseAppsViewModel dialog) => _ = CloseAndUpdateAsync(dialog);

    private async Task CloseAndUpdateAsync(CloseAppsViewModel dialog)
    {
        var update = dialog.Model;
        var names = dialog.Processes.Count > 0
            ? dialog.Processes.Select(p => p.ProcessName).ToList()
            : update.ProcessNames.ToList();

        _log.LogInformation("User chose Close apps and update for {App}: {Processes}",
            update.DisplayName, string.Join(", ", names));

        dialog.IsBusy = true;
        dialog.StatusMessage = Strings.CloseAppsClosing;

        IReadOnlyList<string> stillRunning;
        try
        {
            // Ask nicely first: a window that has unsaved work gets its chance to say so.
            stillRunning = await Task.Run(
                () => ProcessHelper.CloseAsync(names, AppInfo.SessionId, GracefulWait, force: false, _cts.Token),
                _cts.Token).ConfigureAwait(true);

            if (stillRunning.Count > 0)
            {
                // Then mean what the button says. A console process (pwsh in Windows Terminal, for instance) has no
                // main window to close, so WM_CLOSE alone never ends it and the dialog used to come straight back.
                _log.LogInformation("{Count} application(s) ignored the close request for {Key}; terminating them: {StillRunning}",
                    stillRunning.Count, update.Key, string.Join(", ", stillRunning));
                dialog.StatusMessage = Strings.CloseAppsForcing;
                stillRunning = await Task.Run(
                    () => ProcessHelper.CloseAsync(stillRunning, AppInfo.SessionId, TimeSpan.Zero, force: true, _cts.Token),
                    _cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Closing applications for {Key} failed", update.Key);
            stillRunning = ProcessHelper.GetRunning(names, AppInfo.SessionId);
        }

        // Anything left runs elevated or in another user's session: this agent has no rights over either, so it hands
        // the job to the service, which runs as LocalSystem. Never leave the dialog up waiting for the impossible.
        if (stillRunning.Count > 0)
            _log.LogWarning("{Count} application(s) are out of this agent's reach for {Key}; asking the service to close them: {StillRunning}",
                stillRunning.Count, update.Key, string.Join(", ", stillRunning));

        dialog.StatusMessage = stillRunning.Count == 0 ? Strings.CloseAppsAllClosed : Strings.CloseAppsHandedToService;
        await _ipc.InstallNowAsync(update.Key, closeBlockingProcesses: true).ConfigureAwait(true);
        dialog.RequestClose();
    }

    public void Defer(CloseAppsViewModel dialog, int minutes)
    {
        var update = dialog.Model;
        _log.LogInformation("User deferred {App} by {Minutes} minutes from the close-apps dialog",
            update.DisplayName, minutes);
        _ = _ipc.DeferAsync(update.Key, minutes);
        dialog.RequestClose();
    }

    public void NotNow(CloseAppsViewModel dialog)
    {
        var update = dialog.Model;
        _log.LogInformation("User chose Not now for {App}", update.DisplayName);
        // Mandatory updates cannot be dismissed; the dialog simply goes away until the service prompts again.
        if (!update.Mandatory) _ = _ipc.DismissAsync(update.Key);
        dialog.RequestClose();
    }

    private static string Friendly(IEnumerable<string> processNames) =>
        string.Join(", ", processNames.Select(ProcessDisplay.Friendly));
}

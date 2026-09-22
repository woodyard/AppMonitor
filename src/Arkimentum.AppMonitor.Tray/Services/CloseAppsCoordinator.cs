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
public sealed class CloseAppsCoordinator : IHostedService, ICloseAppsActions, ICloseAppsLauncher
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
        // The agent is going away, not the user declining: close without sending an answer.
        foreach (var window in _dialogs.Values.ToList()) window.CloseFromApp();
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

    /// <summary>
    /// Opens the dialog for an update, or refreshes the one already open for it. Called from the service's
    /// <see cref="PromptCloseMessage"/> and from the update card's "Close apps and update" button, which is how a
    /// user who dismissed the dialog gets it back without waiting for the next prompt.
    ///
    /// Everything here is wrapped: a throw while the view model is built or the window is created lands in the
    /// agent's <c>DispatcherUnhandledException</c> handler, which logs one line and marks it handled - and the user
    /// is left looking at a blank window. Logging the update key and the blocking detail at the point of failure is
    /// what turns that into something anyone can act on.
    /// </summary>
    public void ShowFor(PendingUpdate update)
    {
        if (update is null) { _log.LogWarning("Ignoring a request to show the close-apps dialog for a null update"); return; }

        if (_dialogs.TryGetValue(update.Key, out var existing))
        {
            _log.LogInformation("Refreshing the close-apps dialog for {App}", update.DisplayName);
            if (!TryApply(existing, update)) return;
            try { existing.Activate(); } catch (Exception ex) { _log.LogDebug(ex, "Activating the close-apps dialog for {Key} failed", update.Key); }
            return;
        }

        _log.LogInformation("Showing the close-apps dialog for {App} (blocking: {Processes})",
            update.DisplayName, Describe(update));

        CloseAppsViewModel viewModel;
        CloseAppsWindow window;
        try
        {
            viewModel = new CloseAppsViewModel(update, this);
            // Deliberately not owned by the main window: WPF hides owned windows with their owner, and this dialog
            // must survive the user closing the main window while a forced close is counting down.
            window = new CloseAppsWindow(viewModel);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not build the close-apps dialog for {Key} ({App}); blocking detail: {Detail}",
                update.Key, update.DisplayName, Describe(update));
            return;
        }

        window.Closed += (_, _) =>
        {
            _dialogs.Remove(update.Key);
            viewModel.Dispose();
        };
        // A dialog that was drawn but never rendered (blank window) is invisible in the log without this line: it
        // says whether WPF got as far as painting the content, which separates a view-model problem from a rendering one.
        window.ContentRendered += (_, _) => _log.LogInformation("Close-apps dialog for {App} rendered ({Width:F0}x{Height:F0})",
            update.DisplayName, window.ActualWidth, window.ActualHeight);
        _dialogs[update.Key] = window;
        try
        {
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not show the close-apps dialog for {Key} ({App}); blocking detail: {Detail}",
                update.Key, update.DisplayName, Describe(update));
            _dialogs.Remove(update.Key);
            try { window.CloseFromApp(); } catch { /* the window is already broken; nothing else to do */ }
        }
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
                // The update moved on by itself; the user did not decline, so send nothing.
                _dialogs[key].CloseFromApp();
            }
            else
            {
                TryApply(_dialogs[key], update);
            }
        }
    }

    /// <summary>
    /// Pushes a newer snapshot into an open dialog. A state message can arrive between <c>Show()</c> and the window's
    /// first layout pass, so a throw in here used to be exactly what left the dialog drawn but empty.
    /// </summary>
    private bool TryApply(CloseAppsWindow window, PendingUpdate update)
    {
        try
        {
            window.ViewModel.Apply(update);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applying the update to the close-apps dialog for {Key} ({App}) failed; blocking detail: {Detail}",
                update.Key, update.DisplayName, Describe(update));
            return false;
        }
    }

    /// <summary>What the service said is blocking this update, for a failure log line. Never throws.</summary>
    private static string Describe(PendingUpdate update)
    {
        try
        {
            var names = string.Join(", ", BlockingProcessSummary.NamesFor(update));
            var detail = update.BlockingDetails is { Count: > 0 } d ? BlockingProcessInfo.Describe(d) : "none";
            return $"{(names.Length == 0 ? "none" : names)} [{detail}]";
        }
        catch (Exception ex) { return $"unreadable ({ex.GetType().Name})"; }
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
            try
            {
                dialog.ViewModel.IsBusy = false;
                dialog.ViewModel.StatusMessage = stillRunning.Count == 0
                    ? Strings.CloseAppsAllClosed
                    : Strings.CloseAppsStillRunning(Friendly(stillRunning));
                if (stillRunning.Count > 0) dialog.ViewModel.SetProcesses(stillRunning.ToList());
            }
            catch (Exception ex)
            {
                // Never let a display problem swallow the ProcessesClosedMessage below: the service is waiting for it.
                _log.LogError(ex, "Updating the close-apps dialog for {Key} after the close attempt failed", message.UpdateKey);
            }
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

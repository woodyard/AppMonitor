using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// The agent's single source of truth: the last <see cref="StateMessage"/> from the service, plus a small local
/// overlay of statuses the agent knows before the service does (a user-context install it is running right now).
/// Everything here is touched on the UI thread only.
/// </summary>
public sealed class AgentStateStore : IHostedService
{
    private readonly ILogger<AgentStateStore> _log;
    private readonly IpcClientService _ipc;
    private readonly Dictionary<string, string> _localStatus = new(StringComparer.Ordinal);

    private List<PendingUpdate> _updates = [];

    public AgentStateStore(ILogger<AgentStateStore> log, IpcClientService ipc)
    {
        _log = log;
        _ipc = ipc;
    }

    /// <summary>Raised on the UI thread whenever anything the UI shows has changed.</summary>
    public event Action? Changed;

    public bool IsConnected { get; private set; }

    public bool HasReceivedState { get; private set; }

    public IReadOnlyList<PendingUpdate> Updates => _updates;

    public DateTimeOffset? LastScanUtc { get; private set; }

    public DateTimeOffset? NextScanUtc { get; private set; }

    public bool ScanInProgress { get; private set; }

    public string? ServiceVersion { get; private set; }

    public SettingsSummary Settings { get; private set; } = new();

    /// <summary>Updates that are not finished yet — what the tray badge counts.</summary>
    public int ActiveUpdateCount => _updates.Count(u => u.State != UpdateState.Installed);

    /// <summary>True when something needs the user now: a mandatory update past its deadline or a forced close pending.</summary>
    public bool NeedsAttention
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return _updates.Any(u => u.State != UpdateState.Installed &&
                                     (u.ForceCloseAtUtc is not null || u.IsPastDeadline(now)));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived += OnMessage;
        _ipc.ConnectionChanged += OnConnectionChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived -= OnMessage;
        _ipc.ConnectionChanged -= OnConnectionChanged;
        return Task.CompletedTask;
    }

    public PendingUpdate? Find(string key) => _updates.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal));

    /// <summary>Transient status the agent itself is driving (a user-context install), shown until the service catches up.</summary>
    public string? GetLocalStatus(string key) => _localStatus.TryGetValue(key, out var s) ? s : null;

    public void SetLocalStatus(string key, string? status)
    {
        if (status is null)
        {
            if (!_localStatus.Remove(key)) return;
        }
        else
        {
            if (_localStatus.TryGetValue(key, out var existing) && existing == status) return;
            _localStatus[key] = status;
        }
        Changed?.Invoke();
    }

    /// <summary>Re-raises <see cref="Changed"/> so relative times ("in 2 hours") are recomputed.</summary>
    public void Refresh() => Changed?.Invoke();

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        if (!connected) ScanInProgress = false;
        Changed?.Invoke();
    }

    private void OnMessage(IpcMessage message)
    {
        if (message is not StateMessage state) return;
        Apply(state);
    }

    /// <summary>Replaces everything the UI shows with the service's snapshot.</summary>
    public void Apply(StateMessage state)
    {
        _updates = state.Updates ?? [];
        LastScanUtc = state.LastScanUtc;
        NextScanUtc = state.NextScanUtc;
        ScanInProgress = state.ScanInProgress;
        ServiceVersion = state.ServiceVersion;
        Settings = state.Settings ?? new SettingsSummary();
        HasReceivedState = true;

        // Drop local statuses for updates the service no longer reports or has already finished.
        if (_localStatus.Count > 0)
        {
            var stale = _localStatus.Keys
                .Where(k => Find(k) is null or { State: UpdateState.Installed or UpdateState.Failed })
                .ToList();
            foreach (var key in stale) _localStatus.Remove(key);
        }

        _log.LogInformation("State: {Count} update(s), scanInProgress={Scanning}, service={ServiceVersion}",
            _updates.Count, ScanInProgress, ServiceVersion ?? "?");
        Changed?.Invoke();
    }
}

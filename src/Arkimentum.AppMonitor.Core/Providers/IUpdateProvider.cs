using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// An update source. Implementations must work both in the service (LocalSystem) and in the tray agent (user session);
/// <see cref="ExecutionContextInfo"/> tells them which one they are in.
/// </summary>
public interface IUpdateProvider
{
    UpdateSource Source { get; }

    /// <summary>
    /// Determines whether <paramref name="app"/> is installed in the current context and whether a newer version exists.
    /// <paramref name="installed"/> is the best inventory match already found by the caller (may be null).
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(AppPolicy app, InstalledApp? installed, ExecutionContextInfo context, CancellationToken ct);

    /// <summary>Installs the update described by <paramref name="update"/> in the current context.</summary>
    Task<InstallResult> InstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// A provider that answers a whole scan from one snapshot of its source instead of one query per application (winget:
/// one full listing and one upgrade listing per scope). <see cref="UpdateChecker"/> reads the snapshot before the
/// applications of that source are checked, and logs what it cost afterwards.
/// </summary>
public interface IScanSnapshotProvider
{
    /// <summary>Reads the snapshot for <paramref name="apps"/> in <paramref name="context"/>. Never throws except for cancellation.</summary>
    Task PrepareScanAsync(IReadOnlyList<AppPolicy> apps, ExecutionContextInfo context, CancellationToken ct);

    /// <summary>Logs the snapshot's cost for the scan: processes started, time spent reading it and matching <paramref name="apps"/> application(s).</summary>
    void LogScanSummary(ExecutionContextInfo context, int apps, TimeSpan matching);
}

/// <summary>Options shared by providers (populated from <see cref="AgentSettings"/> or a <see cref="Ipc.RunUserScanMessage"/>).</summary>
public sealed class ProviderOptions
{
    public bool WingetEnabled { get; set; } = true;
    public bool WebSourcesEnabled { get; set; } = true;
    public string? ProxyUrl { get; set; }
    public string? WingetGlobalArgs { get; set; }
    public bool WingetIncludeUnknown { get; set; }
    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public string DownloadDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "Arkimentum.AppMonitor");
    /// <summary>Explicit path to winget.exe (registry value WingetPath); null = auto-detect.</summary>
    public string? WingetPath { get; set; }

    public static ProviderOptions From(AgentSettings s) => new()
    {
        WingetEnabled = s.WingetEnabled,
        WebSourcesEnabled = s.WebSourcesEnabled,
        ProxyUrl = string.IsNullOrWhiteSpace(s.ProxyUrl) ? null : s.ProxyUrl,
        WingetGlobalArgs = string.IsNullOrWhiteSpace(s.WingetGlobalArgs) ? null : s.WingetGlobalArgs,
        WingetIncludeUnknown = s.WingetIncludeUnknown,
        InstallTimeout = TimeSpan.FromMinutes(Math.Max(1, s.InstallTimeoutMinutes)),
        CheckTimeout = TimeSpan.FromMinutes(Math.Max(1, s.CheckTimeoutMinutes)),
        DownloadDirectory = Path.Combine(s.StateDirectory, "Downloads"),
        WingetPath = string.IsNullOrWhiteSpace(s.WingetPath) ? null : s.WingetPath,
    };
}

public static class ProviderExtensions
{
    /// <summary>Disposes providers that own resources (e.g. the web provider's HttpClient).</summary>
    public static void DisposeAll(this IEnumerable<IUpdateProvider> providers)
    {
        foreach (var p in providers) (p as IDisposable)?.Dispose();
    }
}

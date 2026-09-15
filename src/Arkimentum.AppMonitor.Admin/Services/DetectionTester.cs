using System.Linq;
using System.Threading;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>What one "Test detection" run found.</summary>
public sealed record DetectionOutcome
{
    public bool IsInstalled { get; init; }
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the application was not in the registry and the editor's own values were used.</summary>
    public bool UsedFallbackPolicy { get; init; }
    /// <summary>"System" or "User": the context in which the application was found (null when not installed / failed).</summary>
    public string? Context { get; init; }
}

public interface IDetectionTester
{
    /// <summary>
    /// Runs the inventory scan and the configured provider for one application, in system context, on a background
    /// thread. <paramref name="fallbackPolicy"/> is evaluated on the calling (UI) thread and used only when the
    /// application is not in the registry yet.
    /// </summary>
    Task<DetectionOutcome> TestAsync(string appId, Func<AppPolicy> fallbackPolicy);
}

/// <inheritdoc />
public sealed class DetectionTester : IDetectionTester
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<DetectionTester> _log;
    private readonly SettingsStoreService _settings;
    private readonly CatalogService _catalog;

    public DetectionTester(ILoggerFactory loggers, ILogger<DetectionTester> log, SettingsStoreService settings, CatalogService catalog)
    {
        _loggers = loggers;
        _log = log;
        _settings = settings;
        _catalog = catalog;
    }

    public async Task<DetectionOutcome> TestAsync(string appId, Func<AppPolicy> fallbackPolicy)
    {
        var effective = _settings.ReadEffective(_loggers, _catalog);
        var configured = effective.Apps.FirstOrDefault(a => a.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));
        var usedFallback = configured is null;
        var app = (configured ?? fallbackPolicy()) with { Enabled = true };

        _log.LogInformation("Testing detection for {AppId} ({Source}, {Context}){Fallback}.",
            app.AppId, app.Source, app.Context, usedFallback ? " using the values in the editor" : string.Empty);

        return await Task.Run(async () =>
        {
            var providers = ProviderFactory.Create(_loggers, ProviderOptions.From(effective));
            try
            {
                var inventory = new InstalledAppScanner(_loggers.CreateLogger<InstalledAppScanner>()).Scan();
                var checker = new UpdateChecker(_loggers.CreateLogger<UpdateChecker>(), providers);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, effective.CheckTimeoutMinutes) + 1));
                // Same order as the service: machine-wide first, then the current user's context (per-user installs such as
                // Store/MSIX packages or user-scope setups are only visible there).
                var contexts = new List<ExecutionContextInfo>();
                if (app.Context != InstallContext.User) contexts.Add(ExecutionContextInfo.System);
                if (app.Context != InstallContext.System)
                {
                    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    contexts.Add(new ExecutionContextInfo { IsSystem = false, UserSid = identity.User?.Value, SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId });
                }

                UpdateCheckResult? result = null;
                foreach (var context in contexts)
                {
                    var results = await checker.CheckAsync([app], inventory, context, ProviderOptions.From(effective), cts.Token).ConfigureAwait(false);
                    var r = results.FirstOrDefault();
                    if (r is null) continue;
                    result = r;
                    if (r.IsInstalled || r.Error is not null) break;
                }

                if (result is null)
                {
                    return new DetectionOutcome { Error = "The provider returned no result.", UsedFallbackPolicy = usedFallback };
                }
                return new DetectionOutcome
                {
                    IsInstalled = result.IsInstalled,
                    InstalledVersion = result.InstalledVersion,
                    AvailableVersion = result.AvailableVersion,
                    UpdateAvailable = result.UpdateAvailable,
                    Error = result.Error,
                    UsedFallbackPolicy = usedFallback,
                    Context = result.IsInstalled ? result.ResolvedContext.ToString() : null,
                };
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Detection test for {AppId} failed.", app.AppId);
                return new DetectionOutcome { Error = $"{ex.GetType().Name}: {ex.Message}", UsedFallbackPolicy = usedFallback };
            }
            finally
            {
                providers.DisposeAll();
            }
        }).ConfigureAwait(true);
    }
}

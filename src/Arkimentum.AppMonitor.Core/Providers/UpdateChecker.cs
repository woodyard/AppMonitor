using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>Creates the configured providers for a given execution context.</summary>
public static class ProviderFactory
{
    /// <summary>
    /// Creates one provider per <see cref="UpdateSource"/>. Providers are always created (even when the corresponding
    /// source is disabled) so that <see cref="UpdateChecker"/> can report a precise reason per app instead of silently
    /// dropping it; the providers themselves honour <see cref="ProviderOptions.WingetEnabled"/> /
    /// <see cref="ProviderOptions.WebSourcesEnabled"/>.
    /// </summary>
    public static IReadOnlyList<IUpdateProvider> Create(ILoggerFactory loggerFactory, ProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            new WingetProvider(loggerFactory.CreateLogger<WingetProvider>(), options) { WingetPathOverride = options.WingetPath },
            new WebProvider(loggerFactory.CreateLogger<WebProvider>(), options),
        ];
    }
}

/// <summary>
/// Orchestrates update checks/installs across providers for a set of apps in one execution context.
/// Used by the service (system context) and by the tray agent (user context) alike.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>How many apps are checked concurrently. Web checks are cheap; winget is additionally serialised.</summary>
    public const int MaxParallelChecks = 3;

    private readonly ILogger<UpdateChecker> _logger;
    private readonly IReadOnlyList<IUpdateProvider> _providers;

    /// <summary>
    /// winget keeps machine-wide state (source index, installed-package cache) and does not like concurrent
    /// invocations, so every winget call is serialised while web checks run in parallel.
    /// </summary>
    private readonly SemaphoreSlim _wingetGate = new(1, 1);

    public UpdateChecker(ILogger<UpdateChecker> logger, IReadOnlyList<IUpdateProvider> providers)
    {
        _logger = logger;
        _providers = providers;
    }

    /// <summary>
    /// Checks every app in <paramref name="apps"/> (the caller has already decided which apps belong to this context).
    /// <paramref name="inventory"/> is the Uninstall-key inventory visible in this context; it is used to find the installed
    /// version / best match for web-source apps and to resolve <see cref="InstallContext.Auto"/>.
    /// Never throws for a single app failure: failures are returned as results with <see cref="UpdateCheckResult.Error"/> set.
    /// </summary>
    public async Task<List<UpdateCheckResult>> CheckAsync(IEnumerable<AppPolicy> apps, IReadOnlyList<InstalledApp> inventory,
        ExecutionContextInfo context, ProviderOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(apps);
        inventory ??= [];
        options ??= new ProviderOptions();

        var todo = apps.Where(a => a is not null).ToList();
        var enabled = todo.Where(a => a.Enabled).ToList();
        if (enabled.Count != todo.Count)
        {
            _logger.LogDebug("Skipping {Count} disabled app(s): {Apps}",
                todo.Count - enabled.Count, string.Join(", ", todo.Where(a => !a.Enabled).Select(a => a.AppId)));
        }
        if (enabled.Count == 0) return [];

        // Only entries installed in this very context can be matched; the other process handles the rest.
        var scoped = inventory.Where(i => i.Context == context.Context).ToList();
        _logger.LogInformation("Checking {Count} app(s) in {Context} context against {Inventory} installed entries.",
            enabled.Count, context.Context, scoped.Count);

        var results = new UpdateCheckResult[enabled.Count];
        using var throttle = new SemaphoreSlim(MaxParallelChecks, MaxParallelChecks);

        var tasks = new List<Task>(enabled.Count);
        for (var i = 0; i < enabled.Count; i++)
        {
            var index = i;
            var app = enabled[i];
            tasks.Add(Task.Run(async () =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try { results[index] = await CheckOneAsync(app, scoped, context, options, ct).ConfigureAwait(false); }
                finally { throttle.Release(); }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Individual failures are already captured as results; surface whatever completed.
        }

        var list = results.Where(r => r is not null).ToList();
        var withUpdates = list.Count(r => r.UpdateAvailable);
        _logger.LogInformation("Update check finished in {Context} context: {Updates} update(s) available, {Installed} installed, {Errors} error(s).",
            context.Context, withUpdates, list.Count(r => r.IsInstalled), list.Count(r => r.Error is not null));
        return list;
    }

    private async Task<UpdateCheckResult> CheckOneAsync(AppPolicy app, IReadOnlyList<InstalledApp> scopedInventory,
        ExecutionContextInfo context, ProviderOptions options, CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;

        var provider = _providers.FirstOrDefault(p => p.Source == app.Source);
        if (provider is null)
        {
            _logger.LogWarning("{App}: no provider registered for source {Source}.", label, app.Source);
            return UpdateCheckResult.Failed(app.AppId, app.Source, "provider not available") with { ResolvedContext = context.Context };
        }

        if ((app.Source == UpdateSource.Winget && !options.WingetEnabled) ||
            (app.Source == UpdateSource.Web && !options.WebSourcesEnabled))
        {
            _logger.LogDebug("{App}: {Source} sources are disabled by configuration; skipping.", label, app.Source);
            return UpdateCheckResult.Failed(app.AppId, app.Source, "provider not available") with { ResolvedContext = context.Context };
        }

        // Best inventory match (Match() falls back to DisplayName when no detection regex is configured).
        var installed = InstalledAppScanner.Match(app, scopedInventory).FirstOrDefault();

        using var timeoutCts = new CancellationTokenSource(options.CheckTimeout + TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        UpdateCheckResult result;
        var serialise = app.Source == UpdateSource.Winget;
        try
        {
            if (serialise) await _wingetGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                result = await provider.CheckAsync(app, installed, context, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                if (serialise) _wingetGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{App}: update check timed out after {Timeout}.", label, options.CheckTimeout);
            return UpdateCheckResult.Failed(app.AppId, app.Source, $"Update check timed out after {options.CheckTimeout.TotalSeconds:0} seconds.")
                with { ResolvedContext = context.Context };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{App}: update check failed.", label);
            return UpdateCheckResult.Failed(app.AppId, app.Source, $"{ex.GetType().Name}: {ex.Message}") with { ResolvedContext = context.Context };
        }

        result = ApplyMinimumVersion(app, result, label);
        // Display only: where the tray finds the application's icon (the winget catalog carries none).
        if (result.IsInstalled && result.IconPath is null)
            result = result with { IconPath = AppIconSource.ForInstalledApp(installed, app.DetectFilePath) };
        LogSummary(label, app, result, context);
        return result;
    }

    /// <summary>
    /// Suppresses the update when the installed version already satisfies <see cref="AppPolicy.MinimumVersion"/>.
    /// This lets an admin say "only upgrade machines older than X" without disabling the app.
    /// </summary>
    internal UpdateCheckResult ApplyMinimumVersion(AppPolicy app, UpdateCheckResult result, string label)
    {
        if (!result.UpdateAvailable || string.IsNullOrWhiteSpace(app.MinimumVersion)) return result;
        if (VersionComparer.IsUnknown(result.InstalledVersion)) return result;

        // Installed >= MinimumVersion => nothing to enforce.
        if (VersionComparer.Compare(result.InstalledVersion, app.MinimumVersion) >= 0)
        {
            _logger.LogDebug("{App}: installed {Installed} already satisfies MinimumVersion {Minimum}; not flagging the update to {Available}.",
                label, result.InstalledVersion, app.MinimumVersion, result.AvailableVersion);
            return result with { UpdateAvailable = false };
        }
        return result;
    }

    private void LogSummary(string label, AppPolicy app, UpdateCheckResult result, ExecutionContextInfo context)
    {
        var ctx = result.ResolvedContext == InstallContext.Auto ? context.Context : result.ResolvedContext;
        var src = app.Source.ToString().ToLowerInvariant();

        if (result.Error is not null)
            _logger.LogWarning("{App}: check failed ({Source}, {Context}) - {Error}", label, src, ctx, result.Error);
        else if (!result.IsInstalled)
            _logger.LogInformation("{App}: not installed ({Source}, {Context})", label, src, ctx);
        else if (result.UpdateAvailable)
            _logger.LogInformation("{App}: {Installed} -> {Available} available ({Source}, {Context})",
                label, result.InstalledVersion, result.AvailableVersion, src, ctx);
        else
            _logger.LogInformation("{App}: {Installed} is up to date ({Source}, {Context})", label, result.InstalledVersion, src, ctx);
    }

    /// <summary>Installs a single pending update using the provider matching <see cref="PendingUpdate.Source"/>.</summary>
    public async Task<InstallResult> InstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context,
        IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(update);

        var label = string.IsNullOrWhiteSpace(update.DisplayName) ? app.AppId : update.DisplayName;
        var provider = _providers.FirstOrDefault(p => p.Source == update.Source);
        if (provider is null)
        {
            _logger.LogError("{App}: no provider registered for source {Source}; cannot install.", label, update.Source);
            return InstallResult.Fail($"No provider available for source '{update.Source}'.");
        }

        _logger.LogInformation("{App}: installing {Installed} -> {Available} ({Source}, {Context}).",
            label, update.InstalledVersion, update.AvailableVersion, update.Source.ToString().ToLowerInvariant(), context.Context);

        try
        {
            var result = await provider.InstallAsync(app, update, context, progress, ct).ConfigureAwait(false);
            if (result.Success)
                _logger.LogInformation("{App}: installed successfully (version {Version}{Reboot}). {Message}",
                    label, result.InstalledVersion ?? update.AvailableVersion,
                    result.RebootRequired ? ", reboot required" : string.Empty, result.Message);
            else
                _logger.LogError("{App}: install failed (exit {ExitCode}): {Message}", label, result.ExitCode, result.Message);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{App}: install threw an unexpected exception.", label);
            return InstallResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

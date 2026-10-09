using System.Collections.Concurrent;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// Dependencies winget does not see. TechSmith Snagit's wix installer declares the package dependency
/// <c>Microsoft.EdgeWebView2Runtime</c>; winget does not correlate the installed WebView2 runtime with that package at all
/// (<c>winget list --id Microsoft.EdgeWebView2Runtime --exact</c>: "No installed package found", because its Uninstall
/// entry is a <c>SystemComponent</c>), so every Snagit update first downloaded and installed the runtime again - six
/// minutes on H-SurfaceLap5 (2026-10-09). With <see cref="ProviderOptions.SkipInstalledDependencies"/> an upgrade or
/// install asks <c>winget show</c> (same scope and installer filter as the run) which dependencies the selected installer
/// declares, and adds <c>--skip-dependencies</c> only when every one is a package dependency that winget does not list
/// in any scope while an Uninstall entry with the package's name is there (see <see cref="DecideSkipDependencies"/>).
/// A dependency winget does list is left to winget, which also honours its minimum version. Any lookup that fails means
/// no skip, as before; it never fails the install.
/// </summary>
public sealed partial class WingetProvider
{
    /// <summary>
    /// Replaces the registry read of the dependency check: the Uninstall entry with exactly this display name in the
    /// context (HKLM, plus HKCU in a user's session). Tests answer it; null (the default) reads the registry
    /// (<see cref="UninstallEntryLookup.FindByDisplayName"/>).
    /// </summary>
    internal Func<string, ExecutionContextInfo, InstalledApp?>? DependencyRegistryReader { get; init; }

    /// <summary>
    /// What the dependency check found out about one package dependency: whether winget lists it in any scope (null when
    /// that could not be read), the package's name from <c>winget show</c>, and the Uninstall entry of that name.
    /// </summary>
    internal sealed record DependencyEvidence(string Id, bool? ListedByWinget, string? Name, InstalledApp? Registry);

    /// <summary>
    /// Whether an upgrade or install adds <c>--skip-dependencies</c>. Pure, so the rule is testable. Only when the
    /// selected installer was read (<paramref name="dependencies"/>), declares at least one package dependency and nothing
    /// else (Windows features, libraries and external dependencies cannot be verified), and every package dependency is
    /// not listed by winget (a listed one is winget's to handle, minimum version included), has a package name, and has
    /// an Uninstall entry of that name - at no lower version than a minimum the installer requires.
    /// </summary>
    internal static (bool Skip, string Reason) DecideSkipDependencies(WingetInstallerDependencies? dependencies, IReadOnlyList<DependencyEvidence> evidence)
    {
        if (dependencies is null) return (false, "the selected installer's dependencies could not be read");
        if (!dependencies.InstallerFound) return (false, "winget show named no installer");
        if (dependencies.Packages.Count == 0)
            return (false, dependencies.OtherKinds.Count == 0 ? "the installer declares no package dependencies" : $"the installer declares no package dependencies, only {string.Join(", ", dependencies.OtherKinds)}");
        if (dependencies.OtherKinds.Count > 0)
            return (false, $"the installer also declares {string.Join(", ", dependencies.OtherKinds)}, which cannot be verified");
        foreach (var dep in dependencies.Packages)
        {
            var e = evidence.FirstOrDefault(x => string.Equals(x.Id, dep.Id, StringComparison.OrdinalIgnoreCase));
            if (e is null) return (false, $"{dep.Id} was not checked");
            if (e.ListedByWinget is null) return (false, $"whether winget lists {dep.Id} could not be read");
            if (e.ListedByWinget == true) return (false, $"winget lists {dep.Id}, so winget handles it");
            if (string.IsNullOrWhiteSpace(e.Name)) return (false, $"the package name of {dep.Id} could not be read");
            if (e.Registry is null) return (false, $"{dep.Id} ('{e.Name}') is neither listed by winget nor in the Uninstall registry");
            if (dep.MinVersion is not null
                && (VersionComparer.IsUnknown(e.Registry.DisplayVersion) || VersionComparer.Compare(e.Registry.DisplayVersion, dep.MinVersion) < 0))
                return (false, $"{dep.Id} ('{e.Name}') is installed at {e.Registry.DisplayVersion ?? "an unknown version"}, and the installer requires at least {dep.MinVersion}");
        }
        return (true, "every package dependency is installed but not listed by winget");
    }

    /// <summary>Whether this evidence already rules a skip out, so later dependencies need no lookups.</summary>
    private static bool RulesOutSkip(DependencyEvidence e) => e.ListedByWinget != false || string.IsNullOrWhiteSpace(e.Name) || e.Registry is null;

    private readonly ConcurrentDictionary<(InstallContext Context, string Id, string Source, string Filter), WingetInstallerDependencies?> _installerDependencyCache = new();
    private readonly ConcurrentDictionary<(InstallContext Context, string Id, string Source), DependencyEvidence> _dependencyCache = new();

    /// <summary>
    /// " --skip-dependencies" when the dependency check allows it for this run (see <see cref="DecideSkipDependencies"/>),
    /// else nothing. <paramref name="filter"/> is the scope or installer-type filter the run itself uses, so
    /// <c>winget show</c> selects the same installer. Never throws except for cancellation.
    /// </summary>
    private async Task<string> SkipDependenciesArgumentAsync(AppPolicy app, string winget, string wingetId, string sourceName, string filter,
        string? extraArgs, ExecutionContextInfo context, CancellationToken ct)
    {
        if (!_options.SkipInstalledDependencies) return string.Empty;
        if (HasArgument(extraArgs, "--skip-dependencies") || HasArgument(_options.WingetGlobalArgs, "--skip-dependencies")) return string.Empty;
        try
        {
            var dependencies = await InstallerDependenciesAsync(winget, wingetId, sourceName, filter, context, ct).ConfigureAwait(false);
            var evidence = new List<DependencyEvidence>();
            if (dependencies is { InstallerFound: true, Packages.Count: > 0, OtherKinds.Count: 0 })
            {
                foreach (var dep in dependencies.Packages)
                {
                    var e = await DependencyEvidenceAsync(app, winget, dep.Id, sourceName, context, ct).ConfigureAwait(false);
                    evidence.Add(e);
                    if (RulesOutSkip(e)) break;
                }
            }

            var (skip, reason) = DecideSkipDependencies(dependencies, evidence);
            if (!skip)
            {
                _logger.LogDebug("{AppId}: installing '{WingetId}' with its dependencies: {Reason}.", app.AppId, wingetId, reason);
                return string.Empty;
            }
            foreach (var e in evidence)
                _logger.LogInformation("{AppId}: dependency {Dependency} ('{Name}') is installed ({Version}, registry) but winget does not list it; installing with --skip-dependencies.",
                    app.AppId, e.Id, e.Name, e.Registry!.DisplayVersion ?? "version unknown");
            return " --skip-dependencies";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{AppId}: the dependencies of '{WingetId}' could not be checked; installing with them.", app.AppId, wingetId);
            return string.Empty;
        }
    }

    /// <summary>
    /// The dependencies of the installer <c>winget show --id X --exact --source S &lt;filter&gt;</c> selects, cached per id,
    /// source, filter and context; null when winget could not answer. "No applicable installer" is
    /// <see cref="WingetInstallerDependencies.NoInstaller"/>.
    /// </summary>
    private async Task<WingetInstallerDependencies?> InstallerDependenciesAsync(string winget, string wingetId, string sourceName, string filter,
        ExecutionContextInfo context, CancellationToken ct)
    {
        var key = (context.Context, wingetId.ToLowerInvariant(), SourceKey(sourceName), filter.Trim().ToLowerInvariant());
        if (_installerDependencyCache.TryGetValue(key, out var cached)) return cached;
        var show = await RunShowAsync(winget, wingetId, sourceName, context, filter, ct).ConfigureAwait(false);
        WingetInstallerDependencies? parsed =
            !show.Started || show.TimedOut ? null
            : IsNoApplicableInstaller(show.ExitCode, show.CombinedOutput) ? WingetInstallerDependencies.NoInstaller
            : show.ExitCode != 0 ? null
            : WingetOutputParser.ParseInstallerDependencies(show.StandardOutput.Length > 0 ? show.StandardOutput : show.CombinedOutput);
        _installerDependencyCache[key] = parsed;
        return parsed;
    }

    /// <summary>
    /// What is known about one package dependency, cached per context, id and source for this provider instance: whether
    /// <c>winget list --id D --exact</c> without a scope finds it (a dependency can be machine-wide while the application
    /// is per user), and - only when it does not - the package name from <c>winget show --id D --exact</c> and the
    /// Uninstall entry with that name. A failed lookup leaves its part unknown, which rules the skip out.
    /// </summary>
    private async Task<DependencyEvidence> DependencyEvidenceAsync(AppPolicy app, string winget, string dependencyId, string sourceName,
        ExecutionContextInfo context, CancellationToken ct)
    {
        var key = (context.Context, dependencyId.ToLowerInvariant(), SourceKey(sourceName));
        if (_dependencyCache.TryGetValue(key, out var cached)) return cached;

        DependencyEvidence evidence;
        var listed = await ListAsync(winget, app with { WingetId = dependencyId, WingetSourceName = sourceName }, context, _options.CheckTimeout, ct, unscoped: true)
            .ConfigureAwait(false);
        bool? isListed = listed.Row is not null ? true : listed.NotInstalled ? false : null;
        if (isListed != false)
        {
            evidence = new DependencyEvidence(dependencyId, isListed, null, null);
        }
        else
        {
            var show = await RunShowAsync(winget, dependencyId, sourceName, context, string.Empty, ct).ConfigureAwait(false);
            var name = show.Started && !show.TimedOut && show.ExitCode == 0 ? WingetOutputParser.ParseShowName(show.CombinedOutput, dependencyId) : null;
            InstalledApp? entry = null;
            if (name is not null)
            {
                try
                {
                    entry = DependencyRegistryReader is { } reader ? reader(name, context) : UninstallEntryLookup.FindByDisplayName(name, includeCurrentUser: !context.IsSystem);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "{AppId}: the Uninstall registry could not be read for the dependency {Dependency}.", app.AppId, dependencyId);
                }
            }
            evidence = new DependencyEvidence(dependencyId, false, name, entry);
        }
        _dependencyCache[key] = evidence;
        return evidence;
    }
}

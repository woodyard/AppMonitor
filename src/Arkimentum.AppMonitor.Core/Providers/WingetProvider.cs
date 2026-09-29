using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// Update provider backed by the Windows Package Manager (winget).
/// <para>
/// System vs. user context is expressed with <c>--scope machine</c> / <c>--scope user</c>: verified on Windows 11 with
/// winget 1.30, a machine-wide package (7zip.7zip) is only returned with <c>--scope machine</c> and a per-user package
/// (Microsoft.VisualStudioCode) only with <c>--scope user</c>; the mismatching scope exits with 0x8A150014
/// ("No installed package found matching input criteria.").
/// </para>
/// <para>
/// Package ids are resolved generically: the id is the id of the row in winget's own listing whose name passes the
/// app's identity rule (<see cref="InstalledAppScanner.NameMatches"/>). The configured <c>WingetId</c> (optionally
/// with <c>;</c>-separated alternatives) is a hint that takes precedence, not a requirement. In order: a configured id
/// that <c>winget list</c> finds; else the full per-scope listing searched by name; then, once the product is known to
/// be installed, winget's upgrade listing - a configured id first, else a name match that describes the same install.
/// The resolved id travels with the pending update and is the first id the install tries.
/// </para>
/// <para>
/// A scan starts winget a fixed number of times per scope, however many applications it checks: one full
/// <c>winget list</c> and one <c>winget upgrade</c> per winget source the applications use, both read once per provider
/// instance (= once per scan, see <see cref="PrepareScanAsync"/>) and shared by every application. Every application is
/// then resolved in memory against that snapshot by the rules above. winget's per-id lookup (<c>winget list --id X
/// --exact</c>) only runs when the snapshot cannot answer: for every application when the full listing failed, and for
/// the single id otherwise (see <see cref="ClassifyInListing"/>). Installs and the checks after them always ask winget
/// afresh for the one id they handle; they never read the snapshot.
/// </para>
/// <para>
/// winget lists a package once per installed version. When several versions are registered side by side, the highest
/// installed version is the package's version everywhere - in the scan and in every check after an install (see
/// <see cref="WingetOutputParser.CombineInstalls"/>).
/// </para>
/// </summary>
public sealed class WingetProvider : IUpdateProvider, IScanSnapshotProvider
{
    private readonly ILogger<WingetProvider> _logger;
    private readonly ProviderOptions _options;

    public WingetProvider(ILogger<WingetProvider> logger, ProviderOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public UpdateSource Source => UpdateSource.Winget;

    /// <summary>Optional explicit winget.exe path (from configuration); normally left null.</summary>
    public string? WingetPathOverride { get; init; }

    /// <summary>
    /// Replaces the winget process for the read-only lookups (<c>list</c>, <c>upgrade</c>); arguments, timeout,
    /// context. Tests feed captured winget output through it; null (the default) runs winget.
    /// </summary>
    internal Func<string, TimeSpan, ExecutionContextInfo, CancellationToken, Task<ProcessRunResult>>? LookupRunner { get; init; }

    /// <summary>
    /// Replaces the winget process for every run of the install path (<c>upgrade</c>, <c>install</c>, <c>uninstall</c>,
    /// <c>show</c>); arguments, timeout, context. The checks after a run still go through <see cref="LookupRunner"/>.
    /// Tests script winget's answers through it, so an install test never starts the real winget; null (the default) runs winget.
    /// </summary>
    internal Func<string, TimeSpan, ExecutionContextInfo, CancellationToken, Task<ProcessRunResult>>? InstallRunner { get; init; }

    /// <summary>
    /// Replaces the registry read that verifies the install of an uncorrelated product: the application's best match
    /// among the Uninstall entries of the context. Tests feed the "after" state through it; null (the default) reads the
    /// registry (see <see cref="ReadInstalledFromRegistry"/>).
    /// </summary>
    internal Func<AppPolicy, ExecutionContextInfo, InstalledApp?>? RegistryReader { get; init; }

    /// <summary>
    /// Checks every id with winget's own per-id lookup, as the fallback for a failed full listing does, even when the
    /// listing is complete. For tests that prove both paths reach the same result for the same winget output.
    /// </summary>
    internal bool ForcePerAppLookups { get; init; }

    /// <summary>
    /// The most per-id lookups one scan makes in one scope for ids the full listing cannot settle (see
    /// <see cref="ClassifyInListing"/>). Beyond it the listing's answer stands, so a scan never slides back to one winget
    /// process per application without saying so.
    /// </summary>
    internal const int MaxPerAppLookups = 10;

    public async Task<UpdateCheckResult> CheckAsync(AppPolicy app, InstalledApp? installed, ExecutionContextInfo context, CancellationToken ct)
    {
        if (!_options.WingetEnabled)
        {
            _logger.LogDebug("{AppId}: winget is disabled by configuration; skipping.", app.AppId);
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, "winget disabled by configuration");
        }

        if (string.IsNullOrWhiteSpace(app.WingetId))
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, $"No WingetId configured for '{app.AppId}'.");

        var winget = LocateWinget(context);
        if (winget is null)
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, "winget.exe was not found on this machine (see the log for the probed locations).");

        // The scan's snapshot: winget's full per-scope listing, read once per provider instance (= once per scan) and
        // shared by every application of the scan (see PrepareScanAsync). Each configured id is looked up in it in
        // memory; winget is only asked about a single id when the listing cannot answer (see LookupIdAsync).
        var listing = await GetFullListAsync(winget, context, ct).ConfigureAwait(false);

        // A WingetId may list alternatives ("Mozilla.Firefox;Mozilla.Firefox.MSIX"): the same product is often published
        // as a classic installer and as a Store/MSIX package with different ids. The first id that is installed wins.
        // Alternatives are optional hints: when none of them is installed the id is resolved from winget's listing by
        // the app's identity rule (below).
        var candidates = SplitIds(app.WingetId);
        ListLookup? lookup = null;
        string? matchedId = null;
        string? firstError = null;
        foreach (var id in candidates)
        {
            var attempt = await LookupIdAsync(winget, app, id, listing, context, ct).ConfigureAwait(false);
            if (attempt.NotInstalled)
            {
                _logger.LogDebug("{AppId}: winget reports '{WingetId}' is not installed in the {Context} scope.", app.AppId, id, context.Context);
                continue;
            }
            if (attempt.Error is not null) { firstError ??= attempt.Error; continue; }
            lookup = attempt;
            matchedId = id;
            break;
        }

        if (lookup is null && firstError is null)
        {
            // Generic resolution: none of the configured ids is installed here, but the product may be installed under
            // another id of the same vendor (Adobe.Acrobat.Reader.32-bit for a policy that names the 64-bit id, a
            // per-user Google.Chrome.EXE for Google.Chrome). The id of the row in winget's own listing whose name
            // passes the app's identity rule is the id winget manages the product by.
            var byName = ResolveByName(listing.Rows, app, candidates);
            if (byName is not null)
            {
                _logger.LogInformation("{AppId}: resolved winget id '{WingetId}' from winget's {Context}-scope listing by name ('{Name}'); configured '{Configured}' is not installed here.",
                    app.AppId, byName.Id, context.Context, byName.Name, string.Join(";", candidates));
                lookup = new ListLookup(byName, false, null);
                matchedId = byName.Id;
            }
        }

        if (lookup is null && firstError is null && installed is not null)
        {
            // winget does not correlate the installed product with any package, but the registry detection found it:
            // an MSI product code that the index carries in more than one package (Node.js: OpenJS.NodeJS.LTS and
            // OpenJS.NodeJS.22) makes winget list the install only as "ARP\Machine\X64\{GUID}" without a source, and
            // "winget upgrade" never offers it. The package itself still says which version is current.
            var uncorrelated = await CheckUncorrelatedAsync(winget, app, installed, candidates, context, ct).ConfigureAwait(false);
            if (uncorrelated is not null) return uncorrelated;
        }

        if (lookup is null)
        {
            if (firstError is not null)
                return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, firstError) with { WingetId = candidates[0], ResolvedContext = context.Context };
            return UpdateCheckResult.NotInstalled(app.AppId, UpdateSource.Winget) with
            {
                WingetId = candidates[0],
                ResolvedContext = context.Context,
            };
        }
        if (candidates.Count > 1) _logger.LogDebug("{AppId}: matched winget id '{WingetId}' (of {Count} alternatives).", app.AppId, matchedId, candidates.Count);

        var row = lookup.Row!;

        // "winget list" correlates an installed product with every manifest that matches it (the Firefox MSIX build is
        // listed under both Mozilla.Firefox and Mozilla.Firefox.MSIX), while "winget upgrade" applies the applicability
        // filters and names the id that can actually upgrade it. So when a configured id is in the upgrade listing,
        // that id - and its row - wins; otherwise a row whose name passes the app's identity rule and that describes the
        // install just found (see PickFromUpgradeListing). When neither exists, the list-based result stands: products
        // winget upgrade refuses (Perplexity.Comet, Microsoft.BingWallpaper) are still detected and go to the scoped
        // install fallback.
        var upgradeRows = await GetUpgradeListingAsync(winget, app.WingetSourceName, context, ct).ConfigureAwait(false);
        var upgradeRow = PickFromUpgradeListing(upgradeRows, candidates, app, row);
        if (upgradeRow is not null)
        {
            var configured = candidates.FirstOrDefault(c => string.Equals(c, upgradeRow.Id, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(upgradeRow.Id, matchedId, StringComparison.OrdinalIgnoreCase))
            {
                if (configured is not null)
                    _logger.LogDebug("{AppId}: winget's upgrade listing names '{UpgradeId}' (list matched '{ListId}').", app.AppId, upgradeRow.Id, matchedId);
                else
                    _logger.LogInformation("{AppId}: resolved winget id '{UpgradeId}' from winget's {Context}-scope upgrade listing by name ('{Name}'); the installed product was listed as '{ListId}'.",
                        app.AppId, upgradeRow.Id, context.Context, upgradeRow.Name, matchedId);
            }
            matchedId = configured ?? upgradeRow.Id;

            // Several installs of the product can be registered at once (.NET keeps every patch release side by side,
            // two PuTTY builds), and winget keeps offering the upgrade for an older one although a newer install is
            // there too. The highest installed version counts - the same rule the check after an install applies (see
            // WingetOutputParser.CombineInstalls) - so an update is only flagged when the newest install is outdated.
            var combined = WingetOutputParser.CombineInstalls([upgradeRow, row])!;
            if (upgradeRow.HasAvailable && !combined.HasAvailable)
                _logger.LogDebug("{AppId}: winget's upgrade listing offers {Available} for the {Older} install of '{UpgradeId}', but {Installed} is installed as well; the highest installed version counts, so there is no update.",
                    app.AppId, upgradeRow.Available, upgradeRow.Version, upgradeRow.Id, combined.Version);
            row = combined;
        }
        var installedVersion = string.IsNullOrWhiteSpace(row.Version) ? null : row.Version;
        var available = string.IsNullOrWhiteSpace(row.Available) ? null : row.Available;

        bool updateAvailable;
        if (available is null)
        {
            updateAvailable = false;
        }
        else if (VersionComparer.IsUnknown(installedVersion))
        {
            // winget prints "Unknown" for packages whose installed version it cannot determine. Acting on those is
            // opt-in, because "upgrading" them can reinstall an unrelated build.
            updateAvailable = _options.WingetIncludeUnknown;
            _logger.LogDebug("{AppId}: installed version is unknown ('{Installed}'); UpdateAvailable={Flag} (WingetIncludeUnknown).",
                app.AppId, installedVersion ?? "<empty>", updateAvailable);
        }
        else
        {
            updateAvailable = VersionComparer.IsNewer(available, installedVersion);
        }

        return new UpdateCheckResult
        {
            AppId = app.AppId,
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = installedVersion,
            AvailableVersion = available,
            UpdateAvailable = updateAvailable,
            WingetId = matchedId,
            ResolvedContext = context.Context,
        };
    }

    /// <summary>
    /// The check for a product the registry detection found but winget does not correlate with any package (see
    /// <see cref="UpdateCheckResult.WingetUncorrelated"/>). The first configured id (in order) whose package winget
    /// knows gives the available version (<c>winget show</c>, see <see cref="ShowPackageAsync"/>); the installed version
    /// is the registry's. Null when winget knows none of the ids, so the caller reports the product as not installed,
    /// as before; a failed check when a <c>winget show</c> failed for another reason before a known id was reached
    /// (a failed check keeps a tracked update, "not installed" would drop it).
    /// </summary>
    private async Task<UpdateCheckResult?> CheckUncorrelatedAsync(string winget, AppPolicy app, InstalledApp installed, IReadOnlyList<string> candidates,
        ExecutionContextInfo context, CancellationToken ct)
    {
        string? firstError = null;
        foreach (var id in candidates)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var show = await ShowPackageAsync(winget, id, app.WingetSourceName, context, ct).ConfigureAwait(false);
            // An id winget could not answer for may be the first one that exists: stop rather than pick a later one.
            if (show.Error is not null) { firstError = show.Error; break; }
            if (show.Version is null)
            {
                _logger.LogDebug("{AppId}: winget knows no package '{WingetId}'; trying the next configured id, if any.", app.AppId, id);
                continue;
            }

            var installedVersion = string.IsNullOrWhiteSpace(installed.DisplayVersion) ? null : installed.DisplayVersion.Trim();
            bool updateAvailable;
            if (VersionComparer.IsUnknown(installedVersion))
            {
                // The same opt-in as for winget's own "Unknown": acting on it can reinstall an unrelated build.
                updateAvailable = _options.WingetIncludeUnknown;
                _logger.LogDebug("{AppId}: the registry has no usable version ('{Installed}'); UpdateAvailable={Flag} (WingetIncludeUnknown).",
                    app.AppId, installedVersion ?? "<empty>", updateAvailable);
            }
            else
            {
                updateAvailable = VersionComparer.IsNewer(show.Version, installedVersion);
            }

            _logger.LogInformation("{AppId}: winget does not correlate the installed product ({Version}, registry); available version {Available} from the package '{WingetId}'",
                app.AppId, installedVersion ?? "unknown", show.Version, id);
            return new UpdateCheckResult
            {
                AppId = app.AppId,
                Source = UpdateSource.Winget,
                IsInstalled = true,
                InstalledVersion = installedVersion,
                AvailableVersion = show.Version,
                UpdateAvailable = updateAvailable,
                WingetId = id,
                ResolvedContext = context.Context,
                WingetUncorrelated = true,
            };
        }

        if (firstError is not null)
        {
            _logger.LogWarning("{AppId}: '{Name}' {Version} is installed (registry) but winget does not correlate it with a package, and the package's version could not be read: {Error}",
                app.AppId, installed.DisplayName, installed.DisplayVersion ?? "(no version)", firstError);
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, $"winget does not correlate the installed product with a package, and the package's version could not be read: {firstError}")
                with { WingetId = candidates[0], ResolvedContext = context.Context };
        }

        _logger.LogInformation("{AppId}: '{Name}' {Version} is installed (registry) but winget neither lists it under the configured id(s) nor knows a package '{Configured}'; reporting it as not installed for winget.",
            app.AppId, installed.DisplayName, installed.DisplayVersion ?? "(no version)", string.Join(";", candidates));
        return null;
    }

    /// <summary>What <c>winget show</c> says about a package: its current version (both null when winget knows no such package), or the error that kept it from answering.</summary>
    private sealed record ShowLookup(string? Version, string? Error);

    private readonly ConcurrentDictionary<(InstallContext Context, string Id, string Source), ShowLookup> _showCache = new();

    /// <summary>
    /// <c>winget show --id X --exact [--source S] --accept-source-agreements --disable-interactivity</c>, cached per id,
    /// source and context for the lifetime of this provider instance (= one scan), like the listings. No scope filter:
    /// the package's version does not depend on it, and the install applies the scope itself.
    /// </summary>
    private async Task<ShowLookup> ShowPackageAsync(string winget, string id, string? sourceName, ExecutionContextInfo context, CancellationToken ct)
    {
        var key = (context.Context, id.ToLowerInvariant(), SourceKey(sourceName));
        if (_showCache.TryGetValue(key, out var cached)) return cached;

        ShowLookup lookup;
        try
        {
            var args = new StringBuilder()
                .Append("show --id ").Append(Quote(id))
                .Append(" --exact");
            AppendSource(args, sourceName);
            args.Append(" --accept-source-agreements --disable-interactivity");
            AppendExtra(args, _options.WingetGlobalArgs);

            var run = await RunLookupAsync(winget, args.ToString(), _options.CheckTimeout, context, ct).ConfigureAwait(false);
            if (!run.Started) lookup = new ShowLookup(null, run.StartFailure);
            else if (run.TimedOut) lookup = new ShowLookup(null, $"winget show timed out after {_options.CheckTimeout.TotalSeconds:0} seconds and was terminated.");
            else if (IsUnknownPackage(run.ExitCode, run.CombinedOutput)) lookup = new ShowLookup(null, null);
            else if (run.ExitCode != 0) lookup = new ShowLookup(null, $"winget show exited with 0x{run.ExitCode:X8}: {run.LastLines(2)}");
            else lookup = ParseShowVersion(run.CombinedOutput) is { } version
                ? new ShowLookup(version, null)
                : new ShowLookup(null, $"winget show printed no package version: {run.LastLines(2)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "winget show for '{WingetId}' failed.", id);
            lookup = new ShowLookup(null, $"{ex.GetType().Name}: {ex.Message}");
        }
        _showCache[key] = lookup;
        return lookup;
    }

    /// <summary>
    /// Whether <c>winget show</c> answered that it knows no package with the id (0x8A150014, or its "No package found"
    /// message). Pure, so the rule is testable.
    /// </summary>
    internal static bool IsUnknownPackage(int exitCode, string? output) =>
        exitCode == WingetOutputParser.ExitNoInstalledPackageFound
        || (output?.Contains("No package found matching input criteria", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// The package version <c>winget show</c> prints ("Version: 26.7.0"), or null when the output has no such line.
    /// Pure, so the parsing is testable. Only a line that starts with "Version:" counts (not "Installer Version:" or
    /// the like), and the first one wins: it belongs to the package, before the installer section.
    /// </summary>
    internal static string? ParseShowVersion(string? showOutput)
    {
        if (string.IsNullOrWhiteSpace(showOutput)) return null;
        const string label = "Version:";
        foreach (var raw in showOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[label.Length..].Trim();
            return value.Length == 0 || VersionComparer.IsUnknown(value) ? null : value;
        }
        return null;
    }

    /// <summary>Splits a WingetId setting into its alternatives (separated by ';' or ',' or whitespace).</summary>
    public static IReadOnlyList<string> SplitIds(string? wingetId)
    {
        var list = (wingetId ?? string.Empty).Split([';', ',', ' ', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? [string.Empty] : list;
    }

    public async Task<InstallResult> InstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, IProgress<string>? progress, CancellationToken ct)
    {
        if (!_options.WingetEnabled)
            return InstallResult.Fail("winget disabled by configuration");

        // Candidate ids: the one resolved at scan time first, then the configured ids. The scan resolves the id
        // generically (see CheckAsync): a configured id winget lists as installed, else the id of the listing row whose
        // name passes the app's identity rule, and then the id winget's upgrade listing names for that install - so the
        // resolved id may be one the policy never mentions (Adobe.Acrobat.Reader.32-bit, a per-user Google.Chrome.EXE,
        // Mozilla.Firefox.MSIX). The ordered retry below is the backstop for when the upgrade listing was unavailable:
        // a refusal for one id means try the next, an id winget does not list as installed at all is skipped (it is a
        // configured hint for another build, and installing it would add a second product), and the fallbacks only run
        // once every id refused.
        var candidates = SplitIds(update.WingetId).Concat(SplitIds(update.WingetIdAlternatives)).Concat(SplitIds(app.WingetId))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
            return InstallResult.Fail($"No WingetId configured for '{app.AppId}'.");

        var winget = LocateWinget(context);
        if (winget is null)
            return InstallResult.Fail("winget.exe was not found on this machine.");

        var sourceName = FirstNonEmpty(update.WingetSourceName, app.WingetSourceName, "winget");

        // winget does not correlate the installed product with any package (see CheckAsync): "winget upgrade" would only
        // answer "not installed" for every id, so the package the scan took the available version from is installed
        // over the product directly and the result is judged by the registry.
        if (update.WingetUncorrelated)
            return await InstallUncorrelatedAsync(app, update, context, winget, candidates[0], sourceName, progress, ct).ConfigureAwait(false);

        var fallbackRan = false;
        var result = await RunCandidatesAsync(
            candidates,
            async wingetId =>
            {
                var outcome = await UpgradeOneAsync(app, update, context, winget, wingetId, sourceName, progress, ct).ConfigureAwait(false);
                if (outcome.Refusal is not null && !string.Equals(wingetId, candidates[^1], StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("{AppId}: winget refused to upgrade '{WingetId}' (0x{Code:X8}); trying the next configured id before any fallback.",
                        app.AppId, wingetId, outcome.Refusal.ExitCode);
                return outcome;
            },
            async refusal =>
            {
                // The take-over is checked first: when it is enabled it is the configured answer to a technology
                // mismatch, and "winget install --force" (ReinstallAsync) would leave the old install behind.
                if (ShouldReplace(refusal.ExitCode, update.WingetReplaceOnMismatch || app.WingetReplaceOnMismatch, stillOutdated: true))
                {
                    fallbackRan = true;
                    return await ReplaceAsync(app, update, context, winget, refusal.WingetId, sourceName, refusal.ExitCode, progress, ct).ConfigureAwait(false);
                }
                if (ShouldReinstall(refusal.ExitCode, context, stillOutdated: true))
                {
                    fallbackRan = true;
                    var reason = refusal.ExitCode == WingetOutputParser.ExitNoApplicableUpgrade
                        ? "no applicable upgrade found (the manifest's installer does not match how the product is installed, typically its scope)"
                        : "the installed package type does not match the installer type";
                    return await ReinstallAsync(app, update, context, winget, refusal.WingetId, sourceName, reason, refusal.ExitCode, progress, ct).ConfigureAwait(false);
                }
                return null;
            }).ConfigureAwait(false);

        if (!result.Success && !fallbackRan)
            _logger.LogWarning("{AppId}: {Message}", app.AppId, result.Message);
        return result;
    }

    /// <summary>
    /// A plain <c>winget upgrade</c> that winget refused (see <see cref="IsRefusal"/>) while the product is still
    /// outdated or its version could not be read. Carries what the fallbacks need, so the upgrade is not run again.
    /// </summary>
    /// <param name="WingetId">The id the upgrade was run for.</param>
    /// <param name="ExitCode">winget's refusal exit code.</param>
    /// <param name="VersionAfter">The version winget listed after the refusal, or null when it could not be read.</param>
    /// <param name="PlainResult">The failure reported when no fallback applies.</param>
    internal sealed record UpgradeRefusal(string WingetId, int ExitCode, string? VersionAfter, InstallResult PlainResult);

    /// <summary>
    /// Either a final result for one id, the refusal that sends the install on to the next id, or the answer that winget
    /// does not list the id as installed at all (<see cref="IdNotInstalled"/>, which also moves on to the next id but
    /// never gets a fallback).
    /// </summary>
    internal sealed record UpgradeOutcome(InstallResult? Result, UpgradeRefusal? Refusal, bool IdNotInstalled = false)
    {
        public static UpgradeOutcome Final(InstallResult result) => new(result, null);
        public static UpgradeOutcome Refused(UpgradeRefusal refusal) => new(null, refusal);
        public static UpgradeOutcome NotInstalled(InstallResult result) => new(result, null, true);
    }

    /// <summary>
    /// Whether a plain <c>winget upgrade</c> answered that no installed package matches the id (0x8A150014, or its
    /// message with a failing exit code). Pure, so the rule is testable. With generic id resolution the candidates can
    /// include configured ids for another build of the product (the 64-bit id when the 32-bit one is installed); such an
    /// id is skipped rather than ending the install, and it never gets a fallback, since "install --force" would put
    /// that other build next to the installed one.
    /// </summary>
    internal static bool IsIdNotInstalled(int exitCode, string? output) =>
        exitCode == WingetOutputParser.ExitNoInstalledPackageFound
        || (exitCode != 0 && !IsRefusal(exitCode) && WingetOutputParser.IsNotInstalledOutput(output));

    /// <summary>
    /// The order in which an install works through the configured ids. Pure apart from the two delegates, so the order
    /// is testable. First the plain upgrade for every candidate, in order: the first answer that is not a refusal
    /// (success, "already up to date", or a real failure) is final. Only when every candidate refused are the fallbacks
    /// (take-over, "install --force") applied to the refusals in candidate order; <paramref name="fallback"/> returns
    /// null when none applies to a refusal. The first fallback that succeeds, or that fails for a reason other than
    /// "no applicable installer" (i.e. it ran something), is final. The Firefox case this exists for: the MSIX build is
    /// listed under both Mozilla.Firefox and Mozilla.Firefox.MSIX; the first id refuses and its fallback would run the
    /// machine-wide nullsoft installer, while the second id upgrades the MSIX silently. An id winget does not list as
    /// installed (<see cref="UpgradeOutcome.IdNotInstalled"/>) is skipped and gets no fallback; when no id got further
    /// than that, the first such answer is the result.
    /// </summary>
    internal static async Task<InstallResult> RunCandidatesAsync(IReadOnlyList<string> candidates,
        Func<string, Task<UpgradeOutcome>> upgrade, Func<UpgradeRefusal, Task<InstallResult?>> fallback)
    {
        var refusals = new List<UpgradeRefusal>();
        InstallResult? firstNotInstalled = null;
        foreach (var id in candidates)
        {
            var outcome = await upgrade(id).ConfigureAwait(false);
            if (outcome.IdNotInstalled) { firstNotInstalled ??= outcome.Result; continue; }
            if (outcome.Refusal is null) return outcome.Result!;
            refusals.Add(outcome.Refusal);
        }
        if (refusals.Count == 0)
            return firstNotInstalled ?? InstallResult.Fail("No winget id to upgrade.");

        InstallResult? firstNoInstaller = null;
        foreach (var refusal in refusals)
        {
            var result = await fallback(refusal).ConfigureAwait(false);
            if (result is null) continue;
            if (result.Success || result.ExitCode != WingetOutputParser.ExitNoApplicableInstaller) return result;
            firstNoInstaller ??= result;
        }
        return firstNoInstaller ?? refusals[^1].PlainResult;
    }

    private async Task<UpgradeOutcome> UpgradeOneAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, IProgress<string>? progress, CancellationToken ct)
    {
        var args = new StringBuilder()
            .Append("upgrade --id ").Append(Quote(wingetId))
            .Append(" --exact");
        AppendSource(args, sourceName);
        args.Append(" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
            .Append(ScopeArgument(context));
        AppendExtra(args, update.WingetExtraArgs ?? app.WingetExtraArgs);
        AppendExtra(args, _options.WingetGlobalArgs);

        // The executable and the identity matter when an install misbehaves: a fleet device showed UAC prompts in the
        // user's session for installs the SYSTEM service had started, which a session-0 process cannot cause by itself.
        _logger.LogInformation("{AppId}: upgrading '{WingetId}' via winget ({Context} scope) using {Winget} as {Identity}.",
            app.AppId, wingetId, context.Context, winget, Native.ImpersonationGuard.DescribeCurrentIdentity());
        progress?.Report($"Upgrading {(string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName)} via winget...");

        var lastProgressLine = string.Empty;
        var run = await RunInstallProcessAsync(winget, args.ToString(), _options.InstallTimeout,
            line =>
            {
                var condensed = Condense(line);
                if (condensed is null || condensed == lastProgressLine) return;
                lastProgressLine = condensed;
                progress?.Report(condensed);
            },
            context, ct).ConfigureAwait(false);

        if (!run.Started) return UpgradeOutcome.Final(InstallResult.Fail(run.StartFailure!));
        if (run.TimedOut)
            return UpgradeOutcome.Final(InstallResult.Fail($"winget upgrade timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.", -1));

        var result = InterpretWingetExitCode(run.ExitCode, run);

        // Read the version winget now reports so the caller can record what actually got installed - and so a
        // "no applicable upgrade" / "success" answer can be checked against reality. With several installs of the package
        // registered this is the highest one, as in the scan: the update is verified once the expected version (or a
        // newer one) is among them, however many older releases stay registered next to it.
        string? newVersion = null;
        try
        {
            var after = await ListAsync(winget, app with { WingetId = wingetId, WingetSourceName = sourceName }, context, _options.CheckTimeout, ct).ConfigureAwait(false);
            newVersion = string.IsNullOrWhiteSpace(after.Row?.Version) ? null : after.Row!.Version;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{AppId}: could not re-read the installed version after the winget upgrade.", app.AppId);
        }

        var stillOutdated = newVersion is not null && !VersionComparer.IsUnknown(newVersion) && !string.IsNullOrWhiteSpace(update.AvailableVersion)
                            && VersionComparer.Compare(newVersion, update.AvailableVersion) < 0;

        // A refusal while the product is still outdated (or its version is unknown) is not final: the caller tries the
        // other configured ids first and only then the fallbacks (take-over, "install --force"), see RunCandidatesAsync.
        if (IsRefusal(run.ExitCode) && (stillOutdated || newVersion is null))
        {
            InstallResult plain;
            if (run.ExitCode == WingetOutputParser.ExitNoApplicableUpgrade)
            {
                var message = $"winget found no applicable upgrade for '{wingetId}' ({context.Context} scope); installed version is still {newVersion ?? update.InstalledVersion ?? "unknown"}, expected {update.AvailableVersion}. " +
                              "The installed build (e.g. Store/MSIX) may not match this package id, or the package is managed elsewhere.";
                plain = InstallResult.Fail(message, run.ExitCode);
            }
            else
            {
                plain = result;
            }
            _logger.LogInformation("{AppId}: winget refused to upgrade '{WingetId}' (0x{Code:X8}); installed version {Version}.",
                app.AppId, wingetId, run.ExitCode, newVersion ?? "could not be read");
            return UpgradeOutcome.Refused(new UpgradeRefusal(wingetId, run.ExitCode, newVersion, plain));
        }

        if (IsIdNotInstalled(run.ExitCode, run.CombinedOutput))
        {
            var message = $"winget does not list '{wingetId}' as installed in the {context.Context} scope (exit 0x{run.ExitCode:X8}).";
            _logger.LogInformation("{AppId}: {Message} Trying the next id, if any.", app.AppId, message);
            return UpgradeOutcome.NotInstalled(InstallResult.Fail(message, run.ExitCode));
        }

        if (run.ExitCode == WingetOutputParser.ExitNoApplicableUpgrade)
        {
            _logger.LogInformation("{AppId}: already up to date ({Version}).", app.AppId, newVersion);
            return UpgradeOutcome.Final(InstallResult.Ok("already up to date", run.ExitCode) with { InstalledVersion = newVersion });
        }

        if (!result.Success)
        {
            _logger.LogError("{AppId}: winget upgrade failed - {Message}", app.AppId, result.Message);
            return UpgradeOutcome.Final(result);
        }

        if (stillOutdated && !result.RebootRequired)
        {
            var message = $"winget reported success (exit 0x{run.ExitCode:X8}) but '{wingetId}' is still at {newVersion}, expected {update.AvailableVersion}.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return UpgradeOutcome.Final(InstallResult.Fail(message, run.ExitCode));
        }

        _logger.LogInformation("{AppId}: winget upgrade finished (exit 0x{Hex}){Reboot}; installed version now {Version}.", app.AppId, run.ExitCode.ToString("X8"),
            result.RebootRequired ? ", reboot required" : string.Empty, newVersion ?? update.AvailableVersion);
        return UpgradeOutcome.Final(result with { InstalledVersion = newVersion ?? update.AvailableVersion });
    }

    /// <summary>
    /// Whether a plain <c>winget upgrade</c> exit code is a refusal that sends the install on to the next configured id
    /// (and, once every id refused, to the fallbacks). Pure, so the rule is testable: "no applicable upgrade"
    /// (0x8A15002B) and "the install technology is different" (0x8A15008E). Anything else - success, a real failure,
    /// "no applicable installer" - is not a refusal of this kind.
    /// </summary>
    internal static bool IsRefusal(int exitCode) =>
        exitCode is WingetOutputParser.ExitNoApplicableUpgrade or WingetOutputParser.ExitUpdateInstallTechnologyMismatch;

    /// <summary>
    /// How many <c>winget install</c> attempts the user context makes (see <see cref="UserContextInstallFilter"/>).
    /// </summary>
    internal const int UserContextInstallAttempts = 2;

    /// <summary>
    /// The installer filter for the <paramref name="attempt"/>-th <c>winget install</c> in a user's session. Pure, so
    /// the rule is testable. Never empty: without a filter winget takes whatever installer the manifest offers, and a
    /// machine-wide installer then asks for elevation in the user's session (the Firefox incident). First
    /// <c>--scope user</c>; when winget answers "no applicable installer" (0x8A150010), <c>--installer-type msix</c>
    /// with no scope, because an MSIX installer declares no scope, is always per user and never elevates. When that is
    /// refused as well the agent gives up without running anything.
    /// </summary>
    internal static string UserContextInstallFilter(int attempt) => attempt switch
    {
        0 => " --scope user",
        1 => " --installer-type msix",
        _ => throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "The user context makes two install attempts."),
    };

    /// <summary>
    /// Whether winget said that no installer matches the filters (exit 0x8A150010, or its "No applicable installer
    /// found" message). Pure, so the rule is testable. The message counts on its own: <c>winget show</c> (1.30) prints
    /// it under "Installer:" and still exits 0.
    /// </summary>
    internal static bool IsNoApplicableInstaller(int exitCode, string? output) =>
        exitCode == WingetOutputParser.ExitNoApplicableInstaller
        || (output?.Contains(WingetOutputParser.NoApplicableInstallerMarker, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>The failure reported when a package only ships a machine-wide installer and the agent runs as the user.</summary>
    internal static string MachineOnlyMessage(string wingetId, bool portableSkipped = false) => portableSkipped
        ? $"'{wingetId}' has no per-user or MSIX installer in winget that updates the installed copy: its user-scope installer is a portable package, which would install a second copy; the agent does not start installers that need administrator rights in a user's session."
        : $"'{wingetId}' has no per-user or MSIX installer in winget, only a machine-wide one; the agent does not start installers that need administrator rights in a user's session.";

    /// <summary>
    /// Whether a refused upgrade is retried as "winget install --force" (see <see cref="ReinstallAsync"/>). Pure, so
    /// the rule is testable: only in a user's own session, only when winget said the upgrade is not applicable or the
    /// installed package type does not match the installer, and only while the product is in fact still outdated.
    /// </summary>
    internal static bool ShouldReinstall(int exitCode, ExecutionContextInfo context, bool stillOutdated) =>
        !context.IsSystem && stillOutdated &&
        exitCode is WingetOutputParser.ExitNoApplicableUpgrade or WingetOutputParser.ExitUpdateInstallTechnologyMismatch;

    /// <summary>
    /// Whether a refused upgrade is answered with the configured take-over (see <see cref="ReplaceAsync"/>). Pure, so
    /// the rule is testable: only for the technology-mismatch refusal, only when the application opted in with
    /// <c>WingetReplaceOnMismatch</c>, and only while the product is in fact still outdated. Unlike
    /// <see cref="ShouldReinstall"/> this is allowed in both contexts: a product migrated from one installer
    /// technology to another (MSI to exe, exe to MSIX) is just as common machine-wide as per user.
    /// </summary>
    internal static bool ShouldReplace(int exitCode, bool replaceEnabled, bool stillOutdated) =>
        replaceEnabled && stillOutdated && exitCode == WingetOutputParser.ExitUpdateInstallTechnologyMismatch;

    /// <summary>
    /// Whether the take-over's uninstall is retried with <c>--all-versions</c>. Pure, so the rule is testable: only
    /// for winget's "multiple versions of this package are installed" refusal (0x8A150016), which asks for either
    /// <c>--version</c> or <c>--all-versions</c>. Replacing whatever is on the device is the whole point of the
    /// take-over, so removing every registered version is the intended answer, not picking one of them.
    /// </summary>
    internal static bool ShouldUninstallAllVersions(int exitCode) =>
        exitCode == WingetOutputParser.ExitMultiplePackagesFound;

    /// <summary>
    /// Whether the take-over's removal step got far enough to install over it. Pure, so the rule is testable. The exit
    /// code alone is not enough: with several registrations winget reports the whole run as failed when any one of them
    /// resists (0x8A150066, APPINSTALLER_CLI_ERROR_MULTIPLE_UNINSTALL_FAILED) even though the registration the upgrade
    /// complained about is gone. So the listing afterwards decides: nothing installed any more, or an installed version
    /// that differs from the one we set out to replace, both mean the old install is out of the way. Anything else -
    /// including a listing that could not be read - counts as a failure.
    /// </summary>
    internal static bool RemovalSucceeded(int exitCode, bool notInstalledAfter, string? versionAfter, string? versionBefore)
    {
        if (exitCode == 0) return true;
        if (notInstalledAfter) return true;
        if (string.IsNullOrWhiteSpace(versionAfter) || string.IsNullOrWhiteSpace(versionBefore)) return false;
        return !string.Equals(VersionComparer.Normalize(versionAfter), VersionComparer.Normalize(versionBefore), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The way out when winget refuses to upgrade a per-user install it did not create. "winget upgrade" compares the
    /// manifest's installers with where and how the product is installed: a manifest that only declares a
    /// machine-scope installer (Perplexity.Comet) can never match a per-user install, and an installer type that
    /// differs from the technology the ARP entry was created with (Microsoft.BingWallpaper: per-user MSI, exe wrapper
    /// in the manifest) cannot either. Those filters are built from the installed package's metadata and no argument
    /// relaxes them. "winget install --force" skips the installed-package lookup altogether, so no such filter exists.
    /// It must never run unfiltered in a user's session, though: without a --scope argument winget takes the
    /// manifest's installer as is, and when that is a machine-wide installer it requests elevation and Windows shows a
    /// UAC prompt in the user's session (Mozilla.Firefox, whose manifest only has a machine-scope nullsoft installer).
    /// So the install runs with <c>--scope user</c> first and <c>--installer-type msix</c> second (see
    /// <see cref="UserContextInstallFilter"/>), skipping a user-scope installer that is a portable package; when neither
    /// matches an installer the attempt fails without running anything. Success is still judged by the version winget reports afterwards, never by the exit code. User
    /// context only (<see cref="ShouldReinstall"/>): as LocalSystem a user-scope installer would land in SYSTEM's own
    /// profile.
    /// </summary>
    private async Task<InstallResult> ReinstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, string refusal, int refusalExitCode, IProgress<string>? progress, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;
        _logger.LogInformation("{AppId}: winget refused to upgrade '{WingetId}' ({Refusal}, 0x{Code:X8}); installing {Version} over it with 'winget install --force' in the {Context} context.",
            app.AppId, wingetId, refusal, refusalExitCode, update.AvailableVersion, context.Context);
        progress?.Report($"Installing {name} {update.AvailableVersion} over the current version via winget...");

        var prefix = $"winget could not upgrade '{wingetId}' ({context.Context} scope): {refusal}.";
        var step = await RunInstallStepAsync(app, update, context, winget, wingetId, sourceName, force: true, systemScope: false, progress, ct).ConfigureAwait(false);
        var run = step.Run;
        if (!run.Started) return InstallResult.Fail(run.StartFailure!);
        if (run.TimedOut)
            return InstallResult.Fail($"winget install timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.", -1);
        if (step.NoPerUserInstaller)
        {
            var message = $"{prefix} {MachineOnlyMessage(wingetId, step.PortableSkipped)}";
            // winget truly offers nothing for the user (not merely a portable copy): when the installed copy is an MSIX
            // package, the tray can ask the SYSTEM service to install the package for all users (see HandOverToSystemAsync).
            if (!context.IsSystem && !step.PortableSkipped && _options.SystemInstallHandOver is { } handOver)
                return await HandOverToSystemAsync(app, update, context, winget, wingetId, sourceName, message, handOver, progress, ct).ConfigureAwait(false);
            _logger.LogWarning("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, WingetOutputParser.ExitNoApplicableInstaller);
        }

        var result = InterpretWingetExitCode(run.ExitCode, run);
        var newVersion = await ReadVersionAfterAsync(app, context, winget, wingetId, sourceName, ct).ConfigureAwait(false);
        var stillOutdated = IsStillOutdated(newVersion, update.AvailableVersion);

        if (!result.Success || run.ExitCode == WingetOutputParser.ExitNoApplicableUpgrade)
        {
            var message = $"{prefix} Installing over it with 'winget install --force' failed as well: {result.Message}";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, run.ExitCode);
        }
        if (stillOutdated && !result.RebootRequired)
        {
            var message = $"{prefix} 'winget install --force' reported success (exit 0x{run.ExitCode:X8}) but the installed version is still {newVersion}, expected {update.AvailableVersion}.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, run.ExitCode);
        }

        _logger.LogInformation("{AppId}: 'winget install --force' finished (exit 0x{Hex}){Reboot}; installed version now {Version}.", app.AppId, run.ExitCode.ToString("X8"),
            result.RebootRequired ? ", reboot required" : string.Empty, newVersion ?? update.AvailableVersion);
        return result with { InstalledVersion = newVersion ?? update.AvailableVersion };
    }

    /// <summary>How much of the service's reply text goes into the result: that text is the only evidence that reaches the cloud.</summary>
    internal const int HandOverEvidenceLimit = 900;

    /// <summary>1 once this provider instance (= one install) has handed a package to the service; a second candidate id never does it again.</summary>
    private int _handedOver;

    /// <summary>
    /// The way out when the user context has no installer it may run and the installed copy is an MSIX package
    /// (Microsoft.WindowsAppRuntime.1.6 on DESKTOP-V1CDE3I: registered for the user as an MSIX framework package, while
    /// the manifest only has an exe that needs elevation and declares no Scope, so neither <c>--scope user</c> nor
    /// <c>--installer-type msix</c> selects it). The SYSTEM service never sees such a package - MSIX registrations are
    /// per user - so without this nothing could update it. Guarded: only when <c>winget list --details</c> says every
    /// installed row of the id is an MSIX package of one package family (<see cref="EvaluateHandOver"/>), and only once
    /// per install. The tray then asks the service (<see cref="ProviderOptions.SystemInstallHandOver"/>) to run an
    /// unscoped <c>winget install</c> as SYSTEM, where no UAC prompt is possible (<see cref="InstallForAllUsersAsync"/>),
    /// and judges the outcome by the user's own version afterwards (<see cref="JudgeHandOver"/>): whether the user's
    /// registration moves at once or only at the next sign-in is not known yet, so both are handled, and the service's
    /// evidence (package registrations before and after) always goes into the result message, which is what reaches
    /// the cloud. Every failure message starts with the old "machine-wide installer only" text.
    /// </summary>
    private async Task<InstallResult> HandOverToSystemAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, string machineOnly,
        Func<SystemInstallHandOverRequest, CancellationToken, Task<SystemInstallHandOverReply?>> handOver, IProgress<string>? progress, CancellationToken ct)
    {
        InstallResult NotHandedOver(string reason)
        {
            _logger.LogInformation("{AppId}: not asking the AppMonitor service to install '{WingetId}' for all users: {Reason}.", app.AppId, wingetId, reason);
            _logger.LogWarning("{AppId}: {Message}", app.AppId, machineOnly);
            return InstallResult.Fail(machineOnly, WingetOutputParser.ExitNoApplicableInstaller);
        }

        if (Volatile.Read(ref _handedOver) == 1) return NotHandedOver("this install already handed a package to the service");

        var before = await ListDetailsAsync(winget, wingetId, sourceName, context, ct).ConfigureAwait(false);
        if (before.Error is not null) return NotHandedOver($"'winget list --details' failed ({before.Error})");
        var (eligible, family, why) = EvaluateHandOver(before.Rows, wingetId);
        if (!eligible || family is null) return NotHandedOver(why);
        if (Interlocked.Exchange(ref _handedOver, 1) == 1) return NotHandedOver("this install already handed a package to the service");

        var ownRows = before.Rows.Where(r => string.Equals(r.Id, wingetId, StringComparison.OrdinalIgnoreCase)).ToList();
        var userPackageBefore = WingetOutputParser.HighestMsixPackageVersion(ownRows);
        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;
        _logger.LogInformation("{AppId}: '{WingetId}' is installed for this user as the MSIX package {Family} ({PackageVersion}) and winget only offers an installer that needs administrator rights; asking the AppMonitor service to install {Version} for all users.",
            app.AppId, wingetId, family, userPackageBefore ?? "version unknown", update.AvailableVersion);
        progress?.Report($"Installing {name} {update.AvailableVersion} for all users via the AppMonitor service...");

        SystemInstallHandOverReply? reply;
        try
        {
            reply = await handOver(new SystemInstallHandOverRequest(update.Key, wingetId, family, userPackageBefore), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{AppId}: the hand-over of '{WingetId}' to the AppMonitor service failed.", app.AppId, wingetId);
            reply = null;
        }
        var evidence = Bound(reply?.Message, HandOverEvidenceLimit);
        _logger.LogInformation("{AppId}: the AppMonitor service answered the hand-over of '{WingetId}': {Answer}", app.AppId, wingetId,
            reply is null ? "no answer" : $"ok={reply.Ok}, exit 0x{reply.ExitCode:X8}, machine package {reply.MachinePackageVersion ?? "unknown"}: {evidence}");

        string? userVersion = null;
        string? userPackageAfter = null;
        if (reply is { Ok: true })
        {
            userVersion = await ReadVersionAfterAsync(app, context, winget, wingetId, sourceName, ct).ConfigureAwait(false);
            var after = await ListDetailsAsync(winget, wingetId, sourceName, context, ct).ConfigureAwait(false);
            userPackageAfter = WingetOutputParser.HighestMsixPackageVersion(after.Rows.Where(r => string.Equals(r.Id, wingetId, StringComparison.OrdinalIgnoreCase)));
        }

        var verdict = JudgeHandOver(reply, userVersion, update.AvailableVersion, userPackageBefore);
        var service = evidence is null ? string.Empty : $" Service: {evidence}";
        var packages = $"package {userPackageBefore ?? "unknown"} -> {userPackageAfter ?? "unknown"}";
        switch (verdict)
        {
            case HandOverVerdict.Installed:
            {
                var message = $"{name} {update.AvailableVersion} was installed for all users by the AppMonitor service; this user's copy is now {userVersion} ({packages}).{service}";
                _logger.LogInformation("{AppId}: {Message}", app.AppId, message);
                return InstallResult.Ok(message, reply!.ExitCode, InterpretWingetExitCode(reply.ExitCode).RebootRequired) with { InstalledVersion = userVersion };
            }
            case HandOverVerdict.PendingSignIn:
            {
                // The package is on the device, but this user's registration still points at the old one. Reported as
                // installed with a restart pending (a restart signs the user in again), so the old reading is not
                // taken for a failed install, and the message says plainly what is still to happen.
                var current = userVersion ?? update.InstalledVersion;
                var message = $"Installed {name} {update.AvailableVersion} for all users via the AppMonitor service (package {reply!.MachinePackageVersion}); your copy is still {current ?? "the old version"} ({packages}) and switches over at your next sign-in or restart.{service}";
                _logger.LogInformation("{AppId}: {Message}", app.AppId, message);
                return InstallResult.Ok(message, reply.ExitCode, reboot: true) with { InstalledVersion = current };
            }
            case HandOverVerdict.NoNewerPackage:
            {
                var message = $"{machineOnly} The AppMonitor service installed it for all users and reported success (exit 0x{reply!.ExitCode:X8}), but no newer {family} package is on the device (user {packages}, machine {reply.MachinePackageVersion ?? "unknown"}).{service}";
                _logger.LogError("{AppId}: {Message}", app.AppId, message);
                return InstallResult.Fail(message, reply.ExitCode == 0 ? -1 : reply.ExitCode);
            }
            default:
            {
                var reason = reply is null
                    ? "it did not answer (an older service, a disconnect or a timeout)"
                    : (evidence ?? $"exit 0x{reply.ExitCode:X8}").TrimEnd('.');
                var message = $"{machineOnly} The AppMonitor service could not install it for all users: {reason}.";
                _logger.LogWarning("{AppId}: {Message}", app.AppId, message);
                // Nothing ran when there was no answer (or the service refused): "no applicable installer", as before.
                var code = reply is null || reply.ExitCode == 0 ? WingetOutputParser.ExitNoApplicableInstaller : reply.ExitCode;
                return InstallResult.Fail(message, code);
            }
        }
    }

    /// <summary>
    /// Whether a package winget offers no user-context installer for may be handed to the service, and the package
    /// family it is installed as. Pure, so the rule is testable: only when <c>winget list --details</c> shows at least one
    /// installed row of <paramref name="wingetId"/>, every such row is an MSIX package (installer category "msix") and
    /// all of them belong to one package family. Anything else - a classic exe/MSI install, rows without a family,
    /// several families - keeps the "machine-wide installer only" failure; the reason says why.
    /// </summary>
    internal static (bool HandOver, string? PackageFamilyName, string Reason) EvaluateHandOver(IReadOnlyList<WingetInstalledDetails> rows, string wingetId)
    {
        var own = rows.Where(r => string.Equals(r.Id, wingetId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count == 0) return (false, null, "'winget list --details' shows no installed row for it in this user's scope");
        var notMsix = own.Where(r => !string.Equals(r.InstallerCategory, "msix", StringComparison.OrdinalIgnoreCase)).ToList();
        if (notMsix.Count > 0)
            return (false, null, $"the installed copy is not an MSIX package (installer category {string.Join(", ", notMsix.Select(r => r.InstallerCategory ?? "unknown").Distinct(StringComparer.OrdinalIgnoreCase))})");
        var families = own.Select(r => r.PackageFamilyName?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (families.Any(string.IsNullOrWhiteSpace)) return (false, null, "winget names no package family for the installed MSIX package");
        if (families.Count > 1) return (false, null, $"the installed rows belong to {families.Count} package families ({string.Join(", ", families)})");
        return (true, families[0], "every installed row is an MSIX package of one family");
    }

    /// <summary>The bool form of <see cref="EvaluateHandOver"/>.</summary>
    internal static bool ShouldHandOverToSystem(IReadOnlyList<WingetInstalledDetails> rows, string wingetId, out string? packageFamilyName)
    {
        var (handOver, family, _) = EvaluateHandOver(rows, wingetId);
        packageFamilyName = family;
        return handOver;
    }

    /// <summary>
    /// How the user context judges a hand-over to the service. Pure, so the rule is testable.
    /// <see cref="HandOverVerdict.Failed"/>: no reply (timeout, an older service, a disconnect) or the service's install
    /// failed or was refused. <see cref="HandOverVerdict.Installed"/>: the user's own version, read afterwards, is known
    /// and at least the expected one. <see cref="HandOverVerdict.PendingSignIn"/>: the user's copy is still old, but the
    /// service found a package of the family on the machine that is newer than the user's package before the hand-over
    /// (both MSIX package versions, e.g. 6000.519.329.0 against 6000.519.297.0; winget's own version numbers are another
    /// scheme and never compared with them). <see cref="HandOverVerdict.NoNewerPackage"/> otherwise: success was reported
    /// but nothing shows that it took - never report success when the version did not move.
    /// </summary>
    internal static HandOverVerdict JudgeHandOver(SystemInstallHandOverReply? reply, string? userVersionAfter, string? availableVersion, string? userPackageVersionBefore)
    {
        if (reply is null || !reply.Ok) return HandOverVerdict.Failed;
        if (!string.IsNullOrWhiteSpace(userVersionAfter) && !VersionComparer.IsUnknown(userVersionAfter)
            && !string.IsNullOrWhiteSpace(availableVersion) && VersionComparer.Compare(userVersionAfter, availableVersion) >= 0)
            return HandOverVerdict.Installed;
        if (!string.IsNullOrWhiteSpace(reply.MachinePackageVersion) && !string.IsNullOrWhiteSpace(userPackageVersionBefore)
            && VersionComparer.IsNewer(reply.MachinePackageVersion, userPackageVersionBefore))
            return HandOverVerdict.PendingSignIn;
        return HandOverVerdict.NoNewerPackage;
    }

    /// <summary>Trims <paramref name="text"/> to at most <paramref name="max"/> characters (with an ellipsis), or null when it is empty.</summary>
    public static string? Bound(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    /// <summary>What <c>winget list --details</c> says about one id: its installed rows, or the error that kept it from answering.</summary>
    private sealed record DetailsLookup(IReadOnlyList<WingetInstalledDetails> Rows, string? Error);

    /// <summary>
    /// <c>winget list --id X --exact [--source S] --details --accept-source-agreements --disable-interactivity</c> in this
    /// context's scope: one record per installed registration (see <see cref="WingetOutputParser.ParseListDetails"/>).
    /// "Not installed" is no rows, not an error.
    /// </summary>
    private async Task<DetailsLookup> ListDetailsAsync(string winget, string wingetId, string sourceName, ExecutionContextInfo context, CancellationToken ct)
    {
        try
        {
            var args = new StringBuilder()
                .Append("list --id ").Append(Quote(wingetId))
                .Append(" --exact");
            AppendSource(args, sourceName);
            args.Append(" --details --accept-source-agreements --disable-interactivity")
                .Append(ScopeArgument(context));
            AppendExtra(args, _options.WingetGlobalArgs);

            var run = await RunLookupAsync(winget, args.ToString(), _options.CheckTimeout, context, ct).ConfigureAwait(false);
            if (!run.Started) return new DetailsLookup([], run.StartFailure);
            if (run.TimedOut) return new DetailsLookup([], $"timed out after {_options.CheckTimeout.TotalSeconds:0} seconds");
            if (run.ExitCode == WingetOutputParser.ExitNoInstalledPackageFound || WingetOutputParser.IsNotInstalledOutput(run.CombinedOutput))
                return new DetailsLookup([], null);
            var rows = WingetOutputParser.ParseListDetails(run.StandardOutput);
            if (rows.Count == 0 && run.ExitCode != 0) return new DetailsLookup([], $"exit 0x{run.ExitCode:X8}: {run.LastLines(2)}");
            return new DetailsLookup(rows, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "winget list --details for '{WingetId}' failed.", wingetId);
            return new DetailsLookup([], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The service's half of the hand-over (see <see cref="HandOverToSystemAsync"/>): installs <paramref name="wingetId"/>
    /// as LocalSystem with an unscoped <c>winget install --id X --exact [--source S] --silent
    /// --accept-package-agreements --accept-source-agreements --disable-interactivity</c> (plus the configured extra and
    /// global arguments). Unscoped on purpose: a manifest that declares no Scope (Microsoft.WindowsAppRuntime.1.6) answers
    /// "no applicable installer" to <c>--scope machine</c> as much as to <c>--scope user</c>. As SYSTEM, in session 0, the
    /// installer runs elevated and cannot show a prompt to anyone. "Already installed" and "no newer version" are not
    /// errors here (<see cref="InterpretAllUsersInstallExit"/>): the caller's before/after check of the package
    /// registrations decides what happened. Must only be called by the service, for an id it has checked belongs to the
    /// update.
    /// </summary>
    public async Task<InstallResult> InstallForAllUsersAsync(AppPolicy app, PendingUpdate update, string wingetId, IProgress<string>? progress, CancellationToken ct)
    {
        if (!_options.WingetEnabled)
            return InstallResult.Fail("winget disabled by configuration");
        var context = ExecutionContextInfo.System;
        var winget = LocateWinget(context);
        if (winget is null)
            return InstallResult.Fail("winget.exe was not found on this machine.");

        var sourceName = FirstNonEmpty(update.WingetSourceName, app.WingetSourceName, "winget");
        _logger.LogInformation("{AppId}: installing '{WingetId}' {Version} for all users with an unscoped 'winget install' using {Winget} as {Identity}.",
            app.AppId, wingetId, update.AvailableVersion, winget, Native.ImpersonationGuard.DescribeCurrentIdentity());

        var step = await RunInstallStepAsync(app, update, context, winget, wingetId, sourceName, force: false, systemScope: false, progress, ct).ConfigureAwait(false);
        var run = step.Run;
        if (!run.Started) return InstallResult.Fail(run.StartFailure!);
        if (run.TimedOut)
            return InstallResult.Fail($"winget install timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.", -1);

        var result = InterpretAllUsersInstallExit(run.ExitCode, run);
        if (result.Success)
            _logger.LogInformation("{AppId}: 'winget install' of '{WingetId}' for all users finished (exit 0x{Hex}): {Message}", app.AppId, wingetId, run.ExitCode.ToString("X8"), result.Message);
        else
            _logger.LogError("{AppId}: 'winget install' of '{WingetId}' for all users failed: {Message}", app.AppId, wingetId, result.Message);
        return result;
    }

    /// <summary>
    /// The outcome of the service's unscoped <c>winget install</c> (see <see cref="InstallForAllUsersAsync"/>). Pure, so
    /// the rule is testable. "No applicable upgrade" (0x8A15002B) and "package already installed" (0x8A150061) mean
    /// winget found nothing newer to install, which is not an error: the package registrations decide. "No applicable
    /// installer" means winget has none even unscoped. Everything else as <see cref="InterpretWingetExitCode"/>.
    /// </summary>
    internal static InstallResult InterpretAllUsersInstallExit(int exitCode, ProcessRunResult? run = null)
    {
        if (exitCode is WingetOutputParser.ExitNoApplicableUpgrade or WingetOutputParser.ExitPackageAlreadyInstalled)
            return InstallResult.Ok($"winget found nothing newer to install (exit 0x{exitCode:X8}).", exitCode);
        if (exitCode != 0 && IsNoApplicableInstaller(exitCode, run?.CombinedOutput))
            return InstallResult.Fail($"winget has no installer for the package even without a scope filter (exit 0x{exitCode:X8}).", exitCode);
        var result = InterpretWingetExitCode(exitCode, run);
        if (result.Success) return result;
        var detail = run?.LastLines() ?? string.Empty;
        return InstallResult.Fail($"winget install failed with exit code {exitCode} (0x{exitCode:X8})." +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Output: {detail}"), exitCode);
    }

    /// <summary>
    /// The opt-in take-over for winget's "the install technology is different" refusal (0x8A15008E): the product was
    /// installed with a technology the current manifest no longer uses (Oh My Posh, installed with the old Inno exe
    /// while the manifest now ships an MSIX). No argument makes <c>winget upgrade</c> cross that line and
    /// <c>winget install --force</c> would only add the new package next to the old one, so this does literally what
    /// winget's own message asks: uninstall the current package, then install the new one. The order is therefore
    /// fixed - uninstall first - and that is exactly the risk the setting opts into: between the two steps the
    /// application is not installed, and if the install fails it stays that way until the next scan. When several
    /// versions of the package are registered and winget refuses to choose (0x8A150016) the uninstall is run once more
    /// with <c>--all-versions</c>, because replacing every registered version is what the take-over is for. Whether the
    /// removal worked is judged by what winget lists afterwards rather than by its exit code, because winget reports the
    /// whole multi-uninstall as failed (0x8A150066) when one registration resists even though the rest are gone. Success
    /// is judged only by the version winget reports afterwards, never by the exit code. When the listing still shows the
    /// old version the resisting registration is looked at: a Windows Installer entry whose product Windows Installer
    /// does not have any more can only be cleared by deleting the Uninstall key, which the agent then does itself (see
    /// <see cref="RemoveStaleMsiRegistrations"/>) before it asks winget once more. In the user context the take-over
    /// first checks with <c>winget show</c> that a per-user or MSIX installer exists and does not uninstall anything
    /// when there is none, because the install step never starts a machine-wide installer there.
    /// </summary>
    private async Task<InstallResult> ReplaceAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, int refusalExitCode, IProgress<string>? progress, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;
        var prefix = $"winget could not upgrade '{wingetId}' ({context.Context} scope): the installed package's technology differs from the manifest's installer.";

        // ---- 0. user context: never remove what cannot be put back without administrator rights. The install step
        // below only accepts a per-user or MSIX installer, so check that one exists before anything is uninstalled.
        if (!context.IsSystem)
        {
            var availability = await CheckPerUserInstallerAsync(app, winget, wingetId, sourceName, context, ct).ConfigureAwait(false);
            if (availability != InstallerAvailability.Available)
            {
                var message = availability == InstallerAvailability.None
                    ? $"{prefix} {MachineOnlyMessage(wingetId)} The current install was left in place."
                    : $"{prefix} Whether winget has a per-user or MSIX installer for '{wingetId}' could not be confirmed, so the current install was left in place (the take-over never removes what it may not be able to reinstall).";
                _logger.LogWarning("{AppId}: {Message}", app.AppId, message);
                return InstallResult.Fail(message, availability == InstallerAvailability.None ? WingetOutputParser.ExitNoApplicableInstaller : -1);
            }
        }

        // ---- 1. remove the current install.
        _logger.LogInformation("{AppId}: winget refused to upgrade '{WingetId}' (install technology mismatch, 0x{Code:X8}) and WingetReplaceOnMismatch is set; removing the current install in the {Context} context.",
            app.AppId, wingetId, refusalExitCode, context.Context);
        progress?.Report($"Removing the current install of {name} via winget...");

        var uninstall = await RunUninstallAsync(winget, wingetId, sourceName, context, allVersions: false, progress, ct).ConfigureAwait(false);
        var retriedAllVersions = false;
        if (uninstall.Started && !uninstall.TimedOut && ShouldUninstallAllVersions(uninstall.ExitCode))
        {
            _logger.LogInformation("{AppId}: winget found more than one installed version of '{WingetId}' (0x{Code:X8}) and will not choose; removing every registered version with --all-versions.",
                app.AppId, wingetId, uninstall.ExitCode);
            progress?.Report($"Removing all installed versions of {name} via winget...");
            retriedAllVersions = true;
            uninstall = await RunUninstallAsync(winget, wingetId, sourceName, context, allVersions: true, progress, ct).ConfigureAwait(false);
        }

        var step = retriedAllVersions ? "winget uninstall --all-versions" : "winget uninstall";
        string? uninstallFailure = null;
        if (!uninstall.Started) uninstallFailure = uninstall.StartFailure;
        else if (uninstall.TimedOut) uninstallFailure = $"{step} timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.";
        else if (uninstall.ExitCode != 0) uninstallFailure = $"{step} exited with 0x{uninstall.ExitCode:X8}. {uninstall.LastLines()}".TrimEnd();

        if (uninstall.Started && !uninstall.TimedOut && uninstallFailure is not null)
        {
            // The exit code alone does not settle it: with several registrations winget fails the whole run when one of
            // them resists, so ask what is actually installed now.
            var after = await ReadInstalledAfterAsync(app, context, winget, wingetId, sourceName, ct).ConfigureAwait(false);
            var versionAfter = string.IsNullOrWhiteSpace(after?.Row?.Version) ? null : after!.Row!.Version;
            var notInstalledAfter = after?.NotInstalled == true;
            var listing = after is null
                ? "the installed state could not be re-read afterwards"
                : notInstalledAfter ? "winget no longer lists the package"
                : versionAfter is not null ? $"winget still lists version {versionAfter} in the {context.Context} scope"
                : after.Error is not null ? $"the installed state could not be re-read afterwards ({after.Error})"
                : "winget's listing afterwards was inconclusive";

            if (RemovalSucceeded(uninstall.ExitCode, notInstalledAfter, versionAfter, update.InstalledVersion))
            {
                var kind = uninstall.ExitCode == WingetOutputParser.ExitMultipleUninstallFailed
                    ? "multiple uninstall failed"
                    : "a non-zero exit code";
                _logger.LogWarning("{AppId}: {Step} for '{WingetId}' reported {Kind} (0x{Code:X8}), but {Listing}; continuing with the install.",
                    app.AppId, step, wingetId, kind, uninstall.ExitCode, listing);
                uninstallFailure = null;
            }
            else
            {
                var stillListed = $"{uninstallFailure} Afterwards {listing}";
                uninstallFailure = $"{stillListed}.";

                // winget still lists a version. The usual reason winget can do nothing about is a stale Windows
                // Installer registration: the ARP key is there, but Windows Installer no longer has the product, so
                // its MsiExec.exe /I{GUID} uninstall string answers 1605 for ever. Clear such a leftover ourselves and
                // ask winget again.
                if (versionAfter is not null)
                {
                    var cleanup = RemoveStaleMsiRegistrations(app, context, after?.Row?.Name);
                    if (cleanup.Removed > 0)
                    {
                        var cleaned = await ReadInstalledAfterAsync(app, context, winget, wingetId, sourceName, ct).ConfigureAwait(false);
                        var cleanedVersion = string.IsNullOrWhiteSpace(cleaned?.Row?.Version) ? null : cleaned!.Row!.Version;
                        var cleanedNotInstalled = cleaned?.NotInstalled == true;
                        if (RemovalSucceeded(uninstall.ExitCode, cleanedNotInstalled, cleanedVersion, update.InstalledVersion))
                        {
                            _logger.LogWarning("{AppId}: {Step} for '{WingetId}' left version {Version} listed, but {Count} stale Windows Installer registration(s) removed; continuing with the install.",
                                app.AppId, step, wingetId, versionAfter, cleanup.Removed);
                            uninstallFailure = null;
                        }
                        else
                        {
                            var afterCleanup = cleaned is null ? "the installed state could not be re-read"
                                : cleanedVersion is not null ? $"winget still lists version {cleanedVersion}"
                                : "winget's listing was inconclusive";
                            uninstallFailure = $"{stillListed}; {cleanup.Removed} stale Windows Installer registration(s) were removed, but {afterCleanup}.";
                        }
                    }
                    else
                    {
                        uninstallFailure = $"{stillListed}, and no stale Windows Installer registration found for it.";
                    }

                    if (uninstallFailure is not null && cleanup.Error is not null)
                        uninstallFailure = $"{uninstallFailure} {cleanup.Error}";
                }
            }
        }

        if (uninstallFailure is not null)
        {
            var retryNote = retriedAllVersions
                ? " More than one version of the package was registered and the retry with --all-versions failed as well:"
                : string.Empty;
            var message = $"{prefix} Removing the current install with winget failed:{retryNote} {uninstallFailure}";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, uninstall.Started && !uninstall.TimedOut ? uninstall.ExitCode : -1);
        }

        // ---- 2. install the new package: --scope machine as LocalSystem; in the user context --scope user first and
        // --installer-type msix second (an MSIX installer declares no scope, so "--scope user" alone would miss it),
        // never unfiltered, so a machine-wide installer is never started in the user's session.
        _logger.LogInformation("{AppId}: the previous install of '{WingetId}' was removed; installing {Version} with winget in the {Context} context.",
            app.AppId, wingetId, update.AvailableVersion, context.Context);
        progress?.Report($"Installing {name} {update.AvailableVersion} via winget...");

        var installStep = await RunInstallStepAsync(app, update, context, winget, wingetId, sourceName, force: false, systemScope: true, progress, ct).ConfigureAwait(false);
        var install = installStep.Run;
        if (install.Started && !install.TimedOut && installStep.NoPerUserInstaller)
        {
            // The pre-check found an installer, so this should not happen. Deliberately not reported as "no applicable
            // installer": the old install is gone, so no further fallback may run for another id.
            var message = $"{prefix} The previous install was removed; {MachineOnlyMessage(wingetId)} The application may now be missing on this device.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, -1);
        }
        if (!install.Started)
        {
            var message = $"{prefix} The previous install was removed; installing {update.AvailableVersion} with winget failed: {install.StartFailure}. The application may now be missing on this device.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, -1);
        }
        if (install.TimedOut)
        {
            var message = $"{prefix} The previous install was removed; installing {update.AvailableVersion} with winget failed: winget install timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated. The application may now be missing on this device.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, -1);
        }

        var result = InterpretWingetExitCode(install.ExitCode, install);
        var newVersion = await ReadVersionAfterAsync(app, context, winget, wingetId, sourceName, ct).ConfigureAwait(false);
        var stillOutdated = IsStillOutdated(newVersion, update.AvailableVersion);

        // Stricter than the other paths: the old install is gone, so "winget says success but no version can be read"
        // is not good enough to record a success - the next scan settles what is really on the device.
        if (!result.Success || newVersion is null || (stillOutdated && !result.RebootRequired))
        {
            var detail = !result.Success ? result.Message
                : newVersion is null ? $"winget reported success (exit 0x{install.ExitCode:X8}) but the installed version could not be verified"
                : $"winget reported success (exit 0x{install.ExitCode:X8}) but the installed version is still {newVersion}, expected {update.AvailableVersion}";
            var message = $"{prefix} The previous install was removed; installing {update.AvailableVersion} with winget failed: {detail}. The application may now be missing on this device.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, install.ExitCode);
        }

        _logger.LogInformation("{AppId}: replaced '{WingetId}' via winget uninstall + install (exit 0x{Hex}){Reboot}; installed version now {Version}.", app.AppId, wingetId,
            install.ExitCode.ToString("X8"), result.RebootRequired ? ", reboot required" : string.Empty, newVersion ?? update.AvailableVersion);
        return result with { InstalledVersion = newVersion ?? update.AvailableVersion };
    }

    /// <summary>
    /// The install of a product winget does not correlate with any package (<see cref="PendingUpdate.WingetUncorrelated"/>):
    /// <c>winget upgrade --id</c> would answer "not installed", so this runs <c>winget install --id X --exact</c> for the
    /// package the scan took the available version from, and the vendor's installer (an MSI major upgrade, an exe)
    /// upgrades the product in place. The scope is always given, never left to the manifest: <c>--scope machine</c> as
    /// LocalSystem, <c>--scope user</c> in the user's session (only that - no MSIX or unscoped attempt, which would put a
    /// second, differently packaged copy next to the installed one; a portable user-scope installer is refused for the
    /// same reason). winget still cannot see the product afterwards, so its listing cannot verify anything: success is
    /// judged only by the registry - the same detection the scan applies (<see cref="InstalledAppScanner.Match"/>) - and
    /// only when the version there reached the expected one, whatever winget's exit code said.
    /// </summary>
    private async Task<InstallResult> InstallUncorrelatedAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, IProgress<string>? progress, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;
        var prefix = $"winget does not correlate the installed {name} with a package ({context.Context} scope), so 'winget upgrade' cannot see it.";

        _logger.LogInformation("{AppId}: winget does not correlate the installed product ({Installed}, registry), so 'winget upgrade' cannot see it; installing {Available} with 'winget install --id {WingetId}' in the {Context} context using {Winget} as {Identity}.",
            app.AppId, update.InstalledVersion ?? "unknown", update.AvailableVersion, wingetId, context.Context, winget, Native.ImpersonationGuard.DescribeCurrentIdentity());

        var filter = ScopeArgument(context);
        if (!context.IsSystem)
        {
            // Never a machine-wide installer in the user's session, and never a portable copy next to the real one.
            var show = await RunShowAsync(winget, wingetId, sourceName, context, filter, ct).ConfigureAwait(false);
            if (show.Started && !show.TimedOut)
            {
                var portable = show.ExitCode == 0 && IsPortable(ParseInstallerType(show.CombinedOutput));
                if (portable || IsNoApplicableInstaller(show.ExitCode, show.CombinedOutput))
                {
                    var message = $"{prefix} '{wingetId}' cannot be installed over it: {MachineOnlyMessage(wingetId, portable)}";
                    _logger.LogWarning("{AppId}: {Message}", app.AppId, message);
                    return InstallResult.Fail(message, WingetOutputParser.ExitNoApplicableInstaller);
                }
            }
        }

        progress?.Report($"Installing {name} {update.AvailableVersion} via winget...");
        var args = new StringBuilder()
            .Append("install --id ").Append(Quote(wingetId))
            .Append(" --exact");
        AppendSource(args, sourceName);
        args.Append(" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
            .Append(filter);
        AppendExtra(args, update.WingetExtraArgs ?? app.WingetExtraArgs);
        AppendExtra(args, _options.WingetGlobalArgs);

        var run = await RunInstallProcessAsync(winget, args.ToString(), _options.InstallTimeout, ProgressSink(progress), context, ct).ConfigureAwait(false);
        if (!run.Started) return InstallResult.Fail(run.StartFailure!);
        if (run.TimedOut)
            return InstallResult.Fail($"winget install timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.", -1);
        if (!context.IsSystem && IsNoApplicableInstaller(run.ExitCode, run.CombinedOutput))
        {
            var message = $"{prefix} '{wingetId}' cannot be installed over it: {MachineOnlyMessage(wingetId)}";
            _logger.LogWarning("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, WingetOutputParser.ExitNoApplicableInstaller);
        }

        var result = InterpretWingetExitCode(run.ExitCode, run);
        var after = ReadInstalledAfterFromRegistry(app, context);
        var registryVersion = string.IsNullOrWhiteSpace(after?.DisplayVersion) ? null : after!.DisplayVersion!.Trim();

        if (!UncorrelatedInstallReached(registryVersion, update.AvailableVersion))
        {
            var found = after is null
                ? "the registry no longer shows a matching Uninstall entry"
                : $"the registry still shows {registryVersion ?? "no version"}";
            var outcome = result.Success ? $"reported success (exit 0x{run.ExitCode:X8})" : $"failed (exit 0x{run.ExitCode:X8}): {result.Message}";
            var message = $"{prefix} 'winget install --id {wingetId}' {outcome}, but {found}, expected {update.AvailableVersion}.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, run.ExitCode);
        }

        if (!result.Success)
            _logger.LogWarning("{AppId}: 'winget install' for '{WingetId}' exited with 0x{Code:X8}, but the registry shows {Version} now; the update took.",
                app.AppId, wingetId, run.ExitCode, registryVersion);
        _logger.LogInformation("{AppId}: 'winget install' for '{WingetId}' finished (exit 0x{Hex}){Reboot}; the registry shows {Version} now.",
            app.AppId, wingetId, run.ExitCode.ToString("X8"), result.RebootRequired ? ", reboot required" : string.Empty, registryVersion);
        var ok = result.Success ? result : InstallResult.Ok($"Installed; winget exited with 0x{run.ExitCode:X8}, but the registry shows the new version.", run.ExitCode);
        return ok with { InstalledVersion = registryVersion };
    }

    /// <summary>
    /// Whether the install of an uncorrelated product took: the version the registry shows afterwards is known and at
    /// least the expected one. Pure, so the rule is testable. Anything else - no entry, no version, "Unknown", a lower
    /// version, no expected version - is a failure: never report success when the version did not move.
    /// </summary>
    internal static bool UncorrelatedInstallReached(string? registryVersion, string? expected) =>
        !string.IsNullOrWhiteSpace(registryVersion) && !VersionComparer.IsUnknown(registryVersion)
        && !string.IsNullOrWhiteSpace(expected) && !VersionComparer.IsUnknown(expected)
        && VersionComparer.Compare(registryVersion, expected) >= 0;

    /// <summary>The application's registry entry after an install, through <see cref="RegistryReader"/> when a test set one.</summary>
    private InstalledApp? ReadInstalledAfterFromRegistry(AppPolicy app, ExecutionContextInfo context)
    {
        try
        {
            return RegistryReader is { } reader ? reader(app, context) : ReadInstalledFromRegistry(app, context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{AppId}: the Uninstall registry could not be read after the install.", app.AppId);
            return null;
        }
    }

    /// <summary>
    /// The application's best match among the Uninstall entries of this context, read afresh - the detection the scan
    /// feeds the check with (<see cref="UpdateChecker"/>: the context's inventory, then <see cref="InstalledAppScanner.Match"/>,
    /// whose first entry has the highest version). LocalSystem reads the machine hive, the tray its own user's.
    /// </summary>
    private static InstalledApp? ReadInstalledFromRegistry(AppPolicy app, ExecutionContextInfo context)
    {
        var sid = context.IsSystem ? null : context.UserSid ?? CurrentUserSid();
        var scanner = new InstalledAppScanner(NullLogger<InstalledAppScanner>.Instance);
        var inventory = scanner.Scan(includeMachine: context.IsSystem, includeUsers: !context.IsSystem, onlyUserSid: sid);
        var scoped = inventory.Where(e => e.Context == context.Context
            && (context.IsSystem || sid is null || string.Equals(e.UserSid, sid, StringComparison.OrdinalIgnoreCase)));
        return InstalledAppScanner.Match(app, scoped).FirstOrDefault();
    }

    /// <summary>
    /// The installer type <c>winget show</c> reports for the installer it selected ("Installer Type: portable (zip)",
    /// "Installer Type: msix"), or null when the output has no such line. Pure, so the parsing is testable. Only the
    /// line that starts with "Installer Type:" counts, not "Nested Installer Type:".
    /// </summary>
    internal static string? ParseInstallerType(string? showOutput)
    {
        if (string.IsNullOrWhiteSpace(showOutput)) return null;
        const string label = "Installer Type:";
        foreach (var raw in showOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[label.Length..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    /// <summary>
    /// Whether an installer type (see <see cref="ParseInstallerType"/>) is a portable package ("portable",
    /// "portable (zip)"). Pure, so the rule is testable. A portable package never updates an installed copy: winget
    /// unpacks a second, separate copy into the user's profile.
    /// </summary>
    internal static bool IsPortable(string? installerType) =>
        installerType?.Contains("portable", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>Outcome of <see cref="RunInstallStepAsync"/>: the last winget run, whether it found no permitted installer, and whether a portable user-scope installer was skipped.</summary>
    private sealed record InstallStepResult(ProcessRunResult Run, bool NoPerUserInstaller, bool PortableSkipped = false);

    /// <summary>
    /// Runs <c>winget install</c> for the fallbacks. As LocalSystem once, with <c>--scope machine</c> when
    /// <paramref name="systemScope"/> is set. In the user context with <see cref="UserContextInstallFilter"/>: up to
    /// two attempts, the second only after winget answered "no applicable installer" to the first, so an unfiltered
    /// (possibly machine-wide, elevating) install is never started. Before the <c>--scope user</c> attempt,
    /// <c>winget show</c> is asked which installer that filter selects: when it is a portable package (Notepad++ and VLC
    /// only offer a portable zip in user scope) the attempt is skipped, because it would install a second, portable copy
    /// instead of updating the real one. <c>NoPerUserInstaller</c> is set when the last attempt was refused as "no
    /// applicable installer", i.e. winget ran nothing.
    /// </summary>
    private async Task<InstallStepResult> RunInstallStepAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, bool force, bool systemScope, IProgress<string>? progress, CancellationToken ct)
    {
        var attempts = context.IsSystem ? 1 : UserContextInstallAttempts;
        var firstAttempt = 0;
        if (!context.IsSystem)
        {
            var show = await RunShowAsync(winget, wingetId, sourceName, context, UserContextInstallFilter(0), ct).ConfigureAwait(false);
            var installerType = show.Started && !show.TimedOut && show.ExitCode == 0 ? ParseInstallerType(show.CombinedOutput) : null;
            if (IsPortable(installerType))
            {
                _logger.LogInformation("{AppId}: winget's user-scope installer for '{WingetId}' is portable ('{Type}') and would install a second copy instead of updating this one; skipping '{Filter}' and trying '{Next}'.",
                    app.AppId, wingetId, installerType, UserContextInstallFilter(0).Trim(), UserContextInstallFilter(1).Trim());
                firstAttempt = 1;
            }
        }
        for (var attempt = firstAttempt; ; attempt++)
        {
            var filter = context.IsSystem ? (systemScope ? ScopeArgument(context) : string.Empty) : UserContextInstallFilter(attempt);
            var args = new StringBuilder()
                .Append("install --id ").Append(Quote(wingetId))
                .Append(" --exact");
            if (force) args.Append(" --force");
            AppendSource(args, sourceName);
            args.Append(" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
                .Append(filter);
            AppendExtra(args, update.WingetExtraArgs ?? app.WingetExtraArgs);
            AppendExtra(args, _options.WingetGlobalArgs);

            var run = await RunInstallProcessAsync(winget, args.ToString(), _options.InstallTimeout, ProgressSink(progress), context, ct).ConfigureAwait(false);
            var noInstaller = !context.IsSystem && run.Started && !run.TimedOut && IsNoApplicableInstaller(run.ExitCode, run.CombinedOutput);
            if (noInstaller && attempt + 1 < attempts)
            {
                _logger.LogInformation("{AppId}: winget has no installer for '{WingetId}' matching '{Filter}' (0x{Code:X8}); trying '{Next}'.",
                    app.AppId, wingetId, filter.Trim(), run.ExitCode, UserContextInstallFilter(attempt + 1).Trim());
                continue;
            }
            return new InstallStepResult(run, noInstaller, firstAttempt > 0);
        }
    }

    /// <summary>
    /// One <c>winget show</c> for the package with an installer filter (<see cref="UserContextInstallFilter"/>): exits 0
    /// with the selected installer's details, or reports "no applicable installer".
    /// </summary>
    private async Task<ProcessRunResult> RunShowAsync(string winget, string wingetId, string sourceName, ExecutionContextInfo context, string filter, CancellationToken ct)
    {
        var args = new StringBuilder()
            .Append("show --id ").Append(Quote(wingetId))
            .Append(" --exact");
        AppendSource(args, sourceName);
        args.Append(filter).Append(" --accept-source-agreements --disable-interactivity");
        AppendExtra(args, _options.WingetGlobalArgs);

        return await RunInstallProcessAsync(winget, args.ToString(), _options.CheckTimeout, null, context, ct).ConfigureAwait(false);
    }

    /// <summary>Whether winget offers an installer the user context may run (see <see cref="CheckPerUserInstallerAsync"/>).</summary>
    private enum InstallerAvailability { Available, None, Unknown }

    /// <summary>
    /// Asks <c>winget show</c> whether the package has a per-user or an MSIX installer, with the same two filters the
    /// install step uses. Available when either answers with a manifest that is not a portable package; None when both
    /// say "no applicable installer" or name a portable package (which the install step never runs, see
    /// <see cref="RunInstallStepAsync"/>); Unknown otherwise (winget failed for another reason), in which case the
    /// take-over does not uninstall either.
    /// </summary>
    private async Task<InstallerAvailability> CheckPerUserInstallerAsync(AppPolicy app, string winget, string wingetId, string sourceName,
        ExecutionContextInfo context, CancellationToken ct)
    {
        var none = 0;
        for (var attempt = 0; attempt < UserContextInstallAttempts; attempt++)
        {
            var filter = UserContextInstallFilter(attempt);
            var run = await RunShowAsync(winget, wingetId, sourceName, context, filter, ct).ConfigureAwait(false);
            if (!run.Started || run.TimedOut)
            {
                _logger.LogWarning("{AppId}: 'winget show' for '{WingetId}' ({Filter}) could not be run: {Reason}", app.AppId, wingetId, filter.Trim(),
                    run.StartFailure ?? "timed out");
                continue;
            }
            if (IsNoApplicableInstaller(run.ExitCode, run.CombinedOutput)) { none++; continue; }
            if (run.ExitCode == 0 && IsPortable(ParseInstallerType(run.CombinedOutput)))
            {
                _logger.LogInformation("{AppId}: winget's installer for '{WingetId}' matching '{Filter}' is portable ('{Type}'); it would install a second copy, so it does not count as a per-user installer.",
                    app.AppId, wingetId, filter.Trim(), ParseInstallerType(run.CombinedOutput));
                none++;
                continue;
            }
            if (run.ExitCode == 0)
            {
                _logger.LogDebug("{AppId}: winget has an installer for '{WingetId}' matching '{Filter}'.", app.AppId, wingetId, filter.Trim());
                return InstallerAvailability.Available;
            }
            _logger.LogWarning("{AppId}: 'winget show' for '{WingetId}' ({Filter}) exited with 0x{Code:X8}: {Tail}", app.AppId, wingetId, filter.Trim(),
                run.ExitCode, run.LastLines(2));
        }
        return none == UserContextInstallAttempts ? InstallerAvailability.None : InstallerAvailability.Unknown;
    }

    /// <summary>
    /// One <c>winget uninstall</c> attempt for the take-over. No --purge (user data is not ours to delete) and no
    /// WingetExtraArgs (those are upgrade arguments); the scope is the one the upgrade used, so we only ever touch our
    /// own context. With <paramref name="allVersions"/> every registered version of the package is removed, which is
    /// the answer to winget refusing to choose between several of them.
    /// </summary>
    private async Task<ProcessRunResult> RunUninstallAsync(string winget, string wingetId, string sourceName, ExecutionContextInfo context,
        bool allVersions, IProgress<string>? progress, CancellationToken ct)
    {
        var args = new StringBuilder()
            .Append("uninstall --id ").Append(Quote(wingetId))
            .Append(" --exact");
        AppendSource(args, sourceName);
        args.Append(" --silent --disable-interactivity")
            .Append(ScopeArgument(context));
        if (allVersions) args.Append(" --all-versions");
        AppendExtra(args, _options.WingetGlobalArgs);

        return await RunInstallProcessAsync(winget, args.ToString(), _options.InstallTimeout, ProgressSink(progress), context, ct).ConfigureAwait(false);
    }

    /// <summary>Outcome of the stale-registration cleanup: how many keys were deleted, and why one could not be.</summary>
    private sealed record StaleCleanup(int Removed, string? Error);

    /// <summary>
    /// Deletes Uninstall entries that are stale Windows Installer registrations of this package, so that winget's
    /// listing can catch up. Strictly bounded: only the Uninstall branch of the scope the take-over runs in (HKLM for
    /// the service, the calling user's own hive for the tray), only entries whose display name is the one winget lists
    /// for the id (or matches the policy's display-name regex), only entries with an MSI product code, and only when
    /// <c>MsiQueryProductState</c> says that product is not installed. Everything deleted is logged at Warning.
    /// </summary>
    private StaleCleanup RemoveStaleMsiRegistrations(AppPolicy app, ExecutionContextInfo context, string? wingetName)
    {
        string? sid = null;
        if (!context.IsSystem)
        {
            sid = context.UserSid ?? CurrentUserSid();
            if (string.IsNullOrWhiteSpace(sid))
                return new StaleCleanup(0, "The current user's SID could not be determined, so per-user Uninstall entries were not inspected.");
        }

        IReadOnlyList<InstalledApp> inventory;
        try
        {
            var scanner = new InstalledAppScanner(NullLogger<InstalledAppScanner>.Instance);
            inventory = scanner.Scan(includeMachine: context.IsSystem, includeUsers: !context.IsSystem, onlyUserSid: sid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{AppId}: the Uninstall registry could not be read while looking for stale Windows Installer registrations.", app.AppId);
            return new StaleCleanup(0, $"The Uninstall registry could not be read: {ex.Message}");
        }

        var stale = inventory
            .Where(e => e.Context == context.Context
                && (context.IsSystem || string.Equals(e.UserSid, sid, StringComparison.OrdinalIgnoreCase))
                && WindowsInstallerState.IsStaleMsiRegistration(e, wingetName, app, WindowsInstallerState.QueryProductState))
            .ToList();
        if (stale.Count == 0) return new StaleCleanup(0, null);

        var removed = 0;
        string? error = null;
        foreach (var entry in stale)
        {
            WindowsInstallerState.TryGetProductCode(entry, out var productCode);
            var state = WindowsInstallerState.QueryProductState(productCode);
            try
            {
                DeleteUninstallKey(entry.RegistryKeyPath);
                removed++;
                _logger.LogWarning("{AppId}: deleted the stale Windows Installer registration {Key} ('{Name}' {Version}, product code {ProductCode}, MsiQueryProductState = {State}); Windows Installer does not have that product, so winget could never remove the entry.",
                    app.AppId, entry.RegistryKeyPath, entry.DisplayName, entry.DisplayVersion ?? "no version", productCode, state);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{AppId}: the stale Windows Installer registration {Key} could not be deleted.", app.AppId, entry.RegistryKeyPath);
                var note = $"The stale Windows Installer registration {entry.RegistryKeyPath} could not be deleted: {ex.Message}";
                error = error is null ? note : $"{error} {note}";
            }
        }
        return new StaleCleanup(removed, error);
    }

    /// <summary>The SID of the process's own user, or null when it cannot be read.</summary>
    private static string? CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    /// <summary>
    /// Deletes one Uninstall key (the whole subkey tree) addressed by the scanner's <c>RegistryKeyPath</c>. Refuses
    /// anything that is not an Uninstall key under a hive the scanner reads, so a malformed path can never take out
    /// something else.
    /// </summary>
    private static void DeleteUninstallKey(string registryKeyPath)
    {
        if (string.IsNullOrWhiteSpace(registryKeyPath))
            throw new InvalidOperationException("the registration has no registry path");

        var firstSeparator = registryKeyPath.IndexOf('\\');
        if (firstSeparator <= 0) throw new InvalidOperationException($"'{registryKeyPath}' is not a registry path");
        var rootName = registryKeyPath[..firstSeparator];
        var path = registryKeyPath[(firstSeparator + 1)..];

        if (!path.Contains(@"\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{registryKeyPath}' is not an Uninstall key");

        var root = rootName.ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKEY_USERS" => Registry.Users,
            "HKEY_CURRENT_USER" => Registry.CurrentUser,
            _ => null,
        };
        if (root is null) throw new InvalidOperationException($"'{rootName}' is not a hive this agent writes to");

        var lastSeparator = path.LastIndexOf('\\');
        var parentPath = path[..lastSeparator];
        var leaf = path[(lastSeparator + 1)..];
        using var parent = root.OpenSubKey(parentPath, writable: true)
            ?? throw new InvalidOperationException($"'{rootName}\\{parentPath}' could not be opened for writing");
        parent.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
    }

    /// <summary>Forwards condensed, de-duplicated winget output lines to the progress reporter.</summary>
    private static Action<string>? ProgressSink(IProgress<string>? progress)
    {
        if (progress is null) return null;
        var last = string.Empty;
        return line =>
        {
            var condensed = Condense(line);
            if (condensed is null || condensed == last) return;
            last = condensed;
            progress.Report(condensed);
        };
    }

    /// <summary>
    /// What winget lists for the package in this context after a run, or null when the lookup itself failed (which the
    /// caller treats as "unknown"). Used by the take-over to judge the removal by the installed state rather than by
    /// the uninstall's exit code.
    /// </summary>
    private async Task<ListLookup?> ReadInstalledAfterAsync(AppPolicy app, ExecutionContextInfo context, string winget, string wingetId, string sourceName, CancellationToken ct)
    {
        try
        {
            return await ListAsync(winget, app with { WingetId = wingetId, WingetSourceName = sourceName }, context, _options.CheckTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{AppId}: could not re-read the installed state after the winget run.", app.AppId);
            return null;
        }
    }

    /// <summary>The version winget reports for the package after an install attempt, or null when it cannot be read.</summary>
    private async Task<string?> ReadVersionAfterAsync(AppPolicy app, ExecutionContextInfo context, string winget, string wingetId, string sourceName, CancellationToken ct)
    {
        try
        {
            var after = await ListAsync(winget, app with { WingetId = wingetId, WingetSourceName = sourceName }, context, _options.CheckTimeout, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(after.Row?.Version) ? null : after.Row!.Version;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{AppId}: could not re-read the installed version after the winget run.", app.AppId);
            return null;
        }
    }

    /// <summary>Whether a version read after an install is known and still below the expected one. Pure, so the rule is testable.</summary>
    internal static bool IsStillOutdated(string? installed, string? available) =>
        installed is not null && !VersionComparer.IsUnknown(installed) && !string.IsNullOrWhiteSpace(available)
        && VersionComparer.Compare(installed, available) < 0;

    /// <summary>Result of a <c>winget list</c> lookup for a single package.</summary>
    private sealed record ListLookup(WingetRow? Row, bool NotInstalled, string? Error);

    /// <summary>
    /// The unfiltered per-scope <c>winget list</c> of one scan: the parsed rows, winget's raw output (for
    /// <see cref="ClassifyInListing"/>), and whether it is <paramref name="Complete"/>, i.e. winget ran to the end and
    /// answered with a table (or with "no installed package"), so that an id missing from it is not installed. An
    /// incomplete listing still serves the per-id lookup's own fallbacks, as it always did.
    /// </summary>
    private sealed record FullListing(IReadOnlyList<WingetRow> Rows, string Output, bool Complete);

    /// <summary>What the scan's winget snapshot cost in one scope, for the summary line (see <see cref="LogScanSummary"/>).</summary>
    private sealed class ScanStats
    {
        public TimeSpan ListElapsed;
        public int ListRows;
        public bool ListComplete;
        public TimeSpan UpgradeElapsed;
        public int UpgradeRows;
        public int Processes;
        public int PerAppLookups;
        public int CappedLookups;
    }

    private readonly Dictionary<InstallContext, FullListing> _fullListCache = new();
    private readonly SemaphoreSlim _fullListGate = new(1, 1);
    private readonly ConcurrentDictionary<InstallContext, ScanStats> _stats = new();

    private ScanStats StatsFor(ExecutionContextInfo context) => _stats.GetOrAdd(context.Context, _ => new ScanStats());

    /// <summary>
    /// Reads the scan's snapshot for <paramref name="context"/> before the applications are checked: the full per-scope
    /// listing and the upgrade listing of every winget source the applications use. Both are cached for the lifetime of
    /// this provider instance, so <see cref="CheckAsync"/> then answers every application from memory. Never throws
    /// except for cancellation: a listing that cannot be read leaves the per-app lookups to the checks.
    /// </summary>
    public async Task PrepareScanAsync(IReadOnlyList<AppPolicy> apps, ExecutionContextInfo context, CancellationToken ct)
    {
        if (!_options.WingetEnabled) return;
        var wingetApps = apps.Where(a => a.Source == UpdateSource.Winget && !string.IsNullOrWhiteSpace(a.WingetId)).ToList();
        if (wingetApps.Count == 0) return;
        var winget = LocateWinget(context);
        if (winget is null) return;

        await GetFullListAsync(winget, context, ct).ConfigureAwait(false);
        foreach (var source in wingetApps.GroupBy(a => SourceKey(a.WingetSourceName)).Select(g => g.First().WingetSourceName))
            await GetUpgradeListingAsync(winget, source, context, ct).ConfigureAwait(false);
    }

    /// <summary>Logs what the scan's winget snapshot cost in <paramref name="context"/>: processes, listing times, matching time and per-app fallbacks.</summary>
    public void LogScanSummary(ExecutionContextInfo context, int apps, TimeSpan matching)
    {
        if (!_stats.TryGetValue(context.Context, out var s)) return;
        if (s.ListComplete)
            _logger.LogInformation("winget snapshot ({Context}): list {ListSeconds:F1}s ({ListRows} rows), upgrade {UpgradeSeconds:F1}s ({UpgradeRows} rows); {Apps} app(s) matched in {MatchMs:F0} ms; {Fallbacks} per-app fallback(s); {Processes} winget process(es)",
                context.Context, s.ListElapsed.TotalSeconds, s.ListRows, s.UpgradeElapsed.TotalSeconds, s.UpgradeRows, apps, matching.TotalMilliseconds, s.PerAppLookups, s.Processes);
        else
            _logger.LogInformation("winget snapshot ({Context}): the full listing could not be used, so {Apps} app(s) were checked with per-app lookups in {MatchSeconds:F1}s; upgrade {UpgradeSeconds:F1}s ({UpgradeRows} rows); {Processes} winget process(es)",
                context.Context, apps, matching.TotalSeconds, s.UpgradeElapsed.TotalSeconds, s.UpgradeRows, s.Processes);
    }

    /// <summary>
    /// Whether one configured id is installed in this scope, and its row. Normally answered from the scan's full listing
    /// (see <see cref="ClassifyInListing"/>) without starting winget. winget's own per-id lookup
    /// (<see cref="LookupByIdAsync"/>) runs when the listing is not complete (then for every id of the scan) and when
    /// the listing cannot settle this one id, the latter at most <see cref="MaxPerAppLookups"/> times per scan and scope;
    /// beyond that the listing's answer stands, exactly as the per-id lookup's own fallback would take it.
    /// </summary>
    private async Task<ListLookup> LookupIdAsync(string winget, AppPolicy app, string id, FullListing listing, ExecutionContextInfo context, CancellationToken ct)
    {
        if (listing.Complete && !ForcePerAppLookups)
        {
            var (kind, row, reason) = ClassifyInListing(listing.Rows, listing.Output, id);
            if (kind == ListingMatch.Found) return new ListLookup(row, false, null);
            if (kind == ListingMatch.Absent) return new ListLookup(null, true, null);

            var stats = StatsFor(context);
            if (Interlocked.Increment(ref stats.PerAppLookups) > MaxPerAppLookups)
            {
                Interlocked.Decrement(ref stats.PerAppLookups);
                if (Interlocked.Increment(ref stats.CappedLookups) == 1)
                    _logger.LogWarning("The full winget listing ({Context} scope) could not settle more than {Max} package id(s) in this scan; the listing's answer stands for the rest (first: '{WingetId}', {Reason}).",
                        context.Context, MaxPerAppLookups, id, reason);
                var fromList = FindInFullList(listing.Rows, id);
                return fromList is null ? new ListLookup(null, true, null) : new ListLookup(fromList, false, null);
            }
            _logger.LogDebug("{AppId}: the full {Context}-scope listing cannot settle '{WingetId}' ({Reason}); asking winget about this id.", app.AppId, context.Context, id, reason);
        }
        return await LookupByIdAsync(winget, app, id, listing, context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// winget's own lookup of one id (<c>winget list --id X --exact</c>), with the full listing as its fallback: winget
    /// (observed with 1.30) sometimes answers "no installed package" to the id lookup although the unfiltered listing
    /// shows the very same id (e.g. Microsoft.PowerShell installed via MSI).
    /// </summary>
    private async Task<ListLookup> LookupByIdAsync(string winget, AppPolicy app, string id, FullListing listing, ExecutionContextInfo context, CancellationToken ct)
    {
        var attempt = await ListAsync(winget, app with { WingetId = id }, context, _options.CheckTimeout, ct).ConfigureAwait(false);
        if (!attempt.NotInstalled) return attempt;

        var fromFullList = FindInFullList(listing.Rows, id);
        if (fromFullList is null) return attempt;
        _logger.LogDebug("{AppId}: '{WingetId}' not returned by the id lookup but present in the full {Context}-scope listing ({Version}).", app.AppId, id, context.Context, fromFullList.Version);
        return new ListLookup(fromFullList, false, null);
    }

    /// <summary>Looks a package id up in the unfiltered per-scope listing: the per-id lookup's fallback.</summary>
    private static WingetRow? FindInFullList(IReadOnlyList<WingetRow> rows, string id)
    {
        var row = WingetOutputParser.FindById(rows, id);
        // Only trust rows that carry a real source; sourceless rows are unmapped ARP entries.
        if (row is null || string.IsNullOrWhiteSpace(row.Source)) return null;
        var idMatches = row.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || row.Id.EndsWith(WingetOutputParser.Ellipsis);
        return idMatches ? row : null;
    }

    /// <summary>What a complete full listing says about one configured id (see <see cref="ClassifyInListing"/>).</summary>
    internal enum ListingMatch { Found, Absent, Unsettled }

    /// <summary>
    /// What winget's complete full per-scope listing says about one configured id, as winget's per-id lookup would
    /// answer it. Pure, so the rule is testable. <see cref="ListingMatch.Found"/>: rows with exactly this id
    /// (case-insensitive), every one with a source; several rows (one per installed version) are combined
    /// (<see cref="WingetOutputParser.CombineInstalls"/>), as the per-id lookup combines them.
    /// <see cref="ListingMatch.Absent"/>: winget's output does not mention the id at all, so it is not installed in
    /// this scope. <see cref="ListingMatch.Unsettled"/> otherwise, and winget's per-id lookup decides: a row with the id
    /// but no source (the per-id lookup's answer depends on its <c>--source</c> filter), a truncated (ellipsised) id cell
    /// the id starts with ("Microsoft.DotNet.DesktopRunti…" can stand for the 8 and the 9 runtime alike), or the id
    /// standing in winget's output as a word of its own although no parsed row carries it (a row the table parser could
    /// not split cleanly).
    /// </summary>
    internal static (ListingMatch Kind, WingetRow? Row, string? Reason) ClassifyInListing(IReadOnlyList<WingetRow> rows, string? output, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (ListingMatch.Absent, null, null);

        var exact = rows.Where(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            return exact.All(r => !string.IsNullOrWhiteSpace(r.Source))
                ? (ListingMatch.Found, WingetOutputParser.CombineInstalls(exact), null)
                : (ListingMatch.Unsettled, null, "listed without a source");
        }
        if (rows.Any(r => r.Id.EndsWith(WingetOutputParser.Ellipsis) && id.StartsWith(r.Id.TrimEnd(WingetOutputParser.Ellipsis), StringComparison.OrdinalIgnoreCase)))
            return (ListingMatch.Unsettled, null, "its id cell is truncated in the listing");
        if (ContainsIdToken(output, id))
            return (ListingMatch.Unsettled, null, "in winget's output but in no parsed row");
        return (ListingMatch.Absent, null, null);
    }

    /// <summary>
    /// Whether <paramref name="id"/> stands in <paramref name="output"/> as a word of its own (case-insensitive, with
    /// whitespace or the text's start/end on both sides), so "Google.Chrome" is not found in "Google.Chrome.EXE". Pure.
    /// </summary>
    internal static bool ContainsIdToken(string? output, string id)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrWhiteSpace(id)) return false;
        for (var i = output.IndexOf(id, StringComparison.OrdinalIgnoreCase); i >= 0; i = output.IndexOf(id, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + id.Length;
            if ((i == 0 || char.IsWhiteSpace(output[i - 1])) && (end == output.Length || char.IsWhiteSpace(output[end])))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The installed-state fallback of the generic id resolution: the row of winget's full per-scope listing whose Name
    /// passes the app's identity rule (<see cref="InstalledAppScanner.NameMatches"/>). Pure, so the rule is testable.
    /// Only rows winget can manage count: a real source, a complete (not ellipsised) id, and no pseudo id
    /// (<c>MSIX\…</c>, <c>ARP\…</c>). Several matches are ordered by <see cref="PickByName"/>.
    /// </summary>
    internal static WingetRow? ResolveByName(IReadOnlyList<WingetRow> listRows, AppPolicy app, IReadOnlyList<string> configuredIds) =>
        PickByName(listRows.Where(r => !string.IsNullOrWhiteSpace(r.Source)), app, configuredIds);

    /// <summary>
    /// The best row among those whose Name passes the app's identity rule, or null. Pure. Rows with a truncated
    /// (ellipsised) or pseudo id are never picked. Tie-break, in order: (1) the id sharing the most leading
    /// dot-separated segments with the first configured id (case-insensitive; <c>Google.Chrome.EXE</c> shares two with
    /// <c>Google.Chrome</c>, an unrelated vendor's id none); (2) the highest installed version; (3) the id in ordinal,
    /// case-insensitive order, so the choice is deterministic. When the chosen id has several rows (one per installed
    /// version) they are combined (<see cref="WingetOutputParser.CombineInstalls"/>).
    /// </summary>
    internal static WingetRow? PickByName(IEnumerable<WingetRow> rows, AppPolicy app, IReadOnlyList<string> configuredIds)
    {
        var hint = configuredIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty;
        var matches = rows.Where(r => IsResolvableId(r.Id) && InstalledAppScanner.NameMatches(app, r.Name)).ToList();
        var best = matches
            .OrderByDescending(r => SharedIdSegments(r.Id, hint))
            .ThenByDescending(r => r.Version, VersionComparer.Instance)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        // Several installed versions of the chosen package (side by side) are one package: combined by the same rule
        // as a lookup by id (WingetOutputParser.CombineInstalls).
        return best is null ? null : WingetOutputParser.CombineInstalls(matches.Where(r => string.Equals(r.Id, best.Id, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>A listing id the agent may use as a package id: not empty, not truncated by winget, not a pseudo id.</summary>
    private static bool IsResolvableId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !id.Contains(WingetOutputParser.Ellipsis) && !InstalledAppDiscovery.IsPseudoId(id);

    /// <summary>How many leading dot-separated segments two winget ids share (case-insensitive).</summary>
    internal static int SharedIdSegments(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        var n = 0;
        while (n < x.Length && n < y.Length && x[n].Length > 0 && string.Equals(x[n], y[n], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>
    /// Whether a row of the upgrade listing describes the same install as the row the lookup found: the same id, the
    /// same installed version, or the same (complete) name. Guards the name match in the upgrade listing, so a
    /// related product whose name also passes the rule ("Git Extensions" for <c>^Git\b</c>) cannot take over the
    /// result of an install that was found and is up to date.
    /// </summary>
    internal static bool DescribesSameInstall(WingetRow installed, WingetRow candidate)
    {
        if (string.Equals(installed.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(installed.Version) && !string.IsNullOrWhiteSpace(candidate.Version)
            && !VersionComparer.IsUnknown(installed.Version) && !VersionComparer.IsUnknown(candidate.Version)
            && string.Equals(VersionComparer.Normalize(installed.Version), VersionComparer.Normalize(candidate.Version), StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(installed.Name)
               && !installed.Name.EndsWith(WingetOutputParser.Ellipsis) && !candidate.Name.EndsWith(WingetOutputParser.Ellipsis)
               && string.Equals(installed.Name.Trim(), candidate.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The unfiltered per-scope <c>winget list</c> (cached for the lifetime of this provider instance, i.e. once per
    /// scan). A listing that failed is cached as well, as incomplete: the checks of that scan then use winget's per-id
    /// lookup for every application, which is logged once here as a warning.
    /// </summary>
    private async Task<FullListing> GetFullListAsync(string winget, ExecutionContextInfo context, CancellationToken ct)
    {
        await _fullListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_fullListCache.TryGetValue(context.Context, out var cached)) return cached;

            var stats = StatsFor(context);
            var sw = Stopwatch.StartNew();
            FullListing listing;
            string? failure = null;
            try
            {
                var args = new StringBuilder().Append("list --accept-source-agreements --disable-interactivity").Append(ScopeArgument(context));
                AppendExtra(args, _options.WingetGlobalArgs);
                var run = await RunLookupAsync(winget, args.ToString(), _options.CheckTimeout, context, ct).ConfigureAwait(false);
                var rows = run.Started && !run.TimedOut ? WingetOutputParser.ParseListOutput(run.StandardOutput) : [];
                // Complete means an id missing from it is not installed: winget ran to the end with exit 0 and printed
                // a table, or answered that nothing is installed in this scope at all.
                var nothingInstalled = rows.Count == 0 && WingetOutputParser.IsNotInstalledOutput(run.CombinedOutput)
                                       && run.ExitCode is 0 or WingetOutputParser.ExitNoInstalledPackageFound;
                var complete = run.Started && !run.TimedOut && ((rows.Count > 0 && run.ExitCode == 0) || nothingInstalled);
                if (!complete)
                    failure = !run.Started ? run.StartFailure
                        : run.TimedOut ? $"timed out after {_options.CheckTimeout.TotalSeconds:0} seconds"
                        : run.ExitCode != 0 ? $"exit 0x{run.ExitCode:X8}: {run.LastLines(2)}"
                        : "no table in winget's output";
                listing = new FullListing(rows, run.StandardOutput, complete);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Full winget listing failed for the {Context} scope.", context.Context);
                failure = $"{ex.GetType().Name}: {ex.Message}";
                listing = new FullListing([], string.Empty, false);
            }

            stats.ListElapsed = sw.Elapsed;
            stats.ListRows = listing.Rows.Count;
            stats.ListComplete = listing.Complete;
            _logger.LogDebug("Full winget listing for the {Context} scope: {Count} row(s) in {Seconds:F1}s.", context.Context, listing.Rows.Count, sw.Elapsed.TotalSeconds);
            if (failure is not null)
                _logger.LogWarning("The full winget listing for the {Context} scope could not be used ({Reason}); this scan looks every application up with its own winget call.",
                    context.Context, failure);
            _fullListCache[context.Context] = listing;
            return listing;
        }
        finally { _fullListGate.Release(); }
    }

    /// <summary>
    /// Runs one of the scan's read-only winget lookups (<c>list</c>, <c>upgrade</c>) and counts it for the scan summary.
    /// Goes through <see cref="LookupRunner"/> when a test set one.
    /// </summary>
    private Task<ProcessRunResult> RunLookupAsync(string winget, string arguments, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
    {
        Interlocked.Increment(ref StatsFor(context).Processes);
        return LookupRunner is { } runner
            ? runner(arguments, timeout, context, ct)
            : ProcessRunner.RunAsync(_logger, winget, arguments, timeout, environment: ProcessRunner.ChildEnvironment(context), ct: ct);
    }

    /// <summary>winget.exe for this context, or null when it is not installed. A test runner needs no real winget.</summary>
    private string? LocateWinget(ExecutionContextInfo context) =>
        LookupRunner is not null || InstallRunner is not null ? "winget.exe" : WingetLocator.Find(_logger, context.IsSystem, WingetPathOverride);

    /// <summary>
    /// Runs a winget process of the install path (<c>upgrade</c>, <c>install</c>, <c>uninstall</c>, <c>show</c>), through
    /// <see cref="InstallRunner"/> when a test set one.
    /// </summary>
    private Task<ProcessRunResult> RunInstallProcessAsync(string winget, string arguments, TimeSpan timeout, Action<string>? onOutputLine,
        ExecutionContextInfo context, CancellationToken ct) =>
        InstallRunner is { } runner ? runner(arguments, timeout, context, ct)
        // A test that scripts the lookups but not the install path must never reach the real winget.
        : LookupRunner is not null ? Task.FromResult(new ProcessRunResult(-1, string.Empty, string.Empty, StartFailure: "no InstallRunner set for this test"))
        : ProcessRunner.RunAsync(_logger, winget, arguments, timeout, onOutputLine: onOutputLine,
            environment: ProcessRunner.ChildEnvironment(context), ct: ct);

    /// <summary>The key the upgrade listing is cached under for a winget source name.</summary>
    private static string SourceKey(string? sourceName) => (sourceName ?? string.Empty).Trim().ToLowerInvariant();

    private readonly Dictionary<(InstallContext Context, string Source), IReadOnlyList<WingetRow>> _upgradeListCache = new();
    private readonly SemaphoreSlim _upgradeListGate = new(1, 1);

    /// <summary>
    /// The per-scope <c>winget upgrade</c> listing (cached for the lifetime of this provider instance, i.e. once per
    /// scan and source). Never fails the check: a failed or timed-out fetch yields no rows, which leaves the list-based
    /// result in place.
    /// </summary>
    private async Task<IReadOnlyList<WingetRow>> GetUpgradeListingAsync(string winget, string? sourceName, ExecutionContextInfo context, CancellationToken ct)
    {
        var key = (context.Context, SourceKey(sourceName));
        await _upgradeListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_upgradeListCache.TryGetValue(key, out var cached)) return cached;

            IReadOnlyList<WingetRow> rows = [];
            var sw = Stopwatch.StartNew();
            try
            {
                var args = new StringBuilder().Append("upgrade");
                AppendSource(args, sourceName);
                args.Append(" --accept-source-agreements --disable-interactivity").Append(ScopeArgument(context));
                AppendExtra(args, _options.WingetGlobalArgs);
                var run = await RunLookupAsync(winget, args.ToString(), _options.CheckTimeout, context, ct).ConfigureAwait(false);
                if (run.Started && !run.TimedOut)
                    rows = WingetOutputParser.ParseListOutput(run.StandardOutput);
                else
                    _logger.LogDebug("winget upgrade listing for the {Context} scope could not be read ({Reason}); using the list-based matches.",
                        context.Context, run.StartFailure ?? "timed out");
                _logger.LogDebug("winget upgrade listing for the {Context} scope: {Count} row(s).", context.Context, rows.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "winget upgrade listing failed for the {Context} scope; using the list-based matches.", context.Context);
            }
            var stats = StatsFor(context);
            stats.UpgradeElapsed += sw.Elapsed;
            stats.UpgradeRows += rows.Count;
            _upgradeListCache[key] = rows;
            return rows;
        }
        finally { _upgradeListGate.Release(); }
    }

    /// <summary>
    /// The row of winget's upgrade listing that names the id able to upgrade the product, or null. Pure, so the rule is
    /// testable. Only rows with an available version count. First the row of the first configured id (in candidate
    /// order) that appears there; ids are compared case-insensitively and exactly, and several rows of that id (one per
    /// installed version) are combined (<see cref="WingetOutputParser.CombineInstalls"/>). Otherwise, when
    /// <paramref name="app"/> is given, a row whose Name passes the app's identity rule
    /// (<see cref="InstalledAppScanner.NameMatches"/>), chosen by <see cref="PickByName"/> (never a pseudo or truncated
    /// id) - and, when <paramref name="installed"/> is given, only one that describes that same install
    /// (<see cref="DescribesSameInstall"/>). A truncated (ellipsised) Id cell is never picked, because
    /// "Mozilla.Firefox…" could stand for either alternative, and ignoring it just keeps the list-based match.
    /// </summary>
    internal static WingetRow? PickFromUpgradeListing(IReadOnlyList<WingetRow> upgradeRows, IReadOnlyList<string> candidateIds,
        AppPolicy? app = null, WingetRow? installed = null)
    {
        if (upgradeRows.Count == 0) return null;
        foreach (var id in candidateIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var row = WingetOutputParser.CombineInstalls(upgradeRows.Where(r => r.HasAvailable && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)).ToList());
            if (row is not null) return row;
        }
        if (app is null) return null;
        return PickByName(upgradeRows.Where(r => r.HasAvailable && (installed is null || DescribesSameInstall(installed, r))), app, candidateIds);
    }

    private async Task<ListLookup> ListAsync(string winget, AppPolicy app, ExecutionContextInfo context, TimeSpan timeout, CancellationToken ct)
    {
        var args = new StringBuilder()
            .Append("list --id ").Append(Quote(app.WingetId!))
            .Append(" --exact");
        AppendSource(args, app.WingetSourceName);
        args.Append(" --accept-source-agreements --disable-interactivity")
            .Append(ScopeArgument(context));
        AppendExtra(args, _options.WingetGlobalArgs);

        var run = await RunLookupAsync(winget, args.ToString(), timeout, context, ct).ConfigureAwait(false);

        if (!run.Started) return new ListLookup(null, false, run.StartFailure);
        if (run.TimedOut)
            return new ListLookup(null, false, $"winget list timed out after {timeout.TotalSeconds:0} seconds and was terminated.");

        if (run.ExitCode == WingetOutputParser.ExitNoInstalledPackageFound || WingetOutputParser.IsNotInstalledOutput(run.CombinedOutput))
            return new ListLookup(null, true, null);

        var rows = WingetOutputParser.ParseListOutput(run.CombinedOutput);
        var matches = WingetOutputParser.FindAllById(rows, app.WingetId);
        // One row per installed version: the scan and every check after an install see the same, highest one.
        var row = WingetOutputParser.CombineInstalls(matches);
        if (matches.Count > 1)
            _logger.LogDebug("{AppId}: winget lists {Count} installs of '{WingetId}' in the {Context} scope ({Versions}); the highest, {Version}, counts.",
                app.AppId, matches.Count, app.WingetId, context.Context, string.Join(", ", matches.Select(m => m.Version)), row!.Version);

        if (row is null)
        {
            if (run.ExitCode != 0)
                return new ListLookup(null, false, $"winget list exited with 0x{run.ExitCode:X8}: {run.LastLines()}");
            return new ListLookup(null, true, null);
        }

        return new ListLookup(row, false, null);
    }

    /// <summary>Maps a winget exit code to an <see cref="InstallResult"/>. Exposed for tests.</summary>
    public static InstallResult InterpretWingetExitCode(int exitCode, ProcessRunResult? run = null)
    {
        var detail = run?.LastLines() ?? string.Empty;
        return exitCode switch
        {
            0 => InstallResult.Ok("Installed successfully."),
            WingetOutputParser.ExitNoApplicableUpgrade => InstallResult.Ok("already up to date", exitCode),
            WingetOutputParser.ExitRebootRequiredToFinish or WingetOutputParser.ExitRebootRequiredForInstall or WingetOutputParser.ExitRebootInitiated
                => InstallResult.Ok("Installed successfully; a restart is required to complete the update.", exitCode, reboot: true),
            InstallerRunner.ExitRebootRequired => InstallResult.Ok("Installed successfully; a restart is required to complete the update.", exitCode, reboot: true),
            InstallerRunner.ExitRebootInitiated => InstallResult.Ok("Installed successfully; a restart has been initiated.", exitCode, reboot: true),
            _ => InstallResult.Fail(
                $"winget upgrade failed with exit code {exitCode} (0x{exitCode:X8})." +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Output: {detail}"),
                exitCode),
        };
    }

    /// <summary>
    /// <c>--scope machine</c> in the service (LocalSystem), <c>--scope user</c> in the tray agent. This is how the two
    /// processes stay out of each other's way: each only ever sees and updates packages in its own scope.
    /// </summary>
    internal static string ScopeArgument(ExecutionContextInfo context) => context.IsSystem ? " --scope machine" : " --scope user";

    private static void AppendSource(StringBuilder args, string? sourceName)
    {
        if (!string.IsNullOrWhiteSpace(sourceName)) args.Append(" --source ").Append(Quote(sourceName));
    }

    private static void AppendExtra(StringBuilder args, string? extra)
    {
        if (!string.IsNullOrWhiteSpace(extra)) args.Append(' ').Append(extra.Trim());
    }

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '"']) >= 0 ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// <summary>Turns a raw winget output line into a short progress message, or null when it is noise.</summary>
    internal static string? Condense(string line)
    {
        if (WingetOutputParser.IsNoise(line)) return null;
        var t = line.Trim();
        if (t.Length == 0) return null;
        if (t.All(c => c == '-')) return null;
        return t.Length > 160 ? t[..157] + "..." : t;
    }
}

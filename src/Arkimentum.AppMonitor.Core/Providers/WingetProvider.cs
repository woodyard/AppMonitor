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
/// </summary>
public sealed class WingetProvider : IUpdateProvider
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

    public async Task<UpdateCheckResult> CheckAsync(AppPolicy app, InstalledApp? installed, ExecutionContextInfo context, CancellationToken ct)
    {
        if (!_options.WingetEnabled)
        {
            _logger.LogDebug("{AppId}: winget is disabled by configuration; skipping.", app.AppId);
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, "winget disabled by configuration");
        }

        if (string.IsNullOrWhiteSpace(app.WingetId))
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, $"No WingetId configured for '{app.AppId}'.");

        var winget = WingetLocator.Find(_logger, context.IsSystem, WingetPathOverride);
        if (winget is null)
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Winget, "winget.exe was not found on this machine (see the log for the probed locations).");

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
            var attempt = await ListAsync(winget, app with { WingetId = id }, context, _options.CheckTimeout, ct).ConfigureAwait(false);
            if (attempt.NotInstalled)
            {
                // winget (observed with 1.30) sometimes answers "no installed package" to `list --id X --exact` although the
                // unfiltered listing shows the very same id (e.g. Microsoft.PowerShell installed via MSI). Fall back to the
                // full per-scope listing, which is fetched once per provider instance (= once per scan).
                var fromFullList = await FindInFullListAsync(winget, id, context, ct).ConfigureAwait(false);
                if (fromFullList is not null)
                {
                    _logger.LogDebug("{AppId}: '{WingetId}' not returned by the id lookup but present in the full {Context}-scope listing ({Version}).", app.AppId, id, context.Context, fromFullList.Version);
                    attempt = new ListLookup(fromFullList, false, null);
                }
                else
                {
                    _logger.LogDebug("{AppId}: winget reports '{WingetId}' is not installed in the {Context} scope.", app.AppId, id, context.Context);
                    continue;
                }
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
            var byName = await FindByNameInFullListAsync(winget, app, candidates, context, ct).ConfigureAwait(false);
            if (byName is not null)
            {
                _logger.LogInformation("{AppId}: resolved winget id '{WingetId}' from winget's {Context}-scope listing by name ('{Name}'); configured '{Configured}' is not installed here.",
                    app.AppId, byName.Id, context.Context, byName.Name, string.Join(";", candidates));
                lookup = new ListLookup(byName, false, null);
                matchedId = byName.Id;
            }
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
            row = upgradeRow;
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

        var winget = WingetLocator.Find(_logger, context.IsSystem, WingetPathOverride);
        if (winget is null)
            return InstallResult.Fail("winget.exe was not found on this machine.");

        var sourceName = FirstNonEmpty(update.WingetSourceName, app.WingetSourceName, "winget");
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
        var run = await ProcessRunner.RunAsync(
            _logger, winget, args.ToString(), _options.InstallTimeout,
            onOutputLine: line =>
            {
                var condensed = Condense(line);
                if (condensed is null || condensed == lastProgressLine) return;
                lastProgressLine = condensed;
                progress?.Report(condensed);
            },
            environment: ProcessRunner.ChildEnvironment(context),
            ct: ct).ConfigureAwait(false);

        if (!run.Started) return UpgradeOutcome.Final(InstallResult.Fail(run.StartFailure!));
        if (run.TimedOut)
            return UpgradeOutcome.Final(InstallResult.Fail($"winget upgrade timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.", -1));

        var result = InterpretWingetExitCode(run.ExitCode, run);

        // Read the version winget now reports so the caller can record what actually got installed - and so a
        // "no applicable upgrade" / "success" answer can be checked against reality.
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

            var run = await ProcessRunner.RunAsync(_logger, winget, args.ToString(), _options.InstallTimeout, onOutputLine: ProgressSink(progress),
                environment: ProcessRunner.ChildEnvironment(context), ct: ct).ConfigureAwait(false);
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

        return await ProcessRunner.RunAsync(_logger, winget, args.ToString(), _options.CheckTimeout,
            environment: ProcessRunner.ChildEnvironment(context), ct: ct).ConfigureAwait(false);
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

        return await ProcessRunner.RunAsync(_logger, winget, args.ToString(), _options.InstallTimeout, onOutputLine: ProgressSink(progress),
            environment: ProcessRunner.ChildEnvironment(context), ct: ct).ConfigureAwait(false);
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

    private static bool IsStillOutdated(string? installed, string? available) =>
        installed is not null && !VersionComparer.IsUnknown(installed) && !string.IsNullOrWhiteSpace(available)
        && VersionComparer.Compare(installed, available) < 0;

    /// <summary>Result of a <c>winget list</c> lookup for a single package.</summary>
    private sealed record ListLookup(WingetRow? Row, bool NotInstalled, string? Error);

    private readonly Dictionary<InstallContext, IReadOnlyList<WingetRow>> _fullListCache = new();
    private readonly SemaphoreSlim _fullListGate = new(1, 1);

    /// <summary>Looks a package id up in the unfiltered per-scope listing (cached for the lifetime of this provider instance).</summary>
    private async Task<WingetRow?> FindInFullListAsync(string winget, string id, ExecutionContextInfo context, CancellationToken ct)
    {
        var rows = await GetFullListAsync(winget, context, ct).ConfigureAwait(false);
        if (rows is null) return null;

        var row = WingetOutputParser.FindById(rows, id);
        // Only trust rows that carry a real source; sourceless rows are unmapped ARP entries.
        if (row is null || string.IsNullOrWhiteSpace(row.Source)) return null;
        var idMatches = row.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || row.Id.EndsWith(WingetOutputParser.Ellipsis);
        return idMatches ? row : null;
    }

    /// <summary>
    /// Searches the unfiltered per-scope listing (the same cached one as <see cref="FindInFullListAsync"/>) for the row
    /// whose name passes the app's identity rule (see <see cref="ResolveByName"/>), or null when there is none.
    /// </summary>
    private async Task<WingetRow?> FindByNameInFullListAsync(string winget, AppPolicy app, IReadOnlyList<string> configuredIds, ExecutionContextInfo context, CancellationToken ct)
    {
        var rows = await GetFullListAsync(winget, context, ct).ConfigureAwait(false);
        return rows is null ? null : ResolveByName(rows, app, configuredIds);
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
    /// case-insensitive order, so the choice is deterministic.
    /// </summary>
    internal static WingetRow? PickByName(IEnumerable<WingetRow> rows, AppPolicy app, IReadOnlyList<string> configuredIds)
    {
        var hint = configuredIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty;
        return rows
            .Where(r => IsResolvableId(r.Id) && InstalledAppScanner.NameMatches(app, r.Name))
            .OrderByDescending(r => SharedIdSegments(r.Id, hint))
            .ThenByDescending(r => r.Version, VersionComparer.Instance)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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

    /// <summary>The unfiltered per-scope <c>winget list</c> rows (cached for the lifetime of this provider instance), or null when the listing failed.</summary>
    private async Task<IReadOnlyList<WingetRow>?> GetFullListAsync(string winget, ExecutionContextInfo context, CancellationToken ct)
    {
        IReadOnlyList<WingetRow>? rows;
        await _fullListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_fullListCache.TryGetValue(context.Context, out rows))
            {
                var args = new StringBuilder().Append("list --accept-source-agreements --disable-interactivity").Append(ScopeArgument(context));
                AppendExtra(args, _options.WingetGlobalArgs);
                var run = await ProcessRunner.RunAsync(_logger, winget, args.ToString(), _options.CheckTimeout,
                    environment: ProcessRunner.ChildEnvironment(context), ct: ct).ConfigureAwait(false);
                rows = run.Started && !run.TimedOut ? WingetOutputParser.ParseListOutput(run.StandardOutput) : [];
                _logger.LogDebug("Full winget listing for the {Context} scope: {Count} row(s).", context.Context, rows.Count);
                _fullListCache[context.Context] = rows;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Full winget listing failed for the {Context} scope.", context.Context);
            return null;
        }
        finally { _fullListGate.Release(); }
        return rows;
    }

    private readonly Dictionary<(InstallContext Context, string Source), IReadOnlyList<WingetRow>> _upgradeListCache = new();
    private readonly SemaphoreSlim _upgradeListGate = new(1, 1);

    /// <summary>
    /// The per-scope <c>winget upgrade</c> listing (cached for the lifetime of this provider instance, i.e. once per
    /// scan and source). Never fails the check: a failed or timed-out fetch yields no rows, which leaves the list-based
    /// result in place.
    /// </summary>
    private async Task<IReadOnlyList<WingetRow>> GetUpgradeListingAsync(string winget, string? sourceName, ExecutionContextInfo context, CancellationToken ct)
    {
        var key = (context.Context, (sourceName ?? string.Empty).Trim().ToLowerInvariant());
        await _upgradeListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_upgradeListCache.TryGetValue(key, out var cached)) return cached;

            IReadOnlyList<WingetRow> rows = [];
            try
            {
                var args = new StringBuilder().Append("upgrade");
                AppendSource(args, sourceName);
                args.Append(" --accept-source-agreements --disable-interactivity").Append(ScopeArgument(context));
                AppendExtra(args, _options.WingetGlobalArgs);
                var run = await ProcessRunner.RunAsync(_logger, winget, args.ToString(), _options.CheckTimeout,
                    environment: ProcessRunner.ChildEnvironment(context), ct: ct).ConfigureAwait(false);
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
            _upgradeListCache[key] = rows;
            return rows;
        }
        finally { _upgradeListGate.Release(); }
    }

    /// <summary>
    /// The row of winget's upgrade listing that names the id able to upgrade the product, or null. Pure, so the rule is
    /// testable. Only rows with an available version count. First the row of the first configured id (in candidate
    /// order) that appears there; ids are compared case-insensitively and exactly. Otherwise, when
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
            var row = upgradeRows.FirstOrDefault(r => r.HasAvailable && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
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

        var run = await ProcessRunner.RunAsync(_logger, winget, args.ToString(), timeout,
            environment: ProcessRunner.ChildEnvironment(context), ct: ct).ConfigureAwait(false);

        if (!run.Started) return new ListLookup(null, false, run.StartFailure);
        if (run.TimedOut)
            return new ListLookup(null, false, $"winget list timed out after {timeout.TotalSeconds:0} seconds and was terminated.");

        if (run.ExitCode == WingetOutputParser.ExitNoInstalledPackageFound || WingetOutputParser.IsNotInstalledOutput(run.CombinedOutput))
            return new ListLookup(null, true, null);

        var rows = WingetOutputParser.ParseListOutput(run.CombinedOutput);
        var row = WingetOutputParser.FindById(rows, app.WingetId);

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

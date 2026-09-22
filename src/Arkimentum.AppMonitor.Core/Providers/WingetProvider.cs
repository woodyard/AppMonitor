using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// Update provider backed by the Windows Package Manager (winget).
/// <para>
/// System vs. user context is expressed with <c>--scope machine</c> / <c>--scope user</c>: verified on Windows 11 with
/// winget 1.30, a machine-wide package (7zip.7zip) is only returned with <c>--scope machine</c> and a per-user package
/// (Microsoft.VisualStudioCode) only with <c>--scope user</c>; the mismatching scope exits with 0x8A150014
/// ("No installed package found matching input criteria.").
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

        // Candidate ids: the one found at scan time first, then the other configured alternatives. winget correlates an
        // installed product with every manifest that matches it (e.g. a Store build shows up under both Mozilla.Firefox
        // and Mozilla.Firefox.MSIX), but only the manifest with a matching installer type can actually upgrade it, so
        // "No applicable upgrade found" for one id means: try the next.
        var candidates = SplitIds(update.WingetId).Concat(SplitIds(update.WingetIdAlternatives)).Concat(SplitIds(app.WingetId))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
            return InstallResult.Fail($"No WingetId configured for '{app.AppId}'.");

        var winget = WingetLocator.Find(_logger, context.IsSystem, WingetPathOverride);
        if (winget is null)
            return InstallResult.Fail("winget.exe was not found on this machine.");

        var sourceName = FirstNonEmpty(update.WingetSourceName, app.WingetSourceName, "winget");
        InstallResult? last = null;
        foreach (var wingetId in candidates)
        {
            last = await UpgradeOneAsync(app, update, context, winget, wingetId, sourceName, progress, ct).ConfigureAwait(false);
            if (last.Success || last.ExitCode != WingetOutputParser.ExitNoApplicableUpgrade) return last;
            if (candidates.Count > 1 && wingetId != candidates[^1])
                _logger.LogInformation("{AppId}: no applicable upgrade for '{WingetId}'; trying the next configured id.", app.AppId, wingetId);
        }
        return last!;
    }

    private async Task<InstallResult> UpgradeOneAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
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
            ct: ct).ConfigureAwait(false);

        if (!run.Started) return InstallResult.Fail(run.StartFailure!);
        if (run.TimedOut)
            return InstallResult.Fail($"winget upgrade timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.", -1);

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

        // The take-over is checked first: when it is enabled it is the configured answer to a technology mismatch,
        // and "winget install --force" (ReinstallAsync) would leave the old install behind.
        if (ShouldReplace(run.ExitCode, update.WingetReplaceOnMismatch || app.WingetReplaceOnMismatch, stillOutdated || newVersion is null))
            return await ReplaceAsync(app, update, context, winget, wingetId, sourceName, run.ExitCode, progress, ct).ConfigureAwait(false);

        if (ShouldReinstall(run.ExitCode, context, stillOutdated || newVersion is null))
        {
            var refusal = run.ExitCode == WingetOutputParser.ExitNoApplicableUpgrade
                ? "no applicable upgrade found (the manifest's installer does not match how the product is installed, typically its scope)"
                : "the installed package type does not match the installer type";
            return await ReinstallAsync(app, update, context, winget, wingetId, sourceName, refusal, run.ExitCode, progress, ct).ConfigureAwait(false);
        }

        if (run.ExitCode == WingetOutputParser.ExitNoApplicableUpgrade)
        {
            if (stillOutdated || newVersion is null)
            {
                var message = $"winget found no applicable upgrade for '{wingetId}' ({context.Context} scope); installed version is still {newVersion ?? update.InstalledVersion ?? "unknown"}, expected {update.AvailableVersion}. " +
                              "The installed build (e.g. Store/MSIX) may not match this package id, or the package is managed elsewhere.";
                _logger.LogWarning("{AppId}: {Message}", app.AppId, message);
                return InstallResult.Fail(message, run.ExitCode);
            }
            _logger.LogInformation("{AppId}: already up to date ({Version}).", app.AppId, newVersion);
            return InstallResult.Ok("already up to date", run.ExitCode) with { InstalledVersion = newVersion };
        }

        if (!result.Success)
        {
            _logger.LogError("{AppId}: winget upgrade failed - {Message}", app.AppId, result.Message);
            return result;
        }

        if (stillOutdated && !result.RebootRequired)
        {
            var message = $"winget reported success (exit 0x{run.ExitCode:X8}) but '{wingetId}' is still at {newVersion}, expected {update.AvailableVersion}.";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, run.ExitCode);
        }

        _logger.LogInformation("{AppId}: winget upgrade finished (exit 0x{Hex}){Reboot}; installed version now {Version}.", app.AppId, run.ExitCode.ToString("X8"),
            result.RebootRequired ? ", reboot required" : string.Empty, newVersion ?? update.AvailableVersion);
        return result with { InstalledVersion = newVersion ?? update.AvailableVersion };
    }

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
    /// The way out when winget refuses to upgrade a per-user install it did not create. "winget upgrade" compares the
    /// manifest's installers with where and how the product is installed: a manifest that only declares a
    /// machine-scope installer (Perplexity.Comet) can never match a per-user install, and an installer type that
    /// differs from the technology the ARP entry was created with (Microsoft.BingWallpaper: per-user MSI, exe wrapper
    /// in the manifest) cannot either. Those filters are built from the installed package's metadata and no argument
    /// relaxes them. "winget install --force" skips the installed-package lookup altogether, so no such filter exists;
    /// without a --scope argument the manifest's installer is taken as is, and it runs in this user's session exactly
    /// as the original per-user setup did, which is also why it needs no elevation. Success is still judged by the
    /// version winget reports afterwards, never by the exit code. User context only: as LocalSystem a user-scope
    /// installer would land in SYSTEM's own profile.
    /// </summary>
    private async Task<InstallResult> ReinstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, string refusal, int refusalExitCode, IProgress<string>? progress, CancellationToken ct)
    {
        var args = new StringBuilder()
            .Append("install --id ").Append(Quote(wingetId))
            .Append(" --exact --force");
        AppendSource(args, sourceName);
        args.Append(" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
        AppendExtra(args, update.WingetExtraArgs ?? app.WingetExtraArgs);
        AppendExtra(args, _options.WingetGlobalArgs);

        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;
        _logger.LogInformation("{AppId}: winget refused to upgrade '{WingetId}' ({Refusal}, 0x{Code:X8}); installing {Version} over it with 'winget install --force' in the {Context} context.",
            app.AppId, wingetId, refusal, refusalExitCode, update.AvailableVersion, context.Context);
        progress?.Report($"Installing {name} {update.AvailableVersion} over the current version via winget...");

        var run = await ProcessRunner.RunAsync(_logger, winget, args.ToString(), _options.InstallTimeout, onOutputLine: ProgressSink(progress), ct: ct).ConfigureAwait(false);
        if (!run.Started) return InstallResult.Fail(run.StartFailure!);
        if (run.TimedOut)
            return InstallResult.Fail($"winget install timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.", -1);

        var result = InterpretWingetExitCode(run.ExitCode, run);
        var newVersion = await ReadVersionAfterAsync(app, context, winget, wingetId, sourceName, ct).ConfigureAwait(false);
        var stillOutdated = IsStillOutdated(newVersion, update.AvailableVersion);
        var prefix = $"winget could not upgrade '{wingetId}' ({context.Context} scope): {refusal}.";

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
    /// application is not installed, and if the install fails it stays that way until the next scan. Success is judged
    /// only by the version winget reports afterwards, never by the exit code.
    /// </summary>
    private async Task<InstallResult> ReplaceAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, string winget,
        string wingetId, string sourceName, int refusalExitCode, IProgress<string>? progress, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;
        var prefix = $"winget could not upgrade '{wingetId}' ({context.Context} scope): the installed package's technology differs from the manifest's installer.";

        // ---- 1. remove the current install. No --purge (user data is not ours to delete) and no WingetExtraArgs
        // (those are upgrade arguments); the scope is the one the upgrade used, so we only ever touch our own context.
        var uninstallArgs = new StringBuilder()
            .Append("uninstall --id ").Append(Quote(wingetId))
            .Append(" --exact");
        AppendSource(uninstallArgs, sourceName);
        uninstallArgs.Append(" --silent --disable-interactivity")
            .Append(ScopeArgument(context));
        AppendExtra(uninstallArgs, _options.WingetGlobalArgs);

        _logger.LogInformation("{AppId}: winget refused to upgrade '{WingetId}' (install technology mismatch, 0x{Code:X8}) and WingetReplaceOnMismatch is set; removing the current install in the {Context} context.",
            app.AppId, wingetId, refusalExitCode, context.Context);
        progress?.Report($"Removing the current install of {name} via winget...");

        var uninstall = await ProcessRunner.RunAsync(_logger, winget, uninstallArgs.ToString(), _options.InstallTimeout, onOutputLine: ProgressSink(progress), ct: ct).ConfigureAwait(false);
        string? uninstallFailure = null;
        if (!uninstall.Started) uninstallFailure = uninstall.StartFailure;
        else if (uninstall.TimedOut) uninstallFailure = $"winget uninstall timed out after {_options.InstallTimeout.TotalMinutes:0} minutes and was terminated.";
        else if (uninstall.ExitCode != 0) uninstallFailure = $"winget uninstall exited with 0x{uninstall.ExitCode:X8}. {uninstall.LastLines()}".TrimEnd();

        if (uninstallFailure is not null)
        {
            var message = $"{prefix} Removing the current install with winget failed: {uninstallFailure}";
            _logger.LogError("{AppId}: {Message}", app.AppId, message);
            return InstallResult.Fail(message, uninstall.Started && !uninstall.TimedOut ? uninstall.ExitCode : -1);
        }

        // ---- 2. install the new package. No scope argument in the user context: a "--scope user" filter would exclude
        // an MSIX installer, which has no scope at all (the same reason ReinstallAsync omits it).
        var installArgs = new StringBuilder()
            .Append("install --id ").Append(Quote(wingetId))
            .Append(" --exact");
        AppendSource(installArgs, sourceName);
        installArgs.Append(" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
        if (context.IsSystem) installArgs.Append(ScopeArgument(context));
        AppendExtra(installArgs, update.WingetExtraArgs ?? app.WingetExtraArgs);
        AppendExtra(installArgs, _options.WingetGlobalArgs);

        _logger.LogInformation("{AppId}: the previous install of '{WingetId}' was removed; installing {Version} with winget in the {Context} context.",
            app.AppId, wingetId, update.AvailableVersion, context.Context);
        progress?.Report($"Installing {name} {update.AvailableVersion} via winget...");

        var install = await ProcessRunner.RunAsync(_logger, winget, installArgs.ToString(), _options.InstallTimeout, onOutputLine: ProgressSink(progress), ct: ct).ConfigureAwait(false);
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
        IReadOnlyList<WingetRow>? rows;
        await _fullListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_fullListCache.TryGetValue(context.Context, out rows))
            {
                var args = new StringBuilder().Append("list --accept-source-agreements --disable-interactivity").Append(ScopeArgument(context));
                AppendExtra(args, _options.WingetGlobalArgs);
                var run = await ProcessRunner.RunAsync(_logger, winget, args.ToString(), _options.CheckTimeout, ct: ct).ConfigureAwait(false);
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

        var row = WingetOutputParser.FindById(rows, id);
        // Only trust rows that carry a real source; sourceless rows are unmapped ARP entries.
        if (row is null || string.IsNullOrWhiteSpace(row.Source)) return null;
        var idMatches = row.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || row.Id.EndsWith(WingetOutputParser.Ellipsis);
        return idMatches ? row : null;
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

        var run = await ProcessRunner.RunAsync(_logger, winget, args.ToString(), timeout, ct: ct).ConfigureAwait(false);

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

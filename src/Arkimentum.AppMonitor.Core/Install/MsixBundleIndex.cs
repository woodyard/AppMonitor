using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Install;

/// <summary>One MSIX bundle on the machine: its full name and what that name says (package name, bundle version, family).</summary>
public sealed record MsixBundle(string FullName, string Name, string Version, string Family);

/// <summary>
/// A row of SYSTEM's winget listing that is a provisioned MSIX bundle (see <see cref="MsixBundleIndex.Match"/>): the
/// bundle, and the highest version of its family's packages on the machine, or null when no package of the family is
/// registered or staged for any user.
/// </summary>
public sealed record MsixBundleMatch(MsixBundle Bundle, string? PackageVersion);

/// <summary>
/// The MSIX bundles and packages on the machine, for reading SYSTEM's winget listing. As LocalSystem, winget lists a
/// provisioned msixbundle by the bundle's identity name and the bundle's own version, not the application's: PowerShell
/// 7.7 is <c>Microsoft.PowerShell  Microsoft.PowerShell  2026.1110.418.0</c>
/// (<c>MSIX\Microsoft.PowerShell_2026.1110.418.0_neutral_~_8wekyb3d8bbwe</c>) while its package is 7.7.0.0, Windows
/// Terminal 3001.25.3131.0 for 1.25.3131.0 (CPC-hsk-HSNE00X, 2026-11-12). The users' own listings show the packages.
/// Built from one enumeration per scan (<see cref="LoadAsync"/>); the rules are pure and testable.
/// </summary>
public sealed class MsixBundleIndex
{
    private readonly IReadOnlyList<MsixBundle> _bundles;
    private readonly Dictionary<string, string> _highestPackageVersion;

    private MsixBundleIndex(IReadOnlyList<MsixBundle> bundles, Dictionary<string, string> highestPackageVersion, string? failure)
    {
        _bundles = bundles;
        _highestPackageVersion = highestPackageVersion;
        Failure = failure;
    }

    /// <summary>An index that knows no bundle: every row stays as winget listed it.</summary>
    public static MsixBundleIndex Empty { get; } = new([], new(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>Why the package enumeration gave no (complete) answer, or null. Bundles may still be known (see <see cref="LoadAsync"/>).</summary>
    public string? Failure { get; }

    public IReadOnlyList<MsixBundle> Bundles => _bundles;

    /// <summary>
    /// The index of a list of package full names (bundles and packages mixed, as <c>Get-AppxPackage -PackageTypeFilter
    /// Main,Bundle</c> gives them; duplicates and names that are no full name are ignored). A bundle is a full name with the
    /// resource id "~" (<see cref="AppxProbeResult.IsBundleFullName"/>). Pure.
    /// </summary>
    public static MsixBundleIndex FromFullNames(IEnumerable<string?> fullNames, string? failure = null)
    {
        var bundles = new List<MsixBundle>();
        var highest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in fullNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Parse(raw) is not { } parsed) continue;
            if (AppxProbeResult.IsBundleFullName(parsed.FullName))
            {
                bundles.Add(parsed);
                continue;
            }
            if (VersionComparer.IsUnknown(parsed.Version)) continue;
            if (!highest.TryGetValue(parsed.Family, out var current) || VersionComparer.Compare(parsed.Version, current) > 0)
                highest[parsed.Family] = parsed.Version;
        }
        return new MsixBundleIndex(bundles, highest, failure);
    }

    /// <summary>
    /// A package or bundle full name (<c>Name_Version_Architecture_ResourceId_PublisherId</c>) split into name, version and
    /// family (<see cref="AppxPackageProbe.FamilyFromFullName"/>); null for anything else. Pure.
    /// </summary>
    public static MsixBundle? Parse(string? fullName)
    {
        var family = AppxPackageProbe.FamilyFromFullName(fullName);
        if (family is null) return null;
        var trimmed = fullName!.Trim();
        var parts = trimmed.Split('_');
        return parts[1].Length == 0 ? null : new MsixBundle(trimmed, parts[0], parts[1], family);
    }

    /// <summary>The highest version of the family's packages (registered or staged for any user), or null when none is known.</summary>
    public string? HighestPackageVersion(string family) =>
        _highestPackageVersion.TryGetValue(family, out var version) ? version : null;

    /// <summary>
    /// Whether a winget row could be a provisioned bundle at all, before anything is enumerated: its Name is an MSIX
    /// package name with a dot (<c>Microsoft.PowerShell</c>), which is how winget names a bundle whose display name it
    /// cannot read, and which the display names of classic installs ("PowerShell 7-x64", "7-Zip 24.08 (x64)") rarely are.
    /// Pure and cheap, so a scan only enumerates the packages when a row of an application it checks passes it.
    /// </summary>
    public static bool IsCandidate(WingetRow row)
    {
        var name = row.Name.Trim().TrimEnd(WingetOutputParser.Ellipsis);
        return name.Contains('.') && AppxPackageProbe.IsValidPackageName(name);
    }

    /// <summary>
    /// The bundle a winget row stands for, or null: a bundle whose package name is the row's Name (case-insensitive; a
    /// Name winget shortened with "…" matches by its start) and whose version is the row's Version. Both must agree, so
    /// a classic install that happens to carry the same name ("Microsoft.PowerShell" at 7.6.1) is never taken for the
    /// bundle. Should two bundles qualify (two publishers), the one with a package on the machine wins. Pure.
    /// </summary>
    public MsixBundleMatch? Match(WingetRow row)
    {
        if (_bundles.Count == 0 || string.IsNullOrWhiteSpace(row.Name) || VersionComparer.IsUnknown(row.Version)) return null;
        var name = row.Name.Trim();
        var shortened = name.EndsWith(WingetOutputParser.Ellipsis);
        var prefix = name.TrimEnd(WingetOutputParser.Ellipsis);
        var bundle = _bundles
            .Where(b => (shortened ? b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) : string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                        && WingetOutputParser.SameInstalledVersion(b.Version, row.Version))
            .OrderByDescending(b => HighestPackageVersion(b.Family) is not null)
            .FirstOrDefault();
        return bundle is null ? null : new MsixBundleMatch(bundle, HighestPackageVersion(bundle.Family));
    }

    /// <summary>
    /// The rows with every bundle row (<see cref="Match"/>) replaced by what it stands for: the version of the family's
    /// highest package (empty when none is known - never the bundle's), no available version (an MSIX package is updated
    /// in each user's context, and winget compares the machine scope against the bundle's version, so it offers nothing
    /// there that could be installed), and <see cref="WingetRow.MsixBundleVersion"/> set to the bundle's version. Every
    /// other row is returned as it is. Pure.
    /// </summary>
    public IReadOnlyList<WingetRow> Resolve(IReadOnlyList<WingetRow> rows)
    {
        if (_bundles.Count == 0) return rows;
        List<WingetRow>? result = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].MsixBundleVersion is null && Match(rows[i]) is { } match)
            {
                result ??= [.. rows];
                result[i] = rows[i] with { Version = match.PackageVersion ?? string.Empty, Available = string.Empty, MsixBundleVersion = rows[i].Version };
            }
        }
        return result ?? rows;
    }

    /// <summary>
    /// The rows without the bundle rows (<see cref="Match"/>): for SYSTEM's upgrade listing, whose offers for a bundle
    /// compare the application's version with the bundle's and can never be installed in the machine scope. Pure.
    /// </summary>
    public IReadOnlyList<WingetRow> WithoutBundles(IReadOnlyList<WingetRow> rows) =>
        _bundles.Count == 0 || !rows.Any(r => Match(r) is not null) ? rows : rows.Where(r => Match(r) is null).ToList();

    // ---------------------------------------------------------------- enumeration

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The PowerShell command: every package and bundle on the machine, for every user (registered or staged), one
    /// <c>P &lt;full name&gt;</c> line each, or an <c>E &lt;message&gt;</c> line. Needs LocalSystem (or an administrator)
    /// for <c>-AllUsers</c>. Contains no double quotes, so it can stand inside <c>-Command "..."</c>.
    /// </summary>
    public const string Script =
        "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
        "try { Get-AppxPackage -AllUsers -PackageTypeFilter Main,Bundle | ForEach-Object { 'P '+[string]$_.PackageFullName } } " +
        "catch { 'E Get-AppxPackage: '+$_.Exception.Message }";

    /// <summary>The script's output: the full names of its <c>P</c> lines and the first line of each <c>E</c> line. Pure.</summary>
    public static (IReadOnlyList<string> FullNames, IReadOnlyList<string> Errors) ParseOutput(string? output)
    {
        var names = new List<string>();
        var errors = new List<string>();
        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            var t = line.Trim().TrimStart('﻿');
            if (t.StartsWith("P ", StringComparison.Ordinal)) names.Add(t[2..].Trim());
            else if (t.StartsWith("E ", StringComparison.Ordinal)) errors.Add(t[2..].Trim());
        }
        return (names, errors);
    }

    /// <summary>
    /// The registry's list of provisioned packages (<c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications</c>,
    /// one subkey per provisioned package or bundle full name): undocumented, read only as a second source of bundle
    /// names, should <c>Get-AppxPackage</c> not list a bundle that is provisioned but registered for nobody. Never throws.
    /// </summary>
    public static IReadOnlyList<string> ReadProvisionedFullNames()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications");
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Enumerates the machine's packages and bundles once (<see cref="Script"/> with Windows PowerShell 5.1, as the
    /// service) plus the provisioned bundles (<see cref="ReadProvisionedFullNames"/>). Never throws except for
    /// cancellation: a failed enumeration keeps the bundles it still knows (their rows then read "version unknown",
    /// never the bundle's version) and says why in <see cref="Failure"/>. <paramref name="runner"/> and
    /// <paramref name="provisioned"/> replace the process and the registry in tests.
    /// </summary>
    public static async Task<MsixBundleIndex> LoadAsync(ILogger logger, CancellationToken ct,
        Func<string, string, TimeSpan, CancellationToken, Task<ProcessRunResult>>? runner = null,
        Func<IReadOnlyList<string>>? provisioned = null)
    {
        var names = new List<string>((provisioned ?? ReadProvisionedFullNames)().Where(AppxProbeResult.IsBundleFullName));
        string? failure = null;
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Script}\"";
            var run = runner is not null
                ? await runner(powershell, arguments, Timeout, ct).ConfigureAwait(false)
                : await ProcessRunner.RunAsync(logger, powershell, arguments, Timeout, ct: ct).ConfigureAwait(false);
            if (!run.Started) failure = run.StartFailure;
            else if (run.TimedOut) failure = $"timed out after {Timeout.TotalSeconds:0} seconds";
            else
            {
                var (fullNames, errors) = ParseOutput(run.StandardOutput);
                names.AddRange(fullNames);
                if (errors.Count > 0) failure = string.Join(" | ", errors);
                else if (fullNames.Count == 0 && run.ExitCode != 0) failure = $"PowerShell exited with {run.ExitCode}: {run.LastLines(2)}";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Enumerating the MSIX packages failed.");
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }
        return FromFullNames(names, failure);
    }
}

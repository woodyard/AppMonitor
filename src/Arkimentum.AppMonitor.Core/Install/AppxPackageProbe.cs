using System.Text.Json;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Install;

/// <summary>One user's registration of an MSIX package, as <c>PackageUserInformation</c> reports it.</summary>
/// <param name="Sid">The user's SID (S-1-5-18 is SYSTEM).</param>
/// <param name="InstallState">Installed, Staged, Paused, ...</param>
public sealed record AppxPackageUser(string Sid, string InstallState);

/// <summary>One MSIX package of the family present on the machine (<c>Get-AppxPackage -AllUsers</c>).</summary>
/// <param name="PackageFamilyName">The package's family (null in output of an older probe script).</param>
public sealed record AppxPackageRegistration(string FullName, string Version, string Architecture, IReadOnlyList<AppxPackageUser> Users,
    string? PackageFamilyName = null);

/// <summary>One provisioned package (<c>Get-AppxProvisionedPackage -Online</c>): installed for every new user at sign-in.</summary>
/// <param name="PackageName">The provisioned package's full name (<c>Name_Version_Arch_ResourceId_PublisherId</c>), when known.</param>
public sealed record AppxProvisionedPackage(string DisplayName, string Version, string? PackageName = null)
{
    /// <summary>The family of <see cref="PackageName"/> (see <see cref="AppxPackageProbe.FamilyFromFullName"/>), or null.</summary>
    public string? PackageFamilyName => AppxPackageProbe.FamilyFromFullName(PackageName);
}

/// <summary>
/// What <see cref="AppxPackageProbe"/> found for one package family: the packages on the machine with their users, the
/// provisioned packages, the errors of the individual cmdlets, or <see cref="Failure"/> when PowerShell gave no answer.
/// </summary>
public sealed record AppxProbeResult(
    IReadOnlyList<AppxPackageRegistration> Packages,
    IReadOnlyList<AppxProvisionedPackage> Provisioned,
    IReadOnlyList<string> Errors,
    string? Failure = null)
{
    public static AppxProbeResult Failed(string failure) => new([], [], [], failure);

    /// <summary>
    /// The highest package version of the family on the machine (registered or staged for any user); null when none is
    /// known. A provisioned entry's version counts only when no package is listed at all and the entry is not a bundle
    /// (<see cref="IsBundleFullName"/>): a provisioned bundle reports the bundle's own version, which is not the
    /// application's. PowerShell 7.7's msixbundle is provisioned as 2026.1110.418.0 while its package is 7.7.0.0 (the
    /// take-over on CPC-hsk-HSNE00X, 2026-11-12, recorded "installed 2026.1110.418.0").
    /// </summary>
    public string? HighestVersion => Highest(PackageVersions) ?? Highest(ProvisionedPackageVersions);

    /// <summary>The versions of the packages listed for any user (registered or staged).</summary>
    public IEnumerable<string> PackageVersions => Packages.Select(p => p.Version);

    /// <summary>The versions of provisioned entries that are packages, not bundles (see <see cref="HighestVersion"/>).</summary>
    public IEnumerable<string> ProvisionedPackageVersions =>
        Provisioned.Where(p => !IsBundleFullName(p.PackageName)).Select(p => p.Version);

    /// <summary>
    /// Whether a package full name is a bundle's: bundles carry the resource id "~"
    /// (<c>Microsoft.PowerShell_2026.1110.418.0_neutral_~_8wekyb3d8bbwe</c>), packages none
    /// (<c>Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe</c>). An unknown name is not taken for a bundle.
    /// </summary>
    public static bool IsBundleFullName(string? fullName) =>
        !string.IsNullOrWhiteSpace(fullName) && fullName.Contains("_~_", StringComparison.Ordinal);

    private static string? Highest(IEnumerable<string> versions) =>
        versions.Where(v => !string.IsNullOrWhiteSpace(v) && !VersionComparer.IsUnknown(v))
            .OrderByDescending(v => v, VersionComparer.Instance)
            .FirstOrDefault();

    /// <summary>
    /// One line for the log and the cloud event, e.g. <c>6000.519.329.0 X64 [S-1-5-18 Installed, S-1-5-21-…-1001 Installed];
    /// 6000.519.297.0 X64 [S-1-5-21-…-1001 Staged]; provisioned 6000.519.329.0</c>. Newest package first.
    /// </summary>
    public string Summarize()
    {
        if (Failure is not null) return $"probe failed ({Failure})";
        var packages = Packages.Count == 0
            ? "no package of the family"
            : string.Join("; ", Packages
                .OrderByDescending(p => p.Version, VersionComparer.Instance)
                .ThenBy(p => p.Architecture, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Version} {p.Architecture} [{(p.Users.Count == 0 ? "no user" : string.Join(", ", p.Users.Select(u => $"{u.Sid} {u.InstallState}")))}]"));
        var provisioned = Provisioned.Count == 0
            ? "not provisioned"
            : "provisioned " + string.Join(", ", Provisioned.Select(p => p.Version).Distinct(StringComparer.OrdinalIgnoreCase));
        var errors = Errors.Count == 0 ? string.Empty : $"; errors: {string.Join(" | ", Errors)}";
        return $"{packages}; {provisioned}{errors}";
    }
}

/// <summary>
/// Reads which packages of one MSIX package family are on the machine, for which users, and whether one is
/// provisioned - the evidence the service records before and after it installs a package for all users on a tray's
/// behalf (see <see cref="Providers.SystemInstallHandOverRequest"/>). Runs Windows PowerShell 5.1 as the service
/// (LocalSystem, which <c>-AllUsers</c> and <c>-Online</c> require), never throws, and gives up after
/// <see cref="Timeout"/>.
/// </summary>
public static partial class AppxPackageProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A package family name: the package name (letters, digits, dots, dashes) and the 13-character publisher id. Strict
    /// on purpose, because the value is put into a PowerShell command line.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9.\-]{1,100}_[a-z0-9]{13}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageFamilyNameRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    /// <summary>Whether <paramref name="packageFamilyName"/> is a well-formed package family name (see <see cref="PackageFamilyNameRegex"/>).</summary>
    public static bool IsValidPackageFamilyName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? packageFamilyName) =>
        !string.IsNullOrEmpty(packageFamilyName) && PackageFamilyNameRegex().IsMatch(packageFamilyName);

    /// <summary>
    /// The PowerShell script for <paramref name="packageFamilyName"/>: compressed JSON of the family's packages (every
    /// user's registration as SID=InstallState) and the provisioned packages whose DisplayName is the family's package
    /// name. Contains no double quotes, so it can stand inside <c>-Command "..."</c>. Throws for a family name that is not
    /// valid, which the callers have already refused.
    /// </summary>
    public static string BuildScript(string packageFamilyName)
    {
        if (!IsValidPackageFamilyName(packageFamilyName))
            throw new ArgumentException($"'{packageFamilyName}' is not a valid package family name.", nameof(packageFamilyName));
        var name = packageFamilyName[..packageFamilyName.LastIndexOf('_')];
        return Script($"$pfn='{packageFamilyName}'; $name='{name}'; ", "$_.PackageFamilyName -eq $pfn");
    }

    /// <summary>
    /// The script for every package whose package name (the family name without the publisher id) is
    /// <paramref name="packageName"/>, whatever its publisher: for the take-over's check after it provisioned an MSIX
    /// package whose family winget did not name (<c>winget show</c> prints none). Same output as <see cref="BuildScript"/>,
    /// each package with its family. Throws for a name that is not valid (see <see cref="IsValidPackageName"/>).
    /// </summary>
    public static string BuildScriptForName(string packageName)
    {
        if (!IsValidPackageName(packageName))
            throw new ArgumentException($"'{packageName}' is not a valid package name.", nameof(packageName));
        return Script($"$name='{packageName}'; ", "$_.Name -eq $name");
    }

    private static string Script(string variables, string packageFilter) =>
        "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
        variables +
        "$r=[ordered]@{packages=@(); provisioned=@(); errors=@()}; " +
        $"try {{ $r.packages=@(Get-AppxPackage -AllUsers | Where-Object {{ {packageFilter} }} | ForEach-Object {{ $p=$_; " +
        "[ordered]@{fullName=[string]$p.PackageFullName; version=[string]$p.Version; architecture=[string]$p.Architecture; family=[string]$p.PackageFamilyName; " +
        "users=@($p.PackageUserInformation | ForEach-Object { $s=$_.UserSecurityId; if ($s.Sid) { $s=$s.Sid }; ([string]$s)+'='+([string]$_.InstallState) })} }) } " +
        "catch { $r.errors+=('Get-AppxPackage: '+$_.Exception.Message) }; " +
        "try { $r.provisioned=@(Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $name } | ForEach-Object { " +
        "[ordered]@{displayName=[string]$_.DisplayName; version=[string]$_.Version; packageName=[string]$_.PackageName} }) } " +
        "catch { $r.errors+=('Get-AppxProvisionedPackage: '+$_.Exception.Message) }; " +
        "$r | ConvertTo-Json -Depth 5 -Compress";

    /// <summary>
    /// An MSIX package name: 3 to 50 letters, digits, dots and dashes (the AppxManifest's <c>Identity Name</c> rule).
    /// Strict, because the value is put into a PowerShell command line.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9.\-]{3,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();

    /// <summary>Whether <paramref name="packageName"/> is a well-formed MSIX package name (see <see cref="PackageNameRegex"/>).</summary>
    public static bool IsValidPackageName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? packageName) =>
        !string.IsNullOrEmpty(packageName) && PackageNameRegex().IsMatch(packageName);

    /// <summary>
    /// The package family of a package full name (<c>Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe</c> ->
    /// <c>Microsoft.PowerShell_8wekyb3d8bbwe</c>): the name and the publisher id, the first and the last of its five
    /// <c>_</c>-separated parts. Null for anything that does not yield a valid family. Pure.
    /// </summary>
    public static string? FamilyFromFullName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        var parts = fullName.Trim().Split('_');
        if (parts.Length != 5) return null;
        var family = $"{parts[0]}_{parts[4]}";
        return IsValidPackageFamilyName(family) ? family : null;
    }

    /// <summary>
    /// Parses the script's JSON. Pure, so the parsing is testable. Tolerant of what ConvertTo-Json makes of PowerShell
    /// collections: a list of one may arrive as the object itself, an empty one as null or missing, a string list of one
    /// as the string. Output that is no JSON object at all is a <see cref="AppxProbeResult.Failure"/>.
    /// </summary>
    public static AppxProbeResult Parse(string? output)
    {
        var json = LastJsonLine(output);
        if (json is null) return AppxProbeResult.Failed("no JSON in PowerShell's output");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) root = root.EnumerateArray().FirstOrDefault();
            if (root.ValueKind != JsonValueKind.Object) return AppxProbeResult.Failed("PowerShell's JSON is not an object");

            var packages = Items(root, "packages")
                .Where(p => p.ValueKind == JsonValueKind.Object)
                .Select(p => new AppxPackageRegistration(
                    Text(p, "fullName"), Text(p, "version"), Text(p, "architecture"),
                    Items(p, "users").Select(u => ParseUser(TextOf(u))).Where(u => u is not null).Select(u => u!).ToList(),
                    NullIfEmpty(Text(p, "family"))))
                .ToList();
            var provisioned = Items(root, "provisioned")
                .Where(p => p.ValueKind == JsonValueKind.Object)
                .Select(p => new AppxProvisionedPackage(Text(p, "displayName"), Text(p, "version"), NullIfEmpty(Text(p, "packageName"))))
                .ToList();
            // Cmdlet messages come with line breaks ("Access is denied.\r\n\r\nAccess is denied.\r\n"); one line each.
            var errors = Items(root, "errors").Select(e => WhitespaceRegex().Replace(TextOf(e), " ").Trim()).Where(e => e.Length > 0).ToList();
            return new AppxProbeResult(packages, provisioned, errors);
        }
        catch (JsonException ex)
        {
            return AppxProbeResult.Failed($"PowerShell's JSON could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the probe for <paramref name="packageFamilyName"/> with <c>powershell.exe -NoProfile -NonInteractive
    /// -ExecutionPolicy Bypass -Command "..."</c>. <paramref name="runner"/> replaces the process in tests.
    /// </summary>
    public static async Task<AppxProbeResult> RunAsync(ILogger logger, string packageFamilyName, CancellationToken ct,
        Func<string, string, TimeSpan, CancellationToken, Task<ProcessRunResult>>? runner = null)
    {
        if (!IsValidPackageFamilyName(packageFamilyName)) return AppxProbeResult.Failed($"'{packageFamilyName}' is not a valid package family name");
        return await RunScriptAsync(logger, BuildScript(packageFamilyName), packageFamilyName, ct, runner).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the probe for every package named <paramref name="packageName"/> (see <see cref="BuildScriptForName"/>), the
    /// way <see cref="RunAsync"/> does for a family. <paramref name="runner"/> replaces the process in tests.
    /// </summary>
    public static async Task<AppxProbeResult> RunForNameAsync(ILogger logger, string packageName, CancellationToken ct,
        Func<string, string, TimeSpan, CancellationToken, Task<ProcessRunResult>>? runner = null)
    {
        if (!IsValidPackageName(packageName)) return AppxProbeResult.Failed($"'{packageName}' is not a valid package name");
        return await RunScriptAsync(logger, BuildScriptForName(packageName), packageName, ct, runner).ConfigureAwait(false);
    }

    private static async Task<AppxProbeResult> RunScriptAsync(ILogger logger, string script, string subject, CancellationToken ct,
        Func<string, string, TimeSpan, CancellationToken, Task<ProcessRunResult>>? runner)
    {
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"";
            var run = runner is not null
                ? await runner(powershell, arguments, Timeout, ct).ConfigureAwait(false)
                : await ProcessRunner.RunAsync(logger, powershell, arguments, Timeout, ct: ct).ConfigureAwait(false);
            if (!run.Started) return AppxProbeResult.Failed(run.StartFailure!);
            if (run.TimedOut) return AppxProbeResult.Failed($"timed out after {Timeout.TotalSeconds:0} seconds");
            var result = Parse(run.StandardOutput);
            if (result.Failure is not null && run.ExitCode != 0)
                return AppxProbeResult.Failed($"PowerShell exited with {run.ExitCode}: {run.LastLines(2)}");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The Appx package probe for {Family} failed.", subject);
            return AppxProbeResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? LastJsonLine(string? output) =>
        string.IsNullOrWhiteSpace(output)
            ? null
            : output.Split('\n').Select(l => l.Trim().TrimStart('﻿')).LastOrDefault(l => l.StartsWith('{') || l.StartsWith('['));

    /// <summary>The elements of a property that should be a list: an array's items, a lone value, or nothing.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var value)) return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToList(),
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            _ => [value],
        };
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        }
        value = default;
        return false;
    }

    private static string Text(JsonElement obj, string name) => TryGet(obj, name, out var value) ? TextOf(value) : string.Empty;

    private static string TextOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.ToString().Trim(),
    };

    /// <summary>"S-1-5-21-…-1001=Installed" -> the SID and the state; null for anything else.</summary>
    private static AppxPackageUser? ParseUser(string text)
    {
        var i = text.LastIndexOf('=');
        if (i <= 0) return null;
        var sid = text[..i].Trim();
        var state = text[(i + 1)..].Trim();
        return sid.Length == 0 ? null : new AppxPackageUser(sid, state.Length == 0 ? "unknown" : state);
    }
}

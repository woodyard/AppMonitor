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
public sealed record AppxPackageRegistration(string FullName, string Version, string Architecture, IReadOnlyList<AppxPackageUser> Users);

/// <summary>One provisioned package (<c>Get-AppxProvisionedPackage -Online</c>): installed for every new user at sign-in.</summary>
public sealed record AppxProvisionedPackage(string DisplayName, string Version);

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

    /// <summary>The highest package version of the family on the machine, for any user or provisioned; null when none is known.</summary>
    public string? HighestVersion =>
        Packages.Select(p => p.Version).Concat(Provisioned.Select(p => p.Version))
            .Where(v => !string.IsNullOrWhiteSpace(v))
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
    public static bool IsValidPackageFamilyName(string? packageFamilyName) =>
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
        return "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
               $"$pfn='{packageFamilyName}'; $name='{name}'; " +
               "$r=[ordered]@{packages=@(); provisioned=@(); errors=@()}; " +
               "try { $r.packages=@(Get-AppxPackage -AllUsers | Where-Object { $_.PackageFamilyName -eq $pfn } | ForEach-Object { $p=$_; " +
               "[ordered]@{fullName=[string]$p.PackageFullName; version=[string]$p.Version; architecture=[string]$p.Architecture; " +
               "users=@($p.PackageUserInformation | ForEach-Object { $s=$_.UserSecurityId; if ($s.Sid) { $s=$s.Sid }; ([string]$s)+'='+([string]$_.InstallState) })} }) } " +
               "catch { $r.errors+=('Get-AppxPackage: '+$_.Exception.Message) }; " +
               "try { $r.provisioned=@(Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $name } | ForEach-Object { " +
               "[ordered]@{displayName=[string]$_.DisplayName; version=[string]$_.Version} }) } " +
               "catch { $r.errors+=('Get-AppxProvisionedPackage: '+$_.Exception.Message) }; " +
               "$r | ConvertTo-Json -Depth 5 -Compress";
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
                    Items(p, "users").Select(u => ParseUser(TextOf(u))).Where(u => u is not null).Select(u => u!).ToList()))
                .ToList();
            var provisioned = Items(root, "provisioned")
                .Where(p => p.ValueKind == JsonValueKind.Object)
                .Select(p => new AppxProvisionedPackage(Text(p, "displayName"), Text(p, "version")))
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
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{BuildScript(packageFamilyName)}\"";
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
            logger.LogDebug(ex, "The Appx package probe for {Family} failed.", packageFamilyName);
            return AppxProbeResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

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

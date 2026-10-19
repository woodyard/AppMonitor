using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Install;

/// <summary>
/// Makes the "uninstall the previous version" step of a winget update in a user's session invisible for per-user Inno
/// Setup applications.
/// <para>
/// The case: GIMP 3.2.4 -> 3.2.6 on H-SURFACELAP5 (2026-10-19), a per-user install updated by the tray. The manifest says
/// <c>UpgradeBehavior: uninstallPrevious</c>, so winget first runs the old version's registered quiet uninstall command,
/// and Inno registers <c>QuietUninstallString = "&lt;dir&gt;\unins000.exe" /SILENT</c>. For Inno, /SILENT asks no questions
/// but shows a progress window; only /VERYSILENT hides it. The user watched GIMP being uninstalled. In the service the
/// same window would appear in session 0, where nobody sees it, so this is only done in a user's session.
/// </para>
/// <para>
/// Before the install, every Inno entry of the application (it has <see cref="InnoVersionValue"/>, and the app's
/// detection rule matches it, <see cref="InstalledAppScanner.Match"/>) in the user's own Uninstall key whose
/// QuietUninstallString is the Inno /SILENT form gets <c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART</c> instead; after the
/// install, an entry that still carries exactly that value gets its original back. After a successful
/// <c>uninstallPrevious</c> the old entry is gone and the new installer has written its own, which is left alone. Only
/// HKCU, never HKLM. Best effort: a registry error is logged and the install goes ahead unchanged.
/// </para>
/// </summary>
public static partial class InnoQuietUninstall
{
    /// <summary>The per-user Uninstall key, under HKCU.</summary>
    public const string UserUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    /// <summary>The value every Inno Setup Uninstall entry has.</summary>
    public const string InnoVersionValue = "Inno Setup: Setup Version";
    public const string QuietUninstallValue = "QuietUninstallString";

    /// <summary>One QuietUninstallString changed for an install: the entry's key name, the value before and the value written.</summary>
    public sealed record Rewrite(string KeyName, string Original, string Rewritten);

    // The Inno uninstaller ("...\unins000.exe": "unins" and a number, never "uninstall.exe"; quoted or not) and what follows it.
    [GeneratedRegex(@"^\s*(?<exe>""[^""]*\\unins\d+\.exe""|[^\s""]*\\unins\d+\.exe)(?<args>\s.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnoCommandRegex();

    [GeneratedRegex(@"(?<=^|\s)/SILENT(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SilentTokenRegex();

    /// <summary>
    /// The very-silent form of an Inno quiet uninstall command (<c>"C:\x\unins000.exe" /SILENT</c> -&gt;
    /// <c>"C:\x\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART</c>), or null when the command is not an Inno
    /// uninstaller with a standalone /SILENT (already /VERYSILENT, NSIS /S, msiexec, anything else). Only the /SILENT token
    /// is replaced; switches already present are not added twice. Pure, so the rule is testable.
    /// </summary>
    public static string? VerySilentForm(string? quietUninstallString)
    {
        if (string.IsNullOrWhiteSpace(quietUninstallString)) return null;
        var m = InnoCommandRegex().Match(quietUninstallString);
        if (!m.Success || !m.Groups["args"].Success) return null;
        var args = m.Groups["args"].Value;
        if (!SilentTokenRegex().IsMatch(args) || HasToken(args, "/VERYSILENT")) return null;
        var replacement = "/VERYSILENT"
                          + (HasToken(args, "/SUPPRESSMSGBOXES") ? string.Empty : " /SUPPRESSMSGBOXES")
                          + (HasToken(args, "/NORESTART") ? string.Empty : " /NORESTART");
        return quietUninstallString[..m.Groups["args"].Index] + SilentTokenRegex().Replace(args, replacement, 1);
    }

    private static bool HasToken(string args, string token) =>
        Regex.IsMatch(args, @"(?<=^|\s)" + Regex.Escape(token) + @"(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Rewrites the QuietUninstallString of every Inno entry of <paramref name="app"/> under <paramref name="uninstallRoot"/>
    /// (see the class remarks) and returns what was changed, for <see cref="Restore"/>. Never throws.
    /// </summary>
    public static IReadOnlyList<Rewrite> RewriteEntries(RegistryKey uninstallRoot, AppPolicy app, ILogger logger)
    {
        var done = new List<Rewrite>();
        string[] names;
        try { names = uninstallRoot.GetSubKeyNames(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{AppId}: the per-user Uninstall key could not be listed; quiet uninstall commands left as they are.", app.AppId);
            return done;
        }

        foreach (var name in names)
        {
            try
            {
                string? original;
                RegistryValueKind kind;
                using (var key = uninstallRoot.OpenSubKey(name, writable: false))
                {
                    if (key?.GetValue(InnoVersionValue) is null) continue;
                    original = key.GetValue(QuietUninstallValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (original is null) continue;
                    kind = key.GetValueKind(QuietUninstallValue);
                    var entry = new InstalledApp
                    {
                        DisplayName = key.GetValue("DisplayName") as string ?? string.Empty,
                        DisplayVersion = key.GetValue("DisplayVersion") as string,
                        Publisher = key.GetValue("Publisher") as string,
                        Context = InstallContext.User,
                    };
                    if (InstalledAppScanner.Match(app, [entry]).Count == 0) continue;
                }
                var rewritten = VerySilentForm(original);
                if (rewritten is null) continue;

                using (var key = uninstallRoot.OpenSubKey(name, writable: true))
                {
                    if (key is null) continue;
                    key.SetValue(QuietUninstallValue, rewritten, kind == RegistryValueKind.ExpandString ? kind : RegistryValueKind.String);
                }
                done.Add(new Rewrite(name, original, rewritten));
                logger.LogInformation("{AppId}: made the Inno uninstaller of '{Key}' very silent for this install (QuietUninstallString {Original} -> {Rewritten}), so winget's removal of the previous version shows no window.",
                    app.AppId, name, original, rewritten);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{AppId}: the quiet uninstall command of '{Key}' could not be made very silent; the install goes ahead unchanged.", app.AppId, name);
            }
        }
        return done;
    }

    /// <summary>
    /// Puts the original QuietUninstallString back on every entry that still exists and still carries exactly the value
    /// <see cref="RewriteEntries"/> wrote. An entry that is gone (the previous version was uninstalled) or that has a new
    /// value (the new installer wrote its own) is left alone. Never throws.
    /// </summary>
    public static void Restore(RegistryKey uninstallRoot, IReadOnlyList<Rewrite> rewrites, AppPolicy app, ILogger logger)
    {
        foreach (var r in rewrites)
        {
            try
            {
                using var key = uninstallRoot.OpenSubKey(r.KeyName, writable: true);
                if (key is null)
                {
                    logger.LogDebug("{AppId}: '{Key}' is gone after the install (the previous version was removed); nothing to restore.", app.AppId, r.KeyName);
                    continue;
                }
                var current = key.GetValue(QuietUninstallValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!string.Equals(current, r.Rewritten, StringComparison.Ordinal))
                {
                    logger.LogDebug("{AppId}: '{Key}' has a new quiet uninstall command after the install ({Current}); left as it is.", app.AppId, r.KeyName, current ?? "none");
                    continue;
                }
                var kind = key.GetValueKind(QuietUninstallValue);
                key.SetValue(QuietUninstallValue, r.Original, kind == RegistryValueKind.ExpandString ? kind : RegistryValueKind.String);
                logger.LogInformation("{AppId}: restored the quiet uninstall command of '{Key}' ({Original}).", app.AppId, r.KeyName, r.Original);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{AppId}: the quiet uninstall command of '{Key}' could not be restored to {Original}.", app.AppId, r.KeyName, r.Original);
            }
        }
    }

    /// <summary>
    /// Rewrites the entries now and restores them when disposed (a <c>using</c> around the install covers success, failure,
    /// cancellation and timeout). <paramref name="openRoot"/> opens the Uninstall key to work on (HKCU's, or a test key);
    /// null or a failure to open means nothing is done.
    /// </summary>
    public static IDisposable? Begin(Func<RegistryKey?> openRoot, AppPolicy app, ILogger logger)
    {
        RegistryKey? root;
        try { root = openRoot(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{AppId}: the per-user Uninstall key could not be opened; quiet uninstall commands left as they are.", app.AppId);
            return null;
        }
        if (root is null) return null;
        var rewrites = RewriteEntries(root, app, logger);
        if (rewrites.Count == 0) { root.Dispose(); return null; }
        return new Scope(root, rewrites, app, logger);
    }

    /// <summary>Opens HKCU's own Uninstall key (read access; entries are opened for writing one by one), or null when it does not exist.</summary>
    public static RegistryKey? OpenUserUninstallRoot() => Registry.CurrentUser.OpenSubKey(UserUninstallKeyPath, writable: false);

    private sealed class Scope(RegistryKey root, IReadOnlyList<Rewrite> rewrites, AppPolicy app, ILogger logger) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { Restore(root, rewrites, app, logger); }
            finally { root.Dispose(); }
        }
    }
}

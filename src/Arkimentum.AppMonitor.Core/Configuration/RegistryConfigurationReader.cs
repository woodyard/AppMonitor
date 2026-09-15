using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Configuration;

/// <summary>
/// Reads <see cref="AgentSettings"/> from the registry.
/// <para>
/// Precedence (highest first):
/// <list type="number">
/// <item>HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor   (Group Policy / Intune policy CSP)</item>
/// <item>HKLM\SOFTWARE\Arkimentum\AppMonitor            (local preference, written by the installer or an admin)</item>
/// <item>Catalog entry (for per-app values only)</item>
/// <item>Built-in default</item>
/// </list>
/// Apps live under an <c>Apps</c> subkey, one subkey per AppId. Value names are documented in docs/Registry.md.
/// </para>
/// </summary>
public sealed class RegistryConfigurationReader
{
    private readonly ILogger _logger;
    private readonly ICatalogProvider? _catalog;
    private readonly RegistryKey _hive;

    /// <param name="hive">
    /// Root hive holding SOFTWARE\Arkimentum\AppMonitor and SOFTWARE\Policies\Arkimentum\AppMonitor. Defaults to HKLM, which is
    /// the only supported location in production; tests and the <c>--user-config</c> troubleshooting switch use HKCU.
    /// </param>
    public RegistryConfigurationReader(ILogger<RegistryConfigurationReader> logger, ICatalogProvider? catalog = null, RegistryKey? hive = null, Func<SettingsDocument?>? cloudLayer = null)
    {
        _logger = logger;
        _catalog = catalog;
        _hive = hive ?? Registry.LocalMachine;
        _cloudLayer = cloudLayer;
    }

    public string HiveName => _hive.Name;

    public AgentSettings Read()
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var pref = _hive.OpenSubKey(AgentSettings.RegistryRoot, writable: false);
        using var pol = _hive.OpenSubKey(AgentSettings.PolicyRegistryRoot, writable: false);

        // Organization configuration from the cloud sits between the policy key and the local preferences. Whether it applies
        // at all (CloudConfigEnabled) is decided from the registry layers only, and it can never override the Cloud* values.
        var cloudDoc = new LayeredKey(pol, pref, new Dictionary<string, string>(), string.Empty).Bool("CloudConfigEnabled", true) ? SafeCloud() : null;
        var cloudGlobal = CloudLayer(cloudDoc?.Global, SettingsSchema.FindGlobal, excludeCloudValues: true);
        var r = new LayeredKey(pol, pref, sources, string.Empty, cloud: cloudGlobal);
        var defaults = new AgentSettings();

        var settings = defaults with
        {
            ScanIntervalMinutes = r.Int("ScanIntervalMinutes", defaults.ScanIntervalMinutes, 5, 10080),
            NotificationIntervalMinutes = r.Int("NotificationIntervalMinutes", defaults.NotificationIntervalMinutes, 1, 10080),
            StartupDelaySeconds = r.Int("StartupDelaySeconds", defaults.StartupDelaySeconds, 0, 3600),
            ScanOnStartup = r.Bool("ScanOnStartup", defaults.ScanOnStartup),
            WingetEnabled = r.Bool("WingetEnabled", defaults.WingetEnabled),
            WebSourcesEnabled = r.Bool("WebSourcesEnabled", defaults.WebSourcesEnabled),
            LaunchTrayAgent = r.Bool("LaunchTrayAgent", defaults.LaunchTrayAgent),
            NotificationsEnabled = r.Bool("NotificationsEnabled", defaults.NotificationsEnabled),
            ShowInstalledNotifications = r.Bool("ShowInstalledNotifications", defaults.ShowInstalledNotifications),
            LogLevel = r.String("LogLevel", defaults.LogLevel),
            LogDirectory = ExpandPath(r.String("LogDirectory", defaults.LogDirectory), defaults.LogDirectory),
            LogRetentionDays = r.Int("LogRetentionDays", defaults.LogRetentionDays, 1, 3650),
            MaxLogFileSizeMb = r.Int("MaxLogFileSizeMB", defaults.MaxLogFileSizeMb, 1, 1024),
            StateDirectory = ExpandPath(r.String("StateDirectory", defaults.StateDirectory), defaults.StateDirectory),
            CatalogPath = ExpandPath(r.String("CatalogPath", defaults.CatalogPath), string.Empty),
            UseCatalog = r.Bool("UseCatalog", defaults.UseCatalog),
            InstallTimeoutMinutes = r.Int("InstallTimeoutMinutes", defaults.InstallTimeoutMinutes, 1, 600),
            CheckTimeoutMinutes = r.Int("CheckTimeoutMinutes", defaults.CheckTimeoutMinutes, 1, 60),
            WingetPath = ExpandPath(r.String("WingetPath", defaults.WingetPath), string.Empty),
            AutoInstallPrerequisites = r.Bool("AutoInstallPrerequisites", defaults.AutoInstallPrerequisites),
            WingetMinimumVersion = r.String("WingetMinimumVersion", defaults.WingetMinimumVersion),
            PrerequisiteCheckIntervalHours = r.Int("PrerequisiteCheckIntervalHours", defaults.PrerequisiteCheckIntervalHours, 1, 720),
            CloudServerUrl = r.String("CloudServerUrl", defaults.CloudServerUrl),
            CloudOrganizationId = r.String("CloudOrganizationId", defaults.CloudOrganizationId),
            CloudEnrollmentKey = r.String("CloudEnrollmentKey", defaults.CloudEnrollmentKey),
            CloudSyncIntervalMinutes = r.Int("CloudSyncIntervalMinutes", defaults.CloudSyncIntervalMinutes, 1, 1440),
            CloudConfigEnabled = r.Bool("CloudConfigEnabled", defaults.CloudConfigEnabled),
            CloudReportingEnabled = r.Bool("CloudReportingEnabled", defaults.CloudReportingEnabled),
            AgentAutoUpdate = r.Bool("AgentAutoUpdate", defaults.AgentAutoUpdate),
            AgentUpdateFeedUrl = r.String("AgentUpdateFeedUrl", defaults.AgentUpdateFeedUrl),
            AgentUpdateChannel = r.String("AgentUpdateChannel", defaults.AgentUpdateChannel),
            AgentUpdateCheckIntervalHours = r.Int("AgentUpdateCheckIntervalHours", defaults.AgentUpdateCheckIntervalHours, 1, 720),
            AgentTargetVersion = r.String("AgentTargetVersion", defaults.AgentTargetVersion),
            ProxyUrl = r.String("ProxyUrl", defaults.ProxyUrl),
            WingetGlobalArgs = r.String("WingetGlobalArgs", defaults.WingetGlobalArgs),
            WingetIncludeUnknown = r.Bool("WingetIncludeUnknown", defaults.WingetIncludeUnknown),
            PolicyTickSeconds = r.Int("PolicyTickSeconds", defaults.PolicyTickSeconds, 10, 600),
            DefaultMandatory = r.Bool("DefaultMandatory", defaults.DefaultMandatory),
            DefaultDeadlineHours = r.Int("DefaultDeadlineHours", defaults.DefaultDeadlineHours, 0, 8760),
            DefaultMaxDeferrals = r.Int("DefaultMaxDeferrals", defaults.DefaultMaxDeferrals, 0, 1000),
            DefaultDeferralOptionsMinutes = r.IntList("DefaultDeferralOptions", defaults.DefaultDeferralOptionsMinutes),
            DefaultAutoInstall = r.Bool("DefaultAutoInstall", defaults.DefaultAutoInstall),
            DefaultCloseGracePeriodMinutes = r.Int("DefaultCloseGracePeriodMinutes", defaults.DefaultCloseGracePeriodMinutes, 0, 1440),
            DefaultForceCloseAtDeadline = r.Bool("DefaultForceCloseAtDeadline", defaults.DefaultForceCloseAtDeadline),
        };

        var enableAllCatalogApps = r.Bool("EnableAllCatalogApps", false);
        var apps = ReadApps(pol, pref, settings, sources, enableAllCatalogApps, cloudDoc);

        return settings with { Apps = apps, ValueSources = sources, LoadedAtUtc = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// Parses the flat "AppList" format: a key whose value names are AppIds and whose string data is a
    /// semicolon-separated list of Name=Value pairs, e.g. <c>Source=winget;WingetId=7zip.7zip;Mandatory=1;DeadlineHours=72</c>.
    /// This format can be produced by Group Policy list elements (ADMX) and Intune, which cannot create nested keys.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, object>> ReadAppList(RegistryKey? root)
    {
        var result = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        using var key = root?.OpenSubKey("AppList");
        if (key is null) return result;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (key.GetValue(name) is not string data) continue;
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var k = pair[..eq].Trim();
                var v = pair[(eq + 1)..].Trim();
                // ProcessNames may use '|' instead of ',' so the whole entry stays one value
                if (k.Equals("ProcessNames", StringComparison.OrdinalIgnoreCase) || k.Equals("DeferralOptions", StringComparison.OrdinalIgnoreCase))
                    v = v.Replace('|', ',');
                values[k] = v;
            }
            result[name.Trim()] = values;
        }
        return result;
    }

    private readonly Func<SettingsDocument?>? _cloudLayer;

    private SettingsDocument? SafeCloud()
    {
        try { return _cloudLayer?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "The organization configuration layer could not be read; ignoring it"); return null; }
    }

    /// <summary>Converts one SettingsDocument value bag into registry-shaped values keyed by value name (unknown names dropped).</summary>
    private static Dictionary<string, object>? CloudLayer(IDictionary<string, SettingValue>? values, Func<string, SettingDefinition?> find, bool excludeCloudValues)
    {
        if (values is null || values.Count == 0) return null;
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (excludeCloudValues && name.StartsWith("Cloud", StringComparison.OrdinalIgnoreCase)) continue;
            var def = find(name);
            if (def is null) continue;
            result[def.Name] = value.ToRegistryValue(def);
        }
        return result.Count == 0 ? null : result;
    }

    private IReadOnlyList<AppPolicy> ReadApps(RegistryKey? pol, RegistryKey? pref, AgentSettings global,
        Dictionary<string, string> sources, bool enableAllCatalogApps, SettingsDocument? cloudDoc)
    {
        using var polApps = pol?.OpenSubKey("Apps");
        using var prefApps = pref?.OpenSubKey("Apps");
        var polList = ReadAppList(pol);
        var prefList = ReadAppList(pref);

        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (polApps is not null) ids.UnionWith(polApps.GetSubKeyNames());
        if (prefApps is not null) ids.UnionWith(prefApps.GetSubKeyNames());
        ids.UnionWith(polList.Keys);
        ids.UnionWith(prefList.Keys);
        if (cloudDoc is not null) ids.UnionWith(cloudDoc.Apps.Keys);

        var catalog = global.UseCatalog ? _catalog?.GetCatalog(global.CatalogPath) : null;
        if (enableAllCatalogApps && catalog is not null)
        {
            foreach (var c in catalog) ids.Add(c.AppId);
        }

        var result = new List<AppPolicy>();
        foreach (var id in ids)
        {
            try
            {
                using var p = polApps?.OpenSubKey(id);
                using var q = prefApps?.OpenSubKey(id);
                var catalogEntry = catalog?.FirstOrDefault(c => c.AppId.Equals(id, StringComparison.OrdinalIgnoreCase));
                polList.TryGetValue(id, out var pl);
                prefList.TryGetValue(id, out var ql);
                Dictionary<string, object>? cloudApp = null;
                if (cloudDoc is not null && cloudDoc.Apps.TryGetValue(id, out var cloudValues))
                    cloudApp = CloudLayer(cloudValues, SettingsSchema.FindApp, excludeCloudValues: false);
                var app = ReadApp(id, new LayeredKey(p, q, sources, $"Apps\\{id}\\", pl, ql, cloudApp), catalogEntry, global);
                if (app is null) continue;
                result.Add(app);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read configuration for app {AppId}; skipping", id);
            }
        }

        return result;
    }

    private AppPolicy? ReadApp(string id, LayeredKey r, AppPolicy? catalog, AgentSettings global)
    {
        var baseline = catalog ?? new AppPolicy { AppId = id };

        var sourceText = r.String("Source", baseline.Source == UpdateSource.Web ? "web" : "winget");
        var source = sourceText.Trim().ToLowerInvariant() switch
        {
            "web" or "site" or "official" or "url" => UpdateSource.Web,
            _ => UpdateSource.Winget,
        };

        var contextText = r.String("Context", baseline.Context.ToString());
        var context = contextText.Trim().ToLowerInvariant() switch
        {
            "system" or "machine" or "device" => InstallContext.System,
            "user" => InstallContext.User,
            _ => InstallContext.Auto,
        };

        var installerTypeText = r.String("InstallerType", baseline.InstallerType.ToString());
        var installerType = installerTypeText.Trim().ToLowerInvariant() switch
        {
            "msi" => InstallerType.Msi,
            "msix" or "appx" => InstallerType.Msix,
            _ => InstallerType.Exe,
        };

        var app = new AppPolicy
        {
            AppId = id,
            Enabled = r.Bool("Enabled", baseline.Enabled),
            DisplayName = r.String("DisplayName", string.IsNullOrWhiteSpace(baseline.DisplayName) ? id : baseline.DisplayName),
            Source = source,
            Context = context,
            WingetId = r.OptString("WingetId") ?? baseline.WingetId,
            WingetSourceName = r.String("WingetSource", baseline.WingetSourceName),
            WingetExtraArgs = r.OptString("WingetExtraArgs") ?? baseline.WingetExtraArgs,
            VersionUrl = r.OptString("VersionUrl") ?? baseline.VersionUrl,
            VersionRegex = r.OptString("VersionRegex") ?? baseline.VersionRegex,
            DownloadUrl = r.OptString("DownloadUrl") ?? baseline.DownloadUrl,
            InstallerArgs = r.OptString("InstallerArgs") ?? baseline.InstallerArgs,
            InstallerType = installerType,
            Sha256Url = r.OptString("Sha256Url") ?? baseline.Sha256Url,
            Sha256 = r.OptString("Sha256") ?? baseline.Sha256,
            UserDownloadUrl = r.OptString("UserDownloadUrl") ?? baseline.UserDownloadUrl,
            UserInstallerArgs = r.OptString("UserInstallerArgs") ?? baseline.UserInstallerArgs,
            DetectDisplayNameRegex = r.OptString("DetectDisplayNameRegex") ?? baseline.DetectDisplayNameRegex,
            DetectPublisherRegex = r.OptString("DetectPublisherRegex") ?? baseline.DetectPublisherRegex,
            DetectFilePath = r.OptString("DetectFilePath") ?? baseline.DetectFilePath,
            // Behaviour is policy, not catalog data: registry value, else the global Default* value.
            Mandatory = r.Bool("Mandatory", global.DefaultMandatory),
            DeadlineHours = r.Int("DeadlineHours", global.DefaultDeadlineHours, 0, 8760),
            MaxDeferrals = r.Int("MaxDeferrals", global.DefaultMaxDeferrals, 0, 1000),
            DeferralOptionsMinutes = r.IntList("DeferralOptions", global.DefaultDeferralOptionsMinutes),
            ProcessNames = r.StringList("ProcessNames", baseline.ProcessNames),
            AutoInstall = r.Bool("AutoInstall", global.DefaultAutoInstall),
            CloseGracePeriodMinutes = r.Int("CloseGracePeriodMinutes", global.DefaultCloseGracePeriodMinutes, 0, 1440),
            ForceCloseAtDeadline = r.Bool("ForceCloseAtDeadline", global.DefaultForceCloseAtDeadline),
            MinimumVersion = r.OptString("MinimumVersion") ?? baseline.MinimumVersion,
            NotificationIntervalMinutes = r.OptInt("NotificationIntervalMinutes"),
        };

        if (app.Source == UpdateSource.Winget && string.IsNullOrWhiteSpace(app.WingetId))
        {
            _logger.LogWarning("App {AppId} uses the winget source but has no WingetId; skipping", id);
            return null;
        }
        if (app.Source == UpdateSource.Web && (string.IsNullOrWhiteSpace(app.VersionUrl) || string.IsNullOrWhiteSpace(app.DownloadUrl)))
        {
            _logger.LogWarning("App {AppId} uses the web source but lacks VersionUrl/DownloadUrl; skipping", id);
            return null;
        }
        return app;
    }

    private static string ExpandPath(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try { return Environment.ExpandEnvironmentVariables(value.Trim()); }
        catch { return fallback; }
    }

    /// <summary>
    /// Reads a value in precedence order: policy subkey, policy AppList entry, organization configuration from the cloud,
    /// preference subkey, preference AppList entry.
    /// </summary>
    private sealed class LayeredKey(RegistryKey? policy, RegistryKey? preference, Dictionary<string, string> sources, string prefix,
        Dictionary<string, object>? policyList = null, Dictionary<string, object>? preferenceList = null, Dictionary<string, object>? cloud = null)
    {
        private object? Get(string name, out string source)
        {
            if (policy?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } p) { source = "Policy"; return p; }
            if (policyList is not null && policyList.TryGetValue(name, out var pl)) { source = "PolicyAppList"; return pl; }
            if (cloud is not null && cloud.TryGetValue(name, out var c)) { source = "Cloud"; return c; }
            if (preference?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } q) { source = "Preference"; return q; }
            if (preferenceList is not null && preferenceList.TryGetValue(name, out var ql)) { source = "PreferenceAppList"; return ql; }
            source = "Default";
            return null;
        }

        private void Record(string name, string source) => sources[prefix + name] = source;

        public int Int(string name, int fallback, int min, int max)
        {
            var v = Get(name, out var src);
            Record(name, src);
            return v switch
            {
                int i => Math.Clamp(i, min, max),
                long l => (int)Math.Clamp(l, min, max),
                string s when int.TryParse(s, out var i) => Math.Clamp(i, min, max),
                _ => fallback,
            };
        }

        public int? OptInt(string name)
        {
            var v = Get(name, out var src);
            if (v is null) return null;
            Record(name, src);
            return v switch { int i => i, long l => (int)l, string s when int.TryParse(s, out var i) => i, _ => null };
        }

        public bool Bool(string name, bool fallback)
        {
            var v = Get(name, out var src);
            Record(name, src);
            return v switch
            {
                int i => i != 0,
                long l => l != 0,
                string s when bool.TryParse(s, out var b) => b,
                string s when int.TryParse(s, out var i) => i != 0,
                _ => fallback,
            };
        }

        public string String(string name, string fallback)
        {
            var v = Get(name, out var src);
            Record(name, src);
            return v switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
                string[] a when a.Length > 0 => string.Join(",", a),
                int i => i.ToString(),
                _ => fallback,
            };
        }

        public string? OptString(string name)
        {
            var v = Get(name, out var src);
            if (v is null) return null;
            Record(name, src);
            return v switch
            {
                string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
                string[] a => a.Length == 0 ? null : string.Join(",", a),
                _ => v.ToString(),
            };
        }

        public IReadOnlyList<int> IntList(string name, IReadOnlyList<int> fallback)
        {
            var v = Get(name, out var src);
            Record(name, src);
            var parts = v switch
            {
                string s => s.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                string[] a => a,
                _ => null,
            };
            if (parts is null) return fallback;
            var list = parts.Select(p => int.TryParse(p, out var i) ? i : -1).Where(i => i > 0).Distinct().OrderBy(i => i).ToList();
            return list.Count > 0 ? list : fallback;
        }

        public IReadOnlyList<string> StringList(string name, IReadOnlyList<string> fallback)
        {
            var v = Get(name, out var src);
            Record(name, src);
            var parts = v switch
            {
                string s => s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                string[] a => a,
                _ => null,
            };
            if (parts is null) return fallback;
            var list = parts.Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return list.Count > 0 ? list : fallback;
        }
    }
}

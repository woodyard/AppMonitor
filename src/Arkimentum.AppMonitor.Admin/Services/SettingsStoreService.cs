using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// The console's single door to the registry: one <see cref="RegistrySettingsStore"/> bound to the chosen hive
/// (HKLM normally, HKCU under <c>--user-config</c>), with every write logged.
/// </summary>
public sealed class SettingsStoreService
{
    private readonly ILogger<SettingsStoreService> _log;
    private readonly RegistryKey _hive;

    public SettingsStoreService(ILogger<SettingsStoreService> log, CommandLineOptions options)
    {
        _log = log;
        UserConfig = options.UserConfig;
        _hive = UserConfig ? Registry.CurrentUser : Registry.LocalMachine;
        Store = new RegistrySettingsStore(_hive);
    }

    public RegistrySettingsStore Store { get; }

    /// <summary>True when the console is bound to HKCU for testing.</summary>
    public bool UserConfig { get; }

    public RegistryKey Hive => _hive;

    public string PreferencePath => Store.FullPathFor(SettingsLayer.Preference);

    public string PolicyPath => Store.FullPathFor(SettingsLayer.Policy);

    public SettingsDocument Read(SettingsLayer layer = SettingsLayer.Preference) => Store.Read(layer);

    public void Write(SettingsDocument document, SettingsLayer layer = SettingsLayer.Preference,
        SettingsWriteMode mode = SettingsWriteMode.Replace)
    {
        Store.Write(document, layer, mode);
        _log.LogInformation("Wrote {Global} global value(s) and {Apps} application(s) to {Key} ({Mode}).",
            document.Global.Count, document.Apps.Count, Store.FullPathFor(layer), mode);
    }

    /// <summary>The merged, effective configuration (policy &gt; preference &gt; catalog &gt; default).</summary>
    public AgentSettings ReadEffective(ILoggerFactory loggers, ICatalogProvider? catalog)
    {
        try
        {
            var reader = new RegistryConfigurationReader(loggers.CreateLogger<RegistryConfigurationReader>(), catalog, _hive);
            return reader.Read();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading the effective configuration failed; falling back to the built-in defaults.");
            return new AgentSettings();
        }
    }

    /// <summary>
    /// Refuses to run on a machine where the preference layer cannot be opened for writing: the console exists to
    /// write it, and failing later — half-way through an Apply — would be worse.
    /// </summary>
    public bool CanWrite(out string error)
    {
        error = string.Empty;
        try
        {
            var path = RegistrySettingsStore.PathFor(SettingsLayer.Preference);
            using var existing = _hive.OpenSubKey(path, writable: true);
            if (existing is not null) return true;
            using var created = _hive.CreateSubKey(path, writable: true);
            if (created is not null) return true;
            error = "The key could not be created.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

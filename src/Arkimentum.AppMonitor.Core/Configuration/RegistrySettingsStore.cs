using Arkimentum.AppMonitor.Models;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Configuration;

public enum SettingsLayer
{
    /// <summary>HKLM\SOFTWARE\Arkimentum\AppMonitor - local preferences, editable by administrators.</summary>
    Preference,
    /// <summary>HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor - managed by Group Policy / Intune, read-only for the admin app.</summary>
    Policy,
}

public enum SettingsWriteMode
{
    /// <summary>Only set the values present in the document.</summary>
    Merge,
    /// <summary>Make the layer identical to the document: remove values and app keys that are not in it.</summary>
    Replace,
}

/// <summary>
/// Reads and writes the raw registry values of one configuration layer as a <see cref="SettingsDocument"/>.
/// Used by the admin console (writing requires administrative rights for HKLM) and by tests (HKCU hive override).
/// </summary>
public sealed class RegistrySettingsStore
{
    private readonly RegistryKey _hive;

    public RegistrySettingsStore(RegistryKey? hive = null) => _hive = hive ?? Registry.LocalMachine;

    public static string PathFor(SettingsLayer layer) =>
        layer == SettingsLayer.Policy ? AgentSettings.PolicyRegistryRoot : AgentSettings.RegistryRoot;

    public string FullPathFor(SettingsLayer layer) => $"{_hive.Name}\\{PathFor(layer)}";

    /// <summary>True when the layer's key exists.</summary>
    public bool Exists(SettingsLayer layer)
    {
        using var key = _hive.OpenSubKey(PathFor(layer));
        return key is not null;
    }

    /// <summary>Reads the values present in the layer. Unknown value names are ignored; app subkeys and AppList entries are both read.</summary>
    public SettingsDocument Read(SettingsLayer layer)
    {
        var doc = new SettingsDocument();
        using var root = _hive.OpenSubKey(PathFor(layer));
        if (root is null) return doc;

        foreach (var def in SettingsSchema.Global)
        {
            var v = SettingValue.FromRegistryValue(def, root.GetValue(def.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            if (v is not null) doc.Global[def.Name] = v;
        }

        // Flat AppList entries first, then Apps\<id> subkeys override (same precedence as the reader).
        foreach (var (appId, values) in RegistryConfigurationReader.ReadAppList(root))
        {
            var target = doc.GetOrAddApp(appId);
            foreach (var (name, raw) in values)
            {
                var def = SettingsSchema.FindApp(name);
                if (def is null) continue;
                var v = SettingValue.FromRegistryValue(def, raw);
                if (v is not null) target[def.Name] = v;
            }
        }

        using var apps = root.OpenSubKey("Apps");
        if (apps is not null)
        {
            foreach (var appId in apps.GetSubKeyNames())
            {
                using var key = apps.OpenSubKey(appId);
                if (key is null) continue;
                var target = doc.GetOrAddApp(appId);
                foreach (var def in SettingsSchema.App)
                {
                    var v = SettingValue.FromRegistryValue(def, key.GetValue(def.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                    if (v is not null) target[def.Name] = v;
                }
            }
        }
        return doc;
    }

    /// <summary>
    /// Writes the document to the layer. Values are written with the registry type from <see cref="SettingsSchema"/>.
    /// In <see cref="SettingsWriteMode.Replace"/> mode, known global values and app subkeys absent from the document are removed
    /// (unknown/extra values are left alone). AppList entries are removed for apps that are written as subkeys.
    /// </summary>
    public void Write(SettingsDocument document, SettingsLayer layer = SettingsLayer.Preference, SettingsWriteMode mode = SettingsWriteMode.Replace)
    {
        var problems = document.Validate();
        if (problems.Count > 0) throw new InvalidDataException("Settings are not valid: " + string.Join(" ", problems));

        using var root = _hive.CreateSubKey(PathFor(layer), writable: true) ?? throw new UnauthorizedAccessException($"Cannot open {FullPathFor(layer)} for writing.");

        foreach (var def in SettingsSchema.Global)
        {
            if (document.Global.TryGetValue(def.Name, out var value)) root.SetValue(def.Name, value.ToRegistryValue(def), def.RegistryKind);
            else if (mode == SettingsWriteMode.Replace) root.DeleteValue(def.Name, throwOnMissingValue: false);
        }

        using var apps = root.CreateSubKey("Apps", writable: true)!;
        if (mode == SettingsWriteMode.Replace)
        {
            foreach (var existing in apps.GetSubKeyNames())
            {
                if (!document.Apps.ContainsKey(existing)) apps.DeleteSubKeyTree(existing, throwOnMissingSubKey: false);
            }
        }

        using var appList = root.OpenSubKey("AppList", writable: true);
        foreach (var (appId, values) in document.Apps)
        {
            using var key = apps.CreateSubKey(appId, writable: true)!;
            foreach (var def in SettingsSchema.App)
            {
                if (values.TryGetValue(def.Name, out var value)) key.SetValue(def.Name, value.ToRegistryValue(def), def.RegistryKind);
                else if (mode == SettingsWriteMode.Replace) key.DeleteValue(def.Name, throwOnMissingValue: false);
            }
            appList?.DeleteValue(appId, throwOnMissingValue: false);
        }
        if (mode == SettingsWriteMode.Replace && appList is not null)
        {
            foreach (var name in appList.GetValueNames())
            {
                if (!document.Apps.ContainsKey(name)) appList.DeleteValue(name, throwOnMissingValue: false);
            }
        }
    }

    public void DeleteApp(string appId, SettingsLayer layer = SettingsLayer.Preference)
    {
        using var root = _hive.OpenSubKey(PathFor(layer), writable: true);
        if (root is null) return;
        using (var apps = root.OpenSubKey("Apps", writable: true)) apps?.DeleteSubKeyTree(appId, throwOnMissingSubKey: false);
        using (var list = root.OpenSubKey("AppList", writable: true)) list?.DeleteValue(appId, throwOnMissingValue: false);
    }

    /// <summary>Removes the whole preference layer (used by "reset to defaults").</summary>
    public void Clear(SettingsLayer layer = SettingsLayer.Preference)
    {
        var path = PathFor(layer);
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        using var p = _hive.OpenSubKey(parent, writable: true);
        p?.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// Effective view for the admin UI: for every known setting, the preference value, the policy value (when locked) and the default.
    /// </summary>
    public IReadOnlyList<EffectiveSetting> Describe()
    {
        var pref = Read(SettingsLayer.Preference);
        var pol = Read(SettingsLayer.Policy);
        var list = new List<EffectiveSetting>();
        foreach (var def in SettingsSchema.Global)
        {
            pref.Global.TryGetValue(def.Name, out var p);
            pol.Global.TryGetValue(def.Name, out var q);
            list.Add(new EffectiveSetting(def, p, q));
        }
        return list;
    }
}

/// <summary>One global setting with its layered values, for display and editing.</summary>
public sealed record EffectiveSetting(SettingDefinition Definition, SettingValue? Preference, SettingValue? Policy)
{
    public bool IsLockedByPolicy => Policy is not null;
    public SettingValue? Effective => Policy ?? Preference;
    public string Source => Policy is not null ? "Policy" : Preference is not null ? "Preference" : "Default";
}

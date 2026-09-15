namespace Arkimentum.AppMonitor.Configuration;

/// <summary>
/// The organization configuration as it applies on one machine. The agent layers the cached organization document
/// between the policy key and the local preferences (see <see cref="RegistryConfigurationReader"/>); this is the same
/// rule, exposed so the admin console can show which local values the organization overrides.
/// </summary>
public static class OrganizationLayer
{
    /// <summary>
    /// Whether the organization configuration is applied at all: <c>CloudConfigEnabled</c>, policy over preference,
    /// default true. Decided from the registry layers only - the organization cannot switch itself on or off.
    /// </summary>
    public static bool AppliesOn(SettingsDocument policy, SettingsDocument preference)
    {
        var value = policy.Global.TryGetValue("CloudConfigEnabled", out var p) ? p
            : preference.Global.TryGetValue("CloudConfigEnabled", out var q) ? q
            : null;
        return value?.AsBool() ?? true;
    }

    /// <summary>
    /// The part of the organization document that overrides local preferences on this machine, or null when it does
    /// not apply (nothing cached, or <c>CloudConfigEnabled</c> is off). The <c>Cloud*</c> connection values are
    /// dropped: the organization can never override how the device reaches it.
    /// </summary>
    public static SettingsDocument? Effective(SettingsDocument policy, SettingsDocument preference, SettingsDocument? organization)
    {
        if (organization is null || !AppliesOn(policy, preference)) return null;

        var result = new SettingsDocument();
        foreach (var (name, value) in organization.Global)
        {
            if (name.StartsWith("Cloud", StringComparison.OrdinalIgnoreCase)) continue;
            result.Global[name] = value;
        }
        foreach (var (appId, values) in organization.Apps)
        {
            var app = result.GetOrAddApp(appId);
            foreach (var (name, value) in values) app[name] = value;
        }
        return result;
    }
}

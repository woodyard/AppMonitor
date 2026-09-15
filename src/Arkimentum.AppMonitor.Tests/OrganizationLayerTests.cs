using Arkimentum.AppMonitor.Configuration;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The admin console greys out local values the organization overrides. It must agree with the agent about when
/// the organization layer applies and which values it can carry.
/// </summary>
public class OrganizationLayerTests
{
    private static SettingsDocument Doc(params (string Name, SettingValue Value)[] globals)
    {
        var doc = new SettingsDocument();
        foreach (var (name, value) in globals) doc.Global[name] = value;
        return doc;
    }

    private static SettingsDocument Organization()
    {
        var doc = Doc(("ScanIntervalMinutes", SettingValue.From(60)), ("CloudServerUrl", SettingValue.From("https://evil.example")),
            ("CloudSyncIntervalMinutes", SettingValue.From(1)));
        doc.GetOrAddApp("7zip")["Mandatory"] = SettingValue.From(true);
        return doc;
    }

    [Fact]
    public void Nothing_cached_means_no_layer()
    {
        Assert.Null(OrganizationLayer.Effective(new SettingsDocument(), new SettingsDocument(), null));
    }

    [Fact]
    public void Applies_by_default_and_strips_the_connection_values()
    {
        var effective = OrganizationLayer.Effective(new SettingsDocument(), new SettingsDocument(), Organization());

        Assert.NotNull(effective);
        Assert.Equal(60, effective!.Global["ScanIntervalMinutes"].AsInt());
        Assert.False(effective.Global.ContainsKey("CloudServerUrl"));
        Assert.False(effective.Global.ContainsKey("CloudSyncIntervalMinutes"));
        Assert.True(effective.Apps["7zip"]["Mandatory"].AsBool());
    }

    [Fact]
    public void Preference_can_switch_it_off()
    {
        var preference = Doc(("CloudConfigEnabled", SettingValue.From(false)));
        Assert.Null(OrganizationLayer.Effective(new SettingsDocument(), preference, Organization()));
    }

    [Fact]
    public void Policy_wins_over_preference()
    {
        var policy = Doc(("CloudConfigEnabled", SettingValue.From(true)));
        var preference = Doc(("CloudConfigEnabled", SettingValue.From(false)));
        Assert.NotNull(OrganizationLayer.Effective(policy, preference, Organization()));

        policy = Doc(("CloudConfigEnabled", SettingValue.From(false)));
        preference = Doc(("CloudConfigEnabled", SettingValue.From(true)));
        Assert.Null(OrganizationLayer.Effective(policy, preference, Organization()));
    }

    [Fact]
    public void Registry_style_bool_text_is_understood()
    {
        var preference = Doc(("CloudConfigEnabled", SettingValue.From("0")));
        Assert.False(OrganizationLayer.AppliesOn(new SettingsDocument(), preference));
    }
}

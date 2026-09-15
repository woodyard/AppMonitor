using Xunit;
using ApiWire = global::Arkimentum.AppMonitor.Api.Contracts;
using CoreConfig = global::Arkimentum.AppMonitor.Configuration;

namespace Arkimentum.AppMonitor.Api.Tests;

/// <summary>
/// The API must validate organization configuration exactly as the agent does. Each case below is run through both
/// implementations and the problem lists must agree, message for message - so porting drift is caught here.
/// </summary>
public sealed class SettingsValidationTests
{
    public static TheoryData<string, string, object, bool> GlobalCases => new()
    {
        // scope,   name,                        value,             expected valid
        { "global", "ScanIntervalMinutes",       240,               true },
        { "global", "ScanIntervalMinutes",       5,                 true },
        { "global", "ScanIntervalMinutes",       10080,             true },
        { "global", "ScanIntervalMinutes",       4,                 false },
        { "global", "ScanIntervalMinutes",       10081,             false },
        { "global", "ScanIntervalMinutes",       "not-a-number",    false },
        { "global", "ScanOnStartup",             true,              true },
        { "global", "ScanOnStartup",             "1",               true },
        { "global", "ScanOnStartup",             "maybe",           false },
        { "global", "LogLevel",                  "Information",     true },
        { "global", "LogLevel",                  "information",     true },
        { "global", "LogLevel",                  "Verbose",         false },
        { "global", "DefaultDeferralOptions",    "60,240,1440",     true },
        { "global", "DefaultDeferralOptions",    "0",               false },
        { "global", "DefaultDeferralOptions",    "",                false },
        { "global", "StartupDelaySeconds",       0,                 true },
        { "global", "StartupDelaySeconds",       3601,              false },
        { "global", "CloudSyncIntervalMinutes",  15,                true },
        { "global", "CloudSyncIntervalMinutes",  0,                 false },
        { "global", "WingetMinimumVersion",      "1.6.0",           true },
        { "global", "NoSuchSetting",             1,                 false },

        { "app",    "Source",                    "winget",          true },
        { "app",    "Source",                    "web",             true },
        { "app",    "Source",                    "ftp",             false },
        { "app",    "Context",                   "auto",            true },
        { "app",    "Context",                   "machine",         false },
        { "app",    "InstallerType",             "msi",             true },
        { "app",    "InstallerType",             "appx",            false },
        { "app",    "DeadlineHours",             0,                 true },
        { "app",    "DeadlineHours",             8760,              true },
        { "app",    "DeadlineHours",             8761,              false },
        { "app",    "MaxDeferrals",              1001,              false },
        { "app",    "NotificationIntervalMinutes", 0,               false },
        { "app",    "Mandatory",                 true,              true },
        { "app",    "DeferralOptions",           "60;240",          true },
        { "app",    "DeferralOptions",           "abc",             false },
        { "app",    "WingetId",                  "Google.Chrome",   true },
        { "app",    "NoSuchAppSetting",          "x",               false },
    };

    [Theory]
    [MemberData(nameof(GlobalCases))]
    public void ApiValidation_MatchesCore(string scope, string name, object value, bool expectedValid)
    {
        var apiProblems = ValidateWithApi(scope, name, value);
        var coreProblems = ValidateWithCore(scope, name, value);

        Assert.Equal(coreProblems, apiProblems);
        Assert.Equal(expectedValid, apiProblems.Count == 0);
    }

    [Fact]
    public void AppIdWithASeparator_IsRejectedByBoth()
    {
        var api = new ApiWire.SettingsDocument();
        api.GetOrAddApp("apps\\chrome")["Mandatory"] = ApiWire.SettingValue.From(true);

        var core = new CoreConfig.SettingsDocument();
        core.GetOrAddApp("apps\\chrome")["Mandatory"] = CoreConfig.SettingValue.From(true);

        Assert.Equal(core.Validate(), api.Validate());
        Assert.Contains(api.Validate(), p => p.Contains("Invalid AppId"));
    }

    [Fact]
    public void ACompleteRealisticDocument_IsValid()
    {
        var document = new ApiWire.SettingsDocument { Description = "Contoso baseline" };
        document.Global["ScanIntervalMinutes"] = ApiWire.SettingValue.From(240);
        document.Global["NotificationsEnabled"] = ApiWire.SettingValue.From(true);
        document.Global["DefaultDeferralOptions"] = ApiWire.SettingValue.From("60,240,1440");
        document.Global["LogLevel"] = ApiWire.SettingValue.From("Warning");
        document.Global["AutoInstallPrerequisites"] = ApiWire.SettingValue.From(true);

        var chrome = document.GetOrAddApp("chrome");
        chrome["Enabled"] = ApiWire.SettingValue.From(true);
        chrome["Source"] = ApiWire.SettingValue.From("winget");
        chrome["WingetId"] = ApiWire.SettingValue.From("Google.Chrome");
        chrome["Mandatory"] = ApiWire.SettingValue.From(true);
        chrome["DeadlineHours"] = ApiWire.SettingValue.From(72);
        chrome["ProcessNames"] = ApiWire.SettingValue.From(new[] { "chrome", "chrome_proxy" });

        var firefox = document.GetOrAddApp("firefox");
        firefox["Source"] = ApiWire.SettingValue.From("web");
        firefox["VersionUrl"] = ApiWire.SettingValue.From("https://product-details.mozilla.org/1.0/firefox_versions.json");
        firefox["VersionRegex"] = ApiWire.SettingValue.From("\"LATEST_FIREFOX_VERSION\":\\s*\"(?<version>[^\"]+)\"");
        firefox["DownloadUrl"] = ApiWire.SettingValue.From("https://download.mozilla.org/?product=firefox-{version}");
        firefox["InstallerType"] = ApiWire.SettingValue.From("exe");
        firefox["InstallerArgs"] = ApiWire.SettingValue.From("-ms");

        Assert.Empty(document.Validate());
    }

    [Fact]
    public void EveryGlobalDefault_PassesValidation()
    {
        foreach (var definition in ApiWire.SettingsSchema.Global)
        {
            if (definition.Default is null) continue;
            var document = new ApiWire.SettingsDocument();
            document.Global[definition.Name] = ToValue(definition.Default);
            Assert.Empty(document.Validate());
        }
    }

    [Fact]
    public void UnsupportedSchema_IsRejected()
    {
        var json = "{\"$schema\":\"arkimentum-appmonitor-settings/2\",\"global\":{},\"apps\":{}}";
        Assert.Throws<InvalidDataException>(() => ApiWire.SettingsDocument.FromJson(json));
    }

    private static IReadOnlyList<string> ValidateWithApi(string scope, string name, object value)
    {
        var document = new ApiWire.SettingsDocument();
        if (scope == "global") document.Global[name] = ToValue(value);
        else document.GetOrAddApp("chrome")[name] = ToValue(value);
        return document.Validate();
    }

    private static IReadOnlyList<string> ValidateWithCore(string scope, string name, object value)
    {
        var document = new CoreConfig.SettingsDocument();
        if (scope == "global") document.Global[name] = ToCoreValue(value);
        else document.GetOrAddApp("chrome")[name] = ToCoreValue(value);
        return document.Validate();
    }

    private static ApiWire.SettingValue ToValue(object value) => value switch
    {
        bool b => ApiWire.SettingValue.From(b),
        int i => ApiWire.SettingValue.From(i),
        long l => ApiWire.SettingValue.From(l),
        string[] a => ApiWire.SettingValue.From(a),
        string s => ApiWire.SettingValue.From(s),
        _ => ApiWire.SettingValue.From(value.ToString() ?? string.Empty),
    };

    private static CoreConfig.SettingValue ToCoreValue(object value) => value switch
    {
        bool b => CoreConfig.SettingValue.From(b),
        int i => CoreConfig.SettingValue.From(i),
        long l => CoreConfig.SettingValue.From(l),
        string[] a => CoreConfig.SettingValue.From(a),
        string s => CoreConfig.SettingValue.From(s),
        _ => CoreConfig.SettingValue.From(value.ToString() ?? string.Empty),
    };
}

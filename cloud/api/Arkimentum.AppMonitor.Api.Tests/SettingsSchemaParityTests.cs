using Xunit;
using ApiWire = global::Arkimentum.AppMonitor.Api.Contracts;
using CoreConfig = global::Arkimentum.AppMonitor.Configuration;

namespace Arkimentum.AppMonitor.Api.Tests;

/// <summary>
/// The API's copy of SettingsSchema is what decides whether an administrator's configuration is accepted, so it must
/// list exactly the same values, kinds, ranges, choices and defaults as the schema the agent reads the registry with.
/// </summary>
public sealed class SettingsSchemaParityTests
{
    [Fact]
    public void GlobalDefinitions_MatchCore() =>
        AssertParity(ApiWire.SettingsSchema.Global, CoreConfig.SettingsSchema.Global);

    [Fact]
    public void AppDefinitions_MatchCore() =>
        AssertParity(ApiWire.SettingsSchema.App, CoreConfig.SettingsSchema.App);

    [Fact]
    public void SchemaIdentifier_MatchesCore() =>
        Assert.Equal(CoreConfig.SettingsDocument.CurrentSchema, ApiWire.SettingsDocument.CurrentSchema);

    [Fact]
    public void SettingKind_HasTheSameMembersAsCore() =>
        Assert.Equal(
            Enum.GetNames<CoreConfig.SettingKind>().OrderBy(n => n, StringComparer.Ordinal),
            Enum.GetNames<ApiWire.SettingKind>().OrderBy(n => n, StringComparer.Ordinal));

    [Fact]
    public void ChoiceLists_MatchCore()
    {
        Assert.Equal(CoreConfig.SettingsSchema.LogLevels, ApiWire.SettingsSchema.LogLevels);
        Assert.Equal(CoreConfig.SettingsSchema.SourceChoices, ApiWire.SettingsSchema.SourceChoices);
        Assert.Equal(CoreConfig.SettingsSchema.ContextChoices, ApiWire.SettingsSchema.ContextChoices);
        Assert.Equal(CoreConfig.SettingsSchema.InstallerTypeChoices, ApiWire.SettingsSchema.InstallerTypeChoices);
        Assert.Equal(CoreConfig.SettingsSchema.NotificationModeChoices, ApiWire.SettingsSchema.NotificationModeChoices);
        Assert.Equal(CoreConfig.SettingsSchema.NotifyInstallingChoices, ApiWire.SettingsSchema.NotifyInstallingChoices);
    }

    private static void AssertParity(IReadOnlyList<ApiWire.SettingDefinition> api, IReadOnlyList<CoreConfig.SettingDefinition> core)
    {
        Assert.Equal(core.Count, api.Count);
        Assert.Equal(core.Select(d => d.Name), api.Select(d => d.Name));

        foreach (var expected in core)
        {
            var actual = api.Single(d => d.Name == expected.Name);
            Assert.Equal(expected.Kind.ToString(), actual.Kind.ToString());
            Assert.Equal(expected.Category, actual.Category);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(expected.Min, actual.Min);
            Assert.Equal(expected.Max, actual.Max);
            Assert.Equal(expected.Advanced, actual.Advanced);
            Assert.Equal(expected.Choices, actual.Choices);
            Assert.Equal(expected.Default?.ToString(), actual.Default?.ToString());
        }
    }
}

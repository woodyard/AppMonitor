using System.Linq;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.ViewModels;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// How the admin console's left rail is put together. The organization pages are the console; the per-machine
/// group is deprecated, appears only under <c>--local</c>, and appears <em>after</em> them.
/// </summary>
public class AdminNavigationRailTests
{
    private static NavigationItemViewModel Page(string title, bool organization, NavigationScope scope = NavigationScope.None) =>
        new(title, "", new object(), scope, organization);

    private static NavigationItemViewModel[] Organization() =>
    [
        Page(Strings.NavConnect, organization: true),
        Page(Strings.NavDevices, organization: true),
        Page(Strings.NavEnrollment, organization: true),
    ];

    private static NavigationItemViewModel[] Local() =>
    [
        Page(Strings.NavOverview, organization: false),
        Page(Strings.NavSettings, organization: false, NavigationScope.LocalEditor),
    ];

    [Fact]
    public void WithoutLocal_TheRailIsTheOrganizationPagesAlone()
    {
        var rail = NavigationRail.Compose(Organization(), null);

        Assert.Equal([Strings.NavConnect, Strings.NavDevices, Strings.NavEnrollment], rail.Select(i => i.Title));
        Assert.All(rail, item => Assert.True(item.IsOrganization));
    }

    [Fact]
    public void WithoutLocal_ThereIsNoGroupHeaderAtAll()
    {
        var rail = NavigationRail.Compose(Organization(), null);

        Assert.DoesNotContain(rail, i => i.IsHeader);
    }

    [Fact]
    public void AnEmptyLocalGroup_CountsAsNoLocalGroup()
    {
        var rail = NavigationRail.Compose(Organization(), []);

        Assert.DoesNotContain(rail, i => i.IsHeader);
        Assert.Equal(3, rail.Count);
    }

    [Fact]
    public void TheConsoleStartsOnTheFirstOrganizationPage_InBothModes()
    {
        Assert.Equal(Strings.NavConnect, NavigationRail.Compose(Organization(), null).First(i => !i.IsHeader).Title);
        Assert.Equal(Strings.NavConnect, NavigationRail.Compose(Organization(), Local()).First(i => !i.IsHeader).Title);
    }

    [Fact]
    public void WithLocal_TheOrganizationGroupComesFirst_AndBothGroupsGetAHeader()
    {
        var rail = NavigationRail.Compose(Organization(), Local());

        Assert.Equal(
        [
            Strings.NavGroupOrganization,
            Strings.NavConnect,
            Strings.NavDevices,
            Strings.NavEnrollment,
            Strings.NavGroupLocalDeprecated,
            Strings.NavOverview,
            Strings.NavSettings,
        ], rail.Select(i => i.Title));
    }

    [Fact]
    public void WithLocal_TheLocalHeaderSaysItIsDeprecated_AndWhereSettingsLiveInstead()
    {
        var rail = NavigationRail.Compose(Organization(), Local());
        var header = rail.Last(i => i.IsHeader);

        Assert.Equal(Strings.NavGroupLocalDeprecated, header.Title);
        Assert.Contains("deprecated", header.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(header.HasTip);
        Assert.Equal(Strings.NavGroupLocalTip, header.Tip);
    }

    [Fact]
    public void TheOrganizationHeader_CarriesNoTip()
    {
        var rail = NavigationRail.Compose(Organization(), Local());
        var header = rail.First(i => i.IsHeader);

        Assert.Equal(Strings.NavGroupOrganization, header.Title);
        Assert.False(header.HasTip);
    }

    [Fact]
    public void HeadersAreNeverSelectable_AndNeverCarryARealPage()
    {
        var rail = NavigationRail.Compose(Organization(), Local());

        foreach (var header in rail.Where(i => i.IsHeader))
        {
            Assert.False(header.SharesEditor);
            Assert.False(header.SharesOrganizationEditor);
            Assert.Equal(string.Empty, header.Glyph);
        }
    }

    [Fact]
    public void ComposeDoesNotMutateTheGroupsItWasGiven()
    {
        var organization = Organization();
        var local = Local();

        var rail = NavigationRail.Compose(organization, local);

        Assert.Equal(3, organization.Length);
        Assert.Equal(2, local.Length);
        Assert.Equal(7, rail.Count);
    }
}

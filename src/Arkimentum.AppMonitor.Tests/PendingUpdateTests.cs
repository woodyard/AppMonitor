using Arkimentum.AppMonitor.Models;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class PendingUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static PendingUpdate Update(Action<PendingUpdate>? configure = null)
    {
        var u = new PendingUpdate
        {
            AppId = "7zip",
            DisplayName = "7-Zip",
            InstalledVersion = "26.02.00.0",
            AvailableVersion = "26.03",
            Source = UpdateSource.Winget,
            Context = InstallContext.System,
            FirstDetectedUtc = Now.AddHours(-1),
            LastSeenUtc = Now,
        };
        configure?.Invoke(u);
        return u;
    }

    // ---------------------------------------------------------------- CanDefer

    [Fact]
    public void CanDefer_WhenAvailableAndUnbounded() =>
        Assert.True(Update(u => u.MaxDeferrals = 0).CanDefer(Now));

    [Fact]
    public void CanDefer_WhileUnderTheDeferralLimit() =>
        Assert.True(Update(u => { u.MaxDeferrals = 3; u.DeferralCount = 2; }).CanDefer(Now));

    [Fact]
    public void CannotDefer_AtTheDeferralLimit() =>
        Assert.False(Update(u => { u.MaxDeferrals = 3; u.DeferralCount = 3; }).CanDefer(Now));

    [Fact]
    public void CannotDefer_AboveTheDeferralLimit() =>
        Assert.False(Update(u => { u.MaxDeferrals = 3; u.DeferralCount = 4; }).CanDefer(Now));

    [Theory]
    [InlineData(UpdateState.Installing)]
    [InlineData(UpdateState.Installed)]
    [InlineData(UpdateState.Scheduled)]
    public void CannotDefer_OnceTheInstallIsUnderway(UpdateState state) =>
        Assert.False(Update(u => u.State = state).CanDefer(Now));

    [Theory]
    [InlineData(UpdateState.Available)]
    [InlineData(UpdateState.Deferred)]
    [InlineData(UpdateState.WaitingForClose)]
    [InlineData(UpdateState.Failed)]
    public void CanDefer_InTheseStates(UpdateState state) =>
        Assert.True(Update(u => u.State = state).CanDefer(Now));

    [Fact]
    public void CannotDefer_PastAMandatoryDeadline() =>
        Assert.False(Update(u => { u.Mandatory = true; u.DeadlineUtc = Now.AddMinutes(-1); }).CanDefer(Now));

    [Fact]
    public void CanDefer_BeforeAMandatoryDeadline() =>
        Assert.True(Update(u => { u.Mandatory = true; u.DeadlineUtc = Now.AddHours(4); }).CanDefer(Now));

    [Fact]
    public void CanDefer_PastADeadlineThatIsNotMandatory() =>
        Assert.True(Update(u => { u.Mandatory = false; u.DeadlineUtc = Now.AddMinutes(-1); }).CanDefer(Now));

    // ---------------------------------------------------------------- IsPastDeadline

    [Fact]
    public void IsPastDeadline_RequiresMandatory() =>
        Assert.False(Update(u => { u.Mandatory = false; u.DeadlineUtc = Now.AddDays(-1); }).IsPastDeadline(Now));

    [Fact]
    public void IsPastDeadline_RequiresADeadline() =>
        Assert.False(Update(u => { u.Mandatory = true; u.DeadlineUtc = null; }).IsPastDeadline(Now));

    [Fact]
    public void IsPastDeadline_TrueExactlyAtTheDeadline() =>
        Assert.True(Update(u => { u.Mandatory = true; u.DeadlineUtc = Now; }).IsPastDeadline(Now));

    [Fact]
    public void IsPastDeadline_FalseBeforeTheDeadline() =>
        Assert.False(Update(u => { u.Mandatory = true; u.DeadlineUtc = Now.AddSeconds(1); }).IsPastDeadline(Now));

    // ---------------------------------------------------------------- misc

    [Fact]
    public void IsDeferred_OnlyUntilTheDeferralExpires()
    {
        var u = Update(x => x.DeferredUntilUtc = Now.AddMinutes(60));
        Assert.True(u.IsDeferred(Now));
        Assert.False(u.IsDeferred(Now.AddMinutes(60)));
        Assert.False(Update().IsDeferred(Now));
    }

    [Fact]
    public void Key_IncludesContextAndSidForUserUpdates()
    {
        Assert.Equal("7zip|System|-", Update().Key);
        Assert.Equal("7zip|User|S-1-5-21-1-2-3-1001",
            Update(u => { u.Context = InstallContext.User; u.UserSid = "S-1-5-21-1-2-3-1001"; }).Key);
        Assert.Equal("7zip|User|-", Update(u => u.Context = InstallContext.User).Key);
        // The SID is irrelevant for machine-wide updates.
        Assert.Equal("7zip|System|-", Update(u => u.UserSid = "S-1-5-21-1-2-3-1001").Key);
    }

    [Fact]
    public void IsActive_UntilInstalled()
    {
        Assert.True(Update().IsActive);
        Assert.True(Update(u => u.State = UpdateState.Failed).IsActive);
        Assert.False(Update(u => u.State = UpdateState.Installed).IsActive);
    }

    [Fact]
    public void Clone_CopiesTheListsRatherThanSharingThem()
    {
        var original = Update(u =>
        {
            u.ProcessNames = ["7zFM", "7zG"];
            u.BlockingProcesses = ["7zFM"];
            u.DeferralOptionsMinutes = [60, 240];
        });

        var clone = original.Clone();
        clone.ProcessNames.Add("7z");
        clone.BlockingProcesses.Clear();
        clone.DeferralOptionsMinutes.Add(1440);

        Assert.Equal(2, original.ProcessNames.Count);
        Assert.Single(original.BlockingProcesses);
        Assert.Equal(2, original.DeferralOptionsMinutes.Count);
        Assert.Equal("7zip", clone.AppId);
    }
}

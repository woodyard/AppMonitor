using Arkimentum.AppMonitor.Api.Services;
using Xunit;
using CoreVersioning = Arkimentum.AppMonitor.Versioning;

namespace Arkimentum.AppMonitor.Api.Tests;

public sealed class VersionComparerParityTests
{
    public static TheoryData<string?, string?> Pairs => new()
    {
        { "1.2.3", "1.2.3" },
        { "1.2", "1.2.0" },
        { "1.2.3", "1.2.4" },
        { "1.10.0", "1.9.0" },
        { "v8.7.1", "8.7.1" },
        { "1.2.3", "1.2.3-beta" },
        { "2024.1", "2023.12" },
        { "153.0.8010.37", "153.0.8010.5" },
        { "26.02.00.0", "26.2.0" },
        { "1.0", "unknown" },
        { null, "1.0" },
        { "", "" },
        { "20260915120000", "20260915110000" },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Compare_MatchesCore(string? a, string? b)
    {
        Assert.Equal(Math.Sign(CoreVersioning.VersionComparer.Compare(a, b)), Math.Sign(VersionComparer.Compare(a, b)));
        Assert.Equal(Math.Sign(CoreVersioning.VersionComparer.Compare(b, a)), Math.Sign(VersionComparer.Compare(b, a)));
        Assert.Equal(CoreVersioning.VersionComparer.IsNewer(a, b), VersionComparer.IsNewer(a, b));
        Assert.Equal(CoreVersioning.VersionComparer.IsUnknown(a), VersionComparer.IsUnknown(a));
    }

    [Fact]
    public void NewestFirstOrdering()
    {
        string[] versions = ["1.9.0", "1.10.0", "1.2.3-beta", "1.2.3", "2.0"];
        Assert.Equal(
            ["2.0", "1.10.0", "1.9.0", "1.2.3", "1.2.3-beta"],
            versions.OrderByDescending(v => v, VersionComparer.Instance).ToArray());
    }
}

public sealed class ConfigVersioningTests
{
    [Fact]
    public void NextIncrementsTheSequenceAndHashesTheDocument()
    {
        var first = ConfigVersioning.Next(null, "{\"a\":1}");
        Assert.Equal("1", first.Split('-')[0]);

        var second = ConfigVersioning.Next(first, "{\"a\":2}");
        Assert.Equal("2", second.Split('-')[0]);
        Assert.NotEqual(first.Split('-')[1], second.Split('-')[1]);

        // The same document always produces the same tail.
        Assert.Equal(first.Split('-')[1], ConfigVersioning.Next("0-00000000", "{\"a\":1}").Split('-')[1]);
    }

    [Theory]
    [InlineData(null, 0L)]
    [InlineData("", 0L)]
    [InlineData("0-00000000", 0L)]
    [InlineData("42-deadbeef", 42L)]
    [InlineData("not-a-version", 0L)]
    public void ParseSequence(string? version, long expected) =>
        Assert.Equal(expected, ConfigVersioning.ParseSequence(version));

    [Theory]
    [InlineData("5-abcd", "5-abcd")]
    [InlineData("\"5-abcd\"", "5-abcd")]
    [InlineData("W/\"5-abcd\"", "5-abcd")]
    [InlineData("  \"5-abcd\"  ", "5-abcd")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Unquote(string? etag, string? expected) =>
        Assert.Equal(expected, ConfigVersioning.Unquote(etag));
}

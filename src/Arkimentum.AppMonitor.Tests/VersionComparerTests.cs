using Arkimentum.AppMonitor.Versioning;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class VersionComparerTests
{
    [Theory]
    // numeric ordering, not lexicographic
    [InlineData("26.03", "26.02.00.0", true)]
    [InlineData("2.90.0", "2.89.1", true)]
    [InlineData("153.0.8010.37", "152.0.7977.84", true)]
    [InlineData("1.104.0", "1.103.2", true)]
    [InlineData("1.10", "1.9", true)]
    [InlineData("1.9", "1.10", false)]
    [InlineData("8.9.8", "8.9.10", false)]
    // equal versions are not "newer"
    [InlineData("26.03", "26.03", false)]
    [InlineData("1.2.3", "1.2.3", false)]
    // missing trailing segments count as zero
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.0", "1.2", false)]
    [InlineData("1.2.1", "1.2", true)]
    // "v" prefix is ignored
    [InlineData("v8.7.1", "8.7.0", true)]
    [InlineData("8.7.1", "v8.7.1", false)]
    [InlineData("V7.6.6", "v7.6.5", true)]
    // prerelease ranks below the release
    [InlineData("1.2.3", "1.2.3-beta", true)]
    [InlineData("1.2.3-beta", "1.2.3", false)]
    [InlineData("1.2.3-rc2", "1.2.3-rc1", true)]
    // unknown installed version => anything is newer
    [InlineData("26.03", "Unknown", true)]
    [InlineData("26.03", "", true)]
    [InlineData("26.03", null, true)]
    [InlineData("26.03", "< 1.0", true)]
    // empty available version is never newer
    [InlineData("", "26.02", false)]
    [InlineData(null, "26.02", false)]
    public void IsNewer_Works(string? available, string? installed, bool expected) =>
        Assert.Equal(expected, VersionComparer.IsNewer(available, installed));

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.4", "1.2.3", 1)]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("26.02.00.0", "26.02", 0)]
    [InlineData("2024.1", "2023.12", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    public void Compare_ReturnsSign(string a, string b, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(VersionComparer.Compare(a, b)));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Unknown", true)]
    [InlineData("unknown", true)]
    [InlineData("UNKNOWN", true)]
    [InlineData("< 1.0", true)]
    [InlineData("> 2.0", true)]
    [InlineData("26.02.00.0", false)]
    public void IsUnknown_Works(string? version, bool expected) =>
        Assert.Equal(expected, VersionComparer.IsUnknown(version));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("  1.2.3  ", "1.2.3")]
    [InlineData("version", "version")]   // "v" only stripped when followed by a digit
    [InlineData(null, "")]
    public void Normalize_StripsVPrefixAndWhitespace(string? input, string expected) =>
        Assert.Equal(expected, VersionComparer.Normalize(input));

    [Fact]
    public void Instance_SortsAscending()
    {
        var list = new List<string?> { "1.10.0", "1.2.0", "1.9.3", null, "2.0" };
        list.Sort(VersionComparer.Instance);
        Assert.Equal([null, "1.2.0", "1.9.3", "1.10.0", "2.0"], list);
    }
}

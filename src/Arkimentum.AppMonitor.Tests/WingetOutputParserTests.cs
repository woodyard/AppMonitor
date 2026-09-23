using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class WingetOutputParserTests
{
    /// <summary>Real output of <c>winget upgrade --accept-source-agreements --disable-interactivity</c> (winget 1.30.140-preview).</summary>
    private const string UpgradeSample =
        "Name                         Id                   Version       Available     Source\r\n" +
        "------------------------------------------------------------------------------------\r\n" +
        "7-Zip 26.02 (x64 edition)    7zip.7zip            26.02.00.0    26.03         winget\r\n" +
        "Google Chrome                Google.Chrome        152.0.7977.84 153.0.8010.37 winget\r\n" +
        "Microsoft Azure CLI (64-bit) Microsoft.AzureCLI   2.89.1        2.90.0        winget\r\n" +
        "Mozilla Firefox              Mozilla.Firefox.MSIX 154.0.1.0     155.0.1       winget\r\n" +
        "4 upgrades available.\r\n";

    /// <summary>Real output of <c>winget list --id Microsoft.VisualStudioCode --exact ...</c>: no Available column at all.</summary>
    private const string NoAvailableColumnSample =
        "Name                                Id                         Version Source\r\n" +
        "------------------------------------------------------------------------------\r\n" +
        "Microsoft Visual Studio Code (User) Microsoft.VisualStudioCode 1.137.0 winget\r\n";

    /// <summary>Real output of <c>winget list --id 7zip.7zip --exact ...</c> when an upgrade exists.</summary>
    private const string SinglePackageWithAvailableSample =
        "Name                      Id        Version    Available Source\r\n" +
        "---------------------------------------------------------------\r\n" +
        "7-Zip 26.02 (x64 edition) 7zip.7zip 26.02.00.0 26.03     winget\r\n";

    /// <summary>
    /// Real output of <c>winget list --accept-source-agreements --disable-interactivity</c> (first rows only).
    /// The Available cell is blank for up-to-date packages and the Source cell is blank for sideloaded MSIX packages;
    /// note that winget also trims the trailing padding, so the last row is shorter than the header.
    /// </summary>
    private const string BlankCellsSample =
        "Name                                                         Id                                                                                    Version              Available     Source\r\n" +
        "--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------\r\n" +
        "1Password                                                    AgileBits.1Password                                                                   8.12.36.40                         winget\r\n" +
        "7-Zip 26.02 (x64 edition)                                    7zip.7zip                                                                             26.02.00.0           26.03         winget\r\n" +
        "App Installer                                                Microsoft.AppInstaller                                                                1.30.139.0                         winget\r\n" +
        "AV1 Video Extension                                          MSIX\\Microsoft.AV1VideoExtension_2.0.30.0_x64__8wekyb3d8bbwe                          2.0.30.0\r\n";

    private const string NotInstalledSample = "No installed package found matching input criteria.\r\n";

    [Fact]
    public void ParsesUpgradeTableWithAllFiveColumns()
    {
        var rows = WingetOutputParser.ParseListOutput(UpgradeSample);

        Assert.Equal(4, rows.Count);

        Assert.Equal("7-Zip 26.02 (x64 edition)", rows[0].Name);
        Assert.Equal("7zip.7zip", rows[0].Id);
        Assert.Equal("26.02.00.0", rows[0].Version);
        Assert.Equal("26.03", rows[0].Available);
        Assert.Equal("winget", rows[0].Source);

        Assert.Equal("Google Chrome", rows[1].Name);
        Assert.Equal("Google.Chrome", rows[1].Id);
        Assert.Equal("152.0.7977.84", rows[1].Version);
        Assert.Equal("153.0.8010.37", rows[1].Available);

        Assert.Equal("Microsoft Azure CLI (64-bit)", rows[2].Name);
        Assert.Equal("Microsoft.AzureCLI", rows[2].Id);
        Assert.Equal("2.89.1", rows[2].Version);
        Assert.Equal("2.90.0", rows[2].Available);

        Assert.Equal("Mozilla Firefox", rows[3].Name);
        Assert.Equal("Mozilla.Firefox.MSIX", rows[3].Id);
        Assert.Equal("154.0.1.0", rows[3].Version);
        Assert.Equal("155.0.1", rows[3].Available);
        Assert.Equal("winget", rows[3].Source);
    }

    /// <summary>
    /// Real output of <c>winget upgrade --scope user --accept-source-agreements --disable-interactivity</c> (winget 1.30,
    /// 2026-09-23, a device with the Firefox MSIX and classic builds), followed by the trailing notes and the second
    /// "explicit targeting" table winget prints for pinned packages (those lines are synthetic, in winget's format).
    /// </summary>
    private const string UserScopeUpgradeSample =
        "Name            Id                   Version   Available Source\r\n" +
        "---------------------------------------------------------------\r\n" +
        "Mozilla Firefox Mozilla.Firefox.MSIX 156.0.0.0 156.0.1   winget\r\n" +
        "1 upgrades available.\r\n" +
        "\r\n" +
        "1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.\r\n" +
        "The following packages have an upgrade available, but require explicit targeting for upgrade:\r\n" +
        "Name          Id            Version Available Source\r\n" +
        "----------------------------------------------------\r\n" +
        "Pinned Thing  Contoso.Pinned 1.0    2.0       winget\r\n";

    [Fact]
    public void UpgradeListingStopsAtTheFooterAndIgnoresTheExplicitTargetingTable()
    {
        var rows = WingetOutputParser.ParseListOutput(UserScopeUpgradeSample);

        var row = Assert.Single(rows);
        Assert.Equal("Mozilla Firefox", row.Name);
        Assert.Equal("Mozilla.Firefox.MSIX", row.Id);
        Assert.Equal("156.0.0.0", row.Version);
        Assert.Equal("156.0.1", row.Available);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void FooterCountLineIsNotParsedAsARow()
    {
        var rows = WingetOutputParser.ParseListOutput(UpgradeSample);
        Assert.DoesNotContain(rows, r => r.Name.Contains("upgrades available", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParsesTableWithoutAvailableColumn()
    {
        var rows = WingetOutputParser.ParseListOutput(NoAvailableColumnSample);

        var row = Assert.Single(rows);
        Assert.Equal("Microsoft Visual Studio Code (User)", row.Name);
        Assert.Equal("Microsoft.VisualStudioCode", row.Id);
        Assert.Equal("1.137.0", row.Version);
        Assert.Equal(string.Empty, row.Available);
        Assert.False(row.HasAvailable);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void ParsesSinglePackageWithAvailable()
    {
        var row = Assert.Single(WingetOutputParser.ParseListOutput(SinglePackageWithAvailableSample));
        Assert.Equal("7zip.7zip", row.Id);
        Assert.Equal("26.02.00.0", row.Version);
        Assert.Equal("26.03", row.Available);
        Assert.True(row.HasAvailable);
    }

    [Fact]
    public void HandlesBlankAvailableAndSourceCells()
    {
        var rows = WingetOutputParser.ParseListOutput(BlankCellsSample);

        Assert.Equal(4, rows.Count);

        Assert.Equal("1Password", rows[0].Name);
        Assert.Equal("AgileBits.1Password", rows[0].Id);
        Assert.Equal("8.12.36.40", rows[0].Version);
        Assert.Equal(string.Empty, rows[0].Available);
        Assert.Equal("winget", rows[0].Source);

        Assert.Equal("26.03", rows[1].Available);

        Assert.Equal("App Installer", rows[2].Name);
        Assert.Equal("Microsoft.AppInstaller", rows[2].Id);
        Assert.Equal("1.30.139.0", rows[2].Version);
        Assert.Equal(string.Empty, rows[2].Available);
        Assert.Equal("winget", rows[2].Source);

        // Row shorter than the header: Available and Source fall off the end entirely.
        Assert.Equal("AV1 Video Extension", rows[3].Name);
        Assert.Equal(@"MSIX\Microsoft.AV1VideoExtension_2.0.30.0_x64__8wekyb3d8bbwe", rows[3].Id);
        Assert.Equal("2.0.30.0", rows[3].Version);
        Assert.Equal(string.Empty, rows[3].Available);
        Assert.Equal(string.Empty, rows[3].Source);
    }

    [Fact]
    public void HandlesUnknownInstalledVersion()
    {
        const string sample =
            "Name           Id                                          Version Available Source\r\n" +
            "---------------------------------------------------------------------------------\r\n" +
            "Some Store App MSIX\\Some.Store.App_1.0.0.0_x64__abcdefghij Unknown\r\n";

        var row = Assert.Single(WingetOutputParser.ParseListOutput(sample));
        Assert.Equal("Unknown", row.Version);
        Assert.False(row.HasAvailable);
        Assert.True(Arkimentum.AppMonitor.Versioning.VersionComparer.IsUnknown(row.Version));
    }

    [Fact]
    public void RepairsACellThatOverflowsItsColumn()
    {
        // Defensive: if a value is wider than the header-derived column, the boundary is pushed to the next token
        // instead of chopping the value in half.
        const string sample =
            "Name      Id         Version Available Source\r\n" +
            "---------------------------------------------\r\n" +
            "Some App  Publisher.WithAVeryLongIdentifier 1.0.0 1.1.0 winget\r\n";

        var row = Assert.Single(WingetOutputParser.ParseListOutput(sample));
        Assert.Equal("Some App", row.Name);
        Assert.Equal("Publisher.WithAVeryLongIdentifier", row.Id);
        Assert.Equal("1.0.0", row.Version);
        Assert.Equal("1.1.0", row.Available);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void HandlesTruncatedNameWithEllipsis()
    {
        // winget truncates a cell that does not fit the console width with U+2026.
        const string sample =
            "Name                          Id                         Version Available Source\r\n" +
            "--------------------------------------------------------------------------------\r\n" +
            "Microsoft Visual Studio Code… Microsoft.VisualStudioCode 1.103.2 1.104.0   winget\r\n";

        var row = Assert.Single(WingetOutputParser.ParseListOutput(sample));
        Assert.Equal("Microsoft Visual Studio Code…", row.Name);
        Assert.Equal("Microsoft.VisualStudioCode", row.Id);
        Assert.Equal("1.103.2", row.Version);
        Assert.Equal("1.104.0", row.Available);
        Assert.Equal("winget", row.Source);
        Assert.True(row.IsTruncated);
    }

    [Fact]
    public void StripsSpinnerAndBannerNoise()
    {
        // What the console looks like before winget overwrites the spinner with the table.
        const string noisy =
            "\r  \r  -\r  \\\r  |\r  /\r" +
            "Windows Package Manager (Preview) v1.30.140-preview\r\n" +
            "Copyright (c) Microsoft Corporation. All rights reserved.\r\n" +
            "\r\n" +
            "Name                      Id        Version    Available Source\r\n" +
            "---------------------------------------------------------------\r\n" +
            "7-Zip 26.02 (x64 edition) 7zip.7zip 26.02.00.0 26.03     winget\r\n";

        var row = Assert.Single(WingetOutputParser.ParseListOutput(noisy));
        Assert.Equal("7zip.7zip", row.Id);
        Assert.Equal("26.03", row.Available);
    }

    [Fact]
    public void StripsDownloadProgressNoise()
    {
        const string noisy =
            "  █████▒▒▒▒▒  50%\r\n" +
            "  12.4 MB / 24.9 MB\r\n" +
            "Name                      Id        Version    Available Source\r\n" +
            "---------------------------------------------------------------\r\n" +
            "7-Zip 26.02 (x64 edition) 7zip.7zip 26.02.00.0 26.03     winget\r\n";

        Assert.Single(WingetOutputParser.ParseListOutput(noisy));
    }

    [Theory]
    [InlineData("No installed package found matching input criteria.", true)]
    [InlineData("  No installed package found matching input criteria.\r\n", true)]
    [InlineData("No package found matching input criteria.", true)]
    [InlineData("No packages found matching input criteria.", true)]
    [InlineData("Name  Id\r\n----\r\nA  B", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNotInstalledOutput_Works(string? output, bool expected) =>
        Assert.Equal(expected, WingetOutputParser.IsNotInstalledOutput(output));

    [Fact]
    public void NotInstalledOutputHasNoRows() =>
        Assert.Empty(WingetOutputParser.ParseListOutput(NotInstalledSample));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("garbage without a table")]
    [InlineData("Name  Id  Version")]   // header but no separator
    public void ReturnsEmptyForNonTableOutput(string? output) =>
        Assert.Empty(WingetOutputParser.ParseListOutput(output));

    [Fact]
    public void FindById_PrefersExactMatch()
    {
        var rows = WingetOutputParser.ParseListOutput(UpgradeSample);
        var row = WingetOutputParser.FindById(rows, "google.chrome");
        Assert.NotNull(row);
        Assert.Equal("Google.Chrome", row!.Id);
    }

    [Fact]
    public void FindById_FallsBackToTheSingleRow()
    {
        var rows = WingetOutputParser.ParseListOutput(NoAvailableColumnSample);
        Assert.NotNull(WingetOutputParser.FindById(rows, "Some.Other.Id"));
    }

    [Fact]
    public void FindById_MatchesTruncatedId()
    {
        const string sample =
            "Name      Id                     Version Available Source\r\n" +
            "---------------------------------------------------------\r\n" +
            "Some App  Publisher.VeryLongPro… 1.0.0   1.1.0     winget\r\n" +
            "Other App Other.Thing            2.0     2.1       winget\r\n";

        var rows = WingetOutputParser.ParseListOutput(sample);
        var row = WingetOutputParser.FindById(rows, "Publisher.VeryLongProductName");
        Assert.NotNull(row);
        Assert.Equal("1.0.0", row!.Version);
    }

    [Fact]
    public void FindById_ReturnsNullWhenAmbiguous()
    {
        var rows = WingetOutputParser.ParseListOutput(UpgradeSample);
        Assert.Null(WingetOutputParser.FindById(rows, "Nothing.Matches"));
    }

    [Theory]
    [InlineData("-", true)]
    [InlineData("\\", true)]
    [InlineData("|", true)]
    [InlineData("/", true)]
    [InlineData("  -  ", true)]
    [InlineData("", true)]
    [InlineData("Windows Package Manager (Preview) v1.30.140-preview", true)]
    [InlineData("Copyright (c) Microsoft Corporation. All rights reserved.", true)]
    [InlineData("  45%", true)]
    [InlineData("7-Zip 26.02 (x64 edition) 7zip.7zip 26.02.00.0 26.03 winget", false)]
    [InlineData("---------------------------------", false)]
    public void IsNoise_Works(string line, bool expected) =>
        Assert.Equal(expected, WingetOutputParser.IsNoise(line));

    [Fact]
    public void ExitCodeConstantsMatchWingetsValues()
    {
        Assert.Equal(-1978335212, WingetOutputParser.ExitNoInstalledPackageFound);   // 0x8A150014
        Assert.Equal(-1978335189, WingetOutputParser.ExitNoApplicableUpgrade);       // 0x8A15002B
        Assert.Equal(unchecked((int)0x8A150109), WingetOutputParser.ExitRebootRequiredToFinish);
        Assert.Equal(unchecked((int)0x8A15010B), WingetOutputParser.ExitRebootInitiated);
    }
}

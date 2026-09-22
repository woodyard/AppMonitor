using Arkimentum.AppMonitor.Web.Catalog;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// The console reads the same <c>catalog/catalog.json</c> the agent ships with, through a deliberately minimal DTO.
/// These tests pin the parsing rules that matter: unknown members are ignored (the agent's catalog may grow),
/// entries without an appId are skipped and a duplicate appId keeps the first.
/// </summary>
public sealed class CatalogEntryTests
{
    [Fact]
    public void Unknown_members_are_ignored()
    {
        // "notes", "deferralOptionsMinutes" and "mandatory" exist in the agent's catalog and are none of the
        // console's business - they must not make the entry unreadable.
        const string json = """
        [
          {
            "appId": "7zip",
            "displayName": "7-Zip",
            "source": "winget",
            "context": "auto",
            "wingetId": "7zip.7zip",
            "processNames": [ "7zFM", "7zG" ],
            "mandatory": true,
            "deferralOptionsMinutes": [ 60, 240 ],
            "notes": "Verified 2026-09-14."
          }
        ]
        """;

        var entries = CatalogEntry.Parse(json);

        var entry = Assert.Single(entries);
        Assert.Equal("7zip", entry.AppId);
        Assert.Equal("7-Zip", entry.DisplayName);
        Assert.Equal("7zip.7zip", entry.WingetId);
        Assert.Equal(["7zFM", "7zG"], entry.ProcessNames);
    }

    [Fact]
    public void Entries_without_an_appId_are_skipped_and_duplicates_keep_the_first()
    {
        const string json = """
        [
          { "displayName": "Nameless" },
          { "appId": "vlc", "displayName": "VLC media player" },
          { "appId": "VLC", "displayName": "Another VLC" }
        ]
        """;

        var entries = CatalogEntry.Parse(json);

        var entry = Assert.Single(entries);
        Assert.Equal("vlc", entry.AppId);
        Assert.Equal("VLC media player", entry.DisplayName);
    }

    [Fact]
    public void A_broken_catalog_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(CatalogEntry.Parse("not json at all"));
        Assert.Empty(CatalogEntry.Parse(""));
    }

    [Fact]
    public void Matching_looks_at_the_id_the_name_and_the_winget_id()
    {
        var entry = new CatalogEntry { AppId = "vscode", DisplayName = "Visual Studio Code", WingetId = "Microsoft.VisualStudioCode" };

        Assert.True(entry.Matches(null));
        Assert.True(entry.Matches("code"));
        Assert.True(entry.Matches("microsoft."));
        Assert.False(entry.Matches("firefox"));
    }

    [Fact]
    public void The_title_falls_back_to_the_appId()
    {
        Assert.Equal("contoso", new CatalogEntry { AppId = "contoso" }.Title);
        Assert.Equal("Contoso Tool", new CatalogEntry { AppId = "contoso", DisplayName = "Contoso Tool" }.Title);
    }
}

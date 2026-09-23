using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class JsonCatalogProviderTests
{
    /// <summary>The shipped catalog at &lt;repo&gt;/catalog/catalog.json, found by walking up from the test binary.</summary>
    private static string RepositoryCatalogPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "catalog", "catalog.json");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate catalog/catalog.json above " + AppContext.BaseDirectory);
        }
    }

    private static IReadOnlyList<AppPolicy> LoadRepositoryCatalog() =>
        new JsonCatalogProvider(NullLogger<JsonCatalogProvider>.Instance).GetCatalog(RepositoryCatalogPath);

    [Fact]
    public void LoadsTheShippedCatalog()
    {
        var catalog = LoadRepositoryCatalog();
        Assert.NotEmpty(catalog);
        Assert.True(catalog.Count >= 12, $"Expected at least 12 catalog entries, found {catalog.Count}.");
    }

    [Fact]
    public void EveryEntryHasAnAppIdAndIsUnique()
    {
        var catalog = LoadRepositoryCatalog();
        foreach (var app in catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.AppId));
            Assert.DoesNotContain(' ', app.AppId);
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName), $"'{app.AppId}' has no displayName.");
        }
        Assert.Equal(catalog.Count, catalog.Select(a => a.AppId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryEntryHasEitherAWingetIdOrAVersionAndDownloadUrl()
    {
        foreach (var app in LoadRepositoryCatalog())
        {
            var hasWinget = !string.IsNullOrWhiteSpace(app.WingetId);
            var hasWeb = !string.IsNullOrWhiteSpace(app.VersionUrl) && !string.IsNullOrWhiteSpace(app.DownloadUrl);
            Assert.True(hasWinget || hasWeb,
                $"'{app.AppId}' has neither a wingetId nor a versionUrl + downloadUrl pair.");
        }
    }

    [Fact]
    public void WebSourcedEntriesAreFullyConfigured()
    {
        foreach (var app in LoadRepositoryCatalog().Where(a => a.Source == UpdateSource.Web))
        {
            Assert.False(string.IsNullOrWhiteSpace(app.VersionUrl), $"'{app.AppId}' uses the web source but has no versionUrl.");
            Assert.False(string.IsNullOrWhiteSpace(app.VersionRegex), $"'{app.AppId}' uses the web source but has no versionRegex.");
            Assert.False(string.IsNullOrWhiteSpace(app.DownloadUrl), $"'{app.AppId}' uses the web source but has no downloadUrl.");
        }
    }

    [Fact]
    public void AllRegularExpressionsCompile()
    {
        foreach (var app in LoadRepositoryCatalog())
        {
            AssertCompiles(app.VersionRegex, app.AppId, nameof(app.VersionRegex));
            AssertCompiles(app.DetectDisplayNameRegex, app.AppId, nameof(app.DetectDisplayNameRegex));
            AssertCompiles(app.DetectPublisherRegex, app.AppId, nameof(app.DetectPublisherRegex));
        }

        static void AssertCompiles(string? pattern, string appId, string field)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            var ex = Record.Exception(() => _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            Assert.True(ex is null, $"'{appId}'.{field} is not a valid regex: {ex?.Message}");
        }
    }

    [Fact]
    public void AllUrlsAreAbsoluteHttps()
    {
        foreach (var app in LoadRepositoryCatalog())
        {
            foreach (var (name, url) in new[]
                     {
                         (nameof(app.VersionUrl), app.VersionUrl),
                         (nameof(app.Sha256Url), app.Sha256Url),
                     })
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                    $"'{app.AppId}'.{name} is not an absolute https URL: {url}");
            }

            // Download URLs still contain {placeholders}; check the scheme only.
            foreach (var (name, url) in new[] { (nameof(app.DownloadUrl), app.DownloadUrl), (nameof(app.UserDownloadUrl), app.UserDownloadUrl) })
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                Assert.StartsWith("https://", url, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void EveryEntryDeclaresAtLeastOneProcessName()
    {
        foreach (var app in LoadRepositoryCatalog())
            Assert.True(app.ProcessNames.Count > 0, $"'{app.AppId}' declares no processNames; the agent cannot detect blocking processes.");
    }

    [Fact]
    public void KnownEntriesAreMappedCorrectly()
    {
        var catalog = LoadRepositoryCatalog();

        var sevenZip = Assert.Single(catalog, a => a.AppId == "7zip");
        Assert.Equal("7-Zip", sevenZip.DisplayName);
        Assert.Equal(UpdateSource.Winget, sevenZip.Source);
        Assert.Equal(InstallContext.Auto, sevenZip.Context);
        Assert.Equal("7zip.7zip", sevenZip.WingetId);
        Assert.Equal("winget", sevenZip.WingetSourceName);
        Assert.Contains("7zFM", sevenZip.ProcessNames);

        var vscode = Assert.Single(catalog, a => a.AppId == "vscode");
        Assert.Equal("Microsoft.VisualStudioCode", vscode.WingetId);
        Assert.Equal(@"^Microsoft Visual Studio Code( \(User\))?$", vscode.DetectDisplayNameRegex);
        Assert.Equal("https://update.code.visualstudio.com/latest/win32-x64-user/stable", vscode.UserDownloadUrl);
        Assert.Contains("/VERYSILENT", vscode.InstallerArgs);

        var chrome = Assert.Single(catalog, a => a.AppId == "chrome");
        Assert.Equal(InstallerType.Msi, chrome.InstallerType);
        Assert.Equal(InstallContext.Auto, chrome.Context); // Chrome is often installed per user (Google.Chrome.EXE)

        var slack = Assert.Single(catalog, a => a.AppId == "slack");
        Assert.Equal(InstallContext.User, slack.Context);
    }

    [Fact]
    public void VersionRegexesMatchTheirRealWorldResponseShape()
    {
        var catalog = LoadRepositoryCatalog();

        // Representative bodies captured from the live endpoints on 2026-09-14.
        var samples = new Dictionary<string, (string Body, string Expected)>(StringComparer.OrdinalIgnoreCase)
        {
            ["7zip"] = ("<P><B>Download 7-Zip 26.03 (2026-08-30) for Windows:</B></P>", "26.03"),
            ["notepadplusplus"] = ("""{"url":"https://api.github.com/x","tag_name":"v8.9.8","name":"v8.9.8"}""", "8.9.8"),
            ["vlc"] = ("""<a href="vlc-3.0.23-win64.exe">vlc-3.0.23-win64.exe</a>  45948080""", "3.0.23"),
            ["firefox"] = ("""{"FIREFOX_ESR":"140.3.0esr","LATEST_FIREFOX_VERSION":"155.0.1"}""", "155.0.1"),
            ["chrome"] = ("""{"releases":[{"name":"x","version":"153.0.8010.37","fraction":1}]}""", "153.0.8010.37"),
            ["vscode"] = ("""{"url":"https://x","productVersion":"1.137.0","version":"645f29cc"}""", "1.137.0"),
            ["powershell"] = ("""{"html_url":"https://x","tag_name":"v7.6.6"}""", "7.6.6"),
        };

        foreach (var (appId, (body, expected)) in samples)
        {
            var app = Assert.Single(catalog, a => a.AppId == appId);
            Assert.False(string.IsNullOrWhiteSpace(app.VersionRegex), $"'{appId}' has no versionRegex.");
            var actual = Arkimentum.AppMonitor.Providers.WebProvider.ExtractVersion(body, app.VersionRegex);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void DownloadUrlPlaceholdersExpandToPlausibleUrls()
    {
        var catalog = LoadRepositoryCatalog();

        Assert.Equal("https://www.7-zip.org/a/7z2603-x64.exe",
            Arkimentum.AppMonitor.Providers.WebProvider.ExpandPlaceholders(Assert.Single(catalog, a => a.AppId == "7zip").DownloadUrl, "26.03"));

        Assert.Equal("https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.9.8/npp.8.9.8.Installer.x64.exe",
            Arkimentum.AppMonitor.Providers.WebProvider.ExpandPlaceholders(Assert.Single(catalog, a => a.AppId == "notepadplusplus").DownloadUrl, "8.9.8"));

        Assert.Equal("https://get.videolan.org/vlc/3.0.23/win64/vlc-3.0.23-win64.exe",
            Arkimentum.AppMonitor.Providers.WebProvider.ExpandPlaceholders(Assert.Single(catalog, a => a.AppId == "vlc").DownloadUrl, "3.0.23"));

        Assert.Equal("https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/PowerShell-7.6.6-win-x64.msi",
            Arkimentum.AppMonitor.Providers.WebProvider.ExpandPlaceholders(Assert.Single(catalog, a => a.AppId == "powershell").DownloadUrl, "7.6.6"));
    }

    // ---------------------------------------------------------------- robustness

    [Fact]
    public void MissingFileYieldsAnEmptyCatalogWithoutThrowing()
    {
        var provider = new JsonCatalogProvider(NullLogger<JsonCatalogProvider>.Instance);
        Assert.Empty(provider.GetCatalog(Path.Combine(Path.GetTempPath(), "arkimentum-no-such-catalog.json")));
    }

    [Fact]
    public void MalformedJsonYieldsAnEmptyCatalogWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arkimentum-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not valid json");
        try
        {
            var provider = new JsonCatalogProvider(NullLogger<JsonCatalogProvider>.Instance);
            Assert.Empty(provider.GetCatalog(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EntriesWithoutAnAppIdAreSkipped()
    {
        const string json = """
            [
              { "displayName": "No id here" },
              { "appId": "good", "displayName": "Good", "wingetId": "Good.App" }
            ]
            """;

        var entries = JsonCatalogProvider.Parse(json, "test", NullLogger.Instance);
        var app = Assert.Single(entries);
        Assert.Equal("good", app.AppId);
    }

    [Fact]
    public void DuplicateAppIdsKeepTheFirstEntry()
    {
        const string json = """
            [
              { "appId": "dup", "displayName": "First", "wingetId": "A" },
              { "appId": "DUP", "displayName": "Second", "wingetId": "B" }
            ]
            """;

        var app = Assert.Single(JsonCatalogProvider.Parse(json, "test", NullLogger.Instance));
        Assert.Equal("First", app.DisplayName);
    }

    [Fact]
    public void UnknownFieldsAreIgnoredAndEnumsAreReadAsStrings()
    {
        const string json = """
            [
              {
                "appId": "demo",
                "displayName": "Demo",
                "source": "web",
                "context": "user",
                "installerType": "msi",
                "versionUrl": "https://example.test/v",
                "versionRegex": "([0-9.]+)",
                "downloadUrl": "https://example.test/d-{version}.msi",
                "somethingNobodyKnows": 42,
                "notes": "free text"
              }
            ]
            """;

        var app = Assert.Single(JsonCatalogProvider.Parse(json, "test", NullLogger.Instance));
        Assert.Equal(UpdateSource.Web, app.Source);
        Assert.Equal(InstallContext.User, app.Context);
        Assert.Equal(InstallerType.Msi, app.InstallerType);
        Assert.True(app.Enabled);
    }

    [Fact]
    public void ReturnsTheCachedInstanceWhileTheFileIsUnchanged()
    {
        var provider = new JsonCatalogProvider(NullLogger<JsonCatalogProvider>.Instance);
        var first = provider.GetCatalog(RepositoryCatalogPath);
        var second = provider.GetCatalog(RepositoryCatalogPath);
        Assert.Same(first, second);

        provider.InvalidateCache();
        Assert.NotSame(first, provider.GetCatalog(RepositoryCatalogPath));
    }

    [Fact]
    public void ResolvePathReturnsNullForAMissingFile() =>
        Assert.Null(JsonCatalogProvider.ResolvePath(Path.Combine(Path.GetTempPath(), "definitely-not-here.json")));
}

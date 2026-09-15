using System.Net;
using System.Net.Http;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class WebProviderTests
{
    private static readonly ExecutionContextInfo SystemContext = ExecutionContextInfo.System;
    private static readonly ExecutionContextInfo UserContext = new() { IsSystem = false, SessionId = 2, UserSid = "S-1-5-21-1-2-3-1001" };

    private static WebProvider Create(FakeHandler handler, ProviderOptions? options = null) =>
        new(NullLogger<WebProvider>.Instance, options ?? new ProviderOptions(), handler);

    private static InstalledApp Installed(string name, string version) =>
        new() { DisplayName = name, DisplayVersion = version, Context = InstallContext.System };

    // ---------------------------------------------------------------- version regex

    [Fact]
    public async Task ExtractsVersionAndFlagsAnUpdate()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"LATEST_FIREFOX_VERSION": "155.0.1", "FIREFOX_ESR": "140.3.0esr"}"""));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "firefox",
            DisplayName = "Mozilla Firefox",
            Source = UpdateSource.Web,
            VersionUrl = "https://product-details.mozilla.org/1.0/firefox_versions.json",
            VersionRegex = "\"LATEST_FIREFOX_VERSION\":\\s*\"([^\"]+)\"",
            DownloadUrl = "https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=en-US",
            InstallerArgs = "/S",
        };

        var result = await provider.CheckAsync(app, Installed("Mozilla Firefox (x64 en-US)", "154.0.1"), SystemContext, default);

        Assert.True(result.IsInstalled);
        Assert.Equal("154.0.1", result.InstalledVersion);
        Assert.Equal("155.0.1", result.AvailableVersion);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(InstallContext.System, result.ResolvedContext);
        Assert.Equal("/S", result.InstallerArgs);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task DoesNotFlagAnUpdateWhenTheInstalledVersionIsCurrent()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"tag_name": "v8.9.8"}"""));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "notepadplusplus",
            Source = UpdateSource.Web,
            VersionUrl = "https://api.github.com/repos/notepad-plus-plus/notepad-plus-plus/releases/latest",
            VersionRegex = "\"tag_name\":\\s*\"v?([^\"]+)\"",
            DownloadUrl = "https://github.com/x/releases/download/v{version}/npp.{version}.Installer.x64.exe",
        };

        var result = await provider.CheckAsync(app, Installed("Notepad++ (64-bit x64)", "8.9.8"), SystemContext, default);

        Assert.True(result.IsInstalled);
        Assert.Equal("8.9.8", result.AvailableVersion);          // the "v" prefix is normalised away
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task UsesTheNamedVersionGroupWhenPresent()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("Download 7-Zip 26.03 (2026-08-30) for Windows"));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "7zip",
            Source = UpdateSource.Web,
            VersionUrl = "https://www.7-zip.org/download.html",
            VersionRegex = @"Download 7-Zip (?<version>\d+\.\d+)",
            DownloadUrl = "https://www.7-zip.org/a/7z{version_nodots}-x64.exe",
        };

        var result = await provider.CheckAsync(app, Installed("7-Zip 26.02 (x64)", "26.02.00.0"), SystemContext, default);

        Assert.Equal("26.03", result.AvailableVersion);
        Assert.True(result.UpdateAvailable);
    }

    [Fact]
    public async Task FailsWithAClearErrorWhenTheRegexDoesNotMatch()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("<html>the page was redesigned</html>"));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "7zip",
            Source = UpdateSource.Web,
            VersionUrl = "https://www.7-zip.org/download.html",
            VersionRegex = @"Download 7-Zip (\d+\.\d+)",
            DownloadUrl = "https://www.7-zip.org/a/7z{version_nodots}-x64.exe",
        };

        var result = await provider.CheckAsync(app, Installed("7-Zip", "26.02.00.0"), SystemContext, default);

        Assert.NotNull(result.Error);
        Assert.Contains("did not match", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task ReportsNotInstalledWhenThereIsNoInventoryMatchAndNoDetectFilePath()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"tag_name": "v9.9.9"}"""));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "notepadplusplus",
            Source = UpdateSource.Web,
            VersionUrl = "https://api.github.com/x",
            VersionRegex = "\"tag_name\":\\s*\"v?([^\"]+)\"",
        };

        var result = await provider.CheckAsync(app, installed: null, SystemContext, default);

        Assert.False(result.IsInstalled);
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Error);
        Assert.Equal(0, handler.RequestCount);   // no point fetching the version when nothing is installed
    }

    [Fact]
    public async Task FailsWhenTheVersionUrlReturnsAnError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "vlc",
            Source = UpdateSource.Web,
            VersionUrl = "https://get.videolan.org/vlc/last/win64/",
            VersionRegex = @"vlc-([\d.]+)-win64\.exe",
        };

        var result = await provider.CheckAsync(app, Installed("VLC media player", "3.0.21"), SystemContext, default);

        Assert.NotNull(result.Error);
        Assert.Contains("503", result.Error!);
    }

    [Fact]
    public async Task ReturnsAFailureWhenWebSourcesAreDisabled()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("x"));
        using var provider = Create(handler, new ProviderOptions { WebSourcesEnabled = false });

        var app = new AppPolicy { AppId = "vlc", Source = UpdateSource.Web, VersionUrl = "https://example.invalid/" };
        var result = await provider.CheckAsync(app, Installed("VLC", "3.0.21"), SystemContext, default);

        Assert.Equal("web sources disabled by configuration", result.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    // ---------------------------------------------------------------- placeholders

    [Theory]
    [InlineData("https://www.7-zip.org/a/7z{version_nodots}-x64.exe", "26.03", "https://www.7-zip.org/a/7z2603-x64.exe")]
    [InlineData("https://x/{version}/npp.{version}.Installer.x64.exe", "8.9.8", "https://x/8.9.8/npp.8.9.8.Installer.x64.exe")]
    [InlineData("https://x/v{version_underscore}.zip", "1.2.3", "https://x/v1_2_3.zip")]
    [InlineData("https://x/{version_major}/setup.exe", "155.0.1", "https://x/155/setup.exe")]
    [InlineData("https://x/{version_major_minor}/setup.exe", "155.0.1", "https://x/155.0/setup.exe")]
    [InlineData("https://x/{version_major_minor}/{version_major}/{version}", "3.0.23", "https://x/3.0/3/3.0.23")]
    [InlineData("https://x/setup.exe", "1.2.3", "https://x/setup.exe")]
    [InlineData("https://x/{VERSION}.exe", "1.2.3", "https://x/1.2.3.exe")]
    [InlineData("https://x/{version}.exe", "v1.2.3", "https://x/1.2.3.exe")]      // leading v normalised away
    [InlineData(null, "1.2.3", null)]
    public void ExpandPlaceholders_Works(string? template, string version, string? expected) =>
        Assert.Equal(expected, WebProvider.ExpandPlaceholders(template, version));

    [Fact]
    public async Task UserContextPrefersTheUserDownloadUrlAndArgs()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"productVersion": "1.137.0"}"""));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "vscode",
            Source = UpdateSource.Web,
            VersionUrl = "https://update.code.visualstudio.com/api/update/win32-x64/stable/latest",
            VersionRegex = "\"productVersion\":\\s*\"([^\"]+)\"",
            DownloadUrl = "https://update.code.visualstudio.com/latest/win32-x64/stable",
            InstallerArgs = "/VERYSILENT /NORESTART",
            UserDownloadUrl = "https://update.code.visualstudio.com/latest/win32-x64-user/stable",
            UserInstallerArgs = "/VERYSILENT /NORESTART /MERGETASKS=!runcode",
        };
        var installed = new InstalledApp { DisplayName = "Microsoft Visual Studio Code (User)", DisplayVersion = "1.103.2", Context = InstallContext.User };

        var user = await provider.CheckAsync(app, installed, UserContext, default);
        Assert.Equal("https://update.code.visualstudio.com/latest/win32-x64-user/stable", user.DownloadUrl);
        Assert.Equal("/VERYSILENT /NORESTART /MERGETASKS=!runcode", user.InstallerArgs);
        Assert.Equal(InstallContext.User, user.ResolvedContext);

        var system = await provider.CheckAsync(app, installed, SystemContext, default);
        Assert.Equal("https://update.code.visualstudio.com/latest/win32-x64/stable", system.DownloadUrl);
        Assert.Equal("/VERYSILENT /NORESTART", system.InstallerArgs);
    }

    // ---------------------------------------------------------------- SHA-256

    [Fact]
    public async Task FetchesTheSha256FromTheChecksumFile()
    {
        const string hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        var handler = new FakeHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                ? FakeHandler.Text($"{hash}  app-1.2.3.exe")
                : FakeHandler.Text("version 1.2.3"));
        using var provider = Create(handler);

        var app = new AppPolicy
        {
            AppId = "demo",
            Source = UpdateSource.Web,
            VersionUrl = "https://example.test/version",
            VersionRegex = @"version ([\d.]+)",
            DownloadUrl = "https://example.test/app-{version}.exe",
            Sha256Url = "https://example.test/app-{version}.exe.sha256",
        };

        var result = await provider.CheckAsync(app, Installed("Demo", "1.0.0"), SystemContext, default);

        Assert.Equal("https://example.test/app-1.2.3.exe", result.DownloadUrl);
        Assert.Equal(hash, result.Sha256);
    }

    [Theory]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  app.exe", null, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("SHA256: 9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", null, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("no hash here", null, null)]
    [InlineData("", null, null)]
    public void ExtractSha256_Works(string text, string? fileName, string? expected) =>
        Assert.Equal(expected, WebProvider.ExtractSha256(text, fileName));

    [Fact]
    public void ExtractSha256_PrefersTheLineMentioningTheFile()
    {
        const string text = """
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  app-x86.exe
            bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  app-x64.exe
            cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc  app-arm64.exe
            """;

        Assert.Equal(new string('b', 64), WebProvider.ExtractSha256(text, "app-x64.exe"));
        Assert.Equal(new string('a', 64), WebProvider.ExtractSha256(text, "not-listed.exe"));  // falls back to the first
    }

    // ---------------------------------------------------------------- install

    [Fact]
    public async Task InstallFailsCleanlyOnASha256Mismatch()
    {
        var payload = "not a real installer"u8.ToArray();
        var handler = new FakeHandler(_ => FakeHandler.Binary(payload, "demo-setup.exe"));
        var downloadDirectory = Path.Combine(Path.GetTempPath(), "Arkimentum.AppMonitor.Tests", Guid.NewGuid().ToString("N"));
        using var provider = Create(handler, new ProviderOptions { DownloadDirectory = downloadDirectory });

        var app = new AppPolicy { AppId = "demo", Source = UpdateSource.Web };
        var update = new PendingUpdate
        {
            AppId = "demo",
            Source = UpdateSource.Web,
            DownloadUrl = "https://example.test/demo-setup.exe",
            InstallerType = InstallerType.Exe,
            Sha256 = new string('0', 64),
        };

        try
        {
            var result = await provider.InstallAsync(app, update, SystemContext, progress: null, default);

            Assert.False(result.Success);
            Assert.Contains("SHA-256", result.Message, StringComparison.OrdinalIgnoreCase);
            // the bad download must not be left behind
            Assert.Empty(Directory.Exists(downloadDirectory) ? Directory.GetFiles(downloadDirectory) : []);
        }
        finally
        {
            try { Directory.Delete(downloadDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InstallFailsWithoutADownloadUrl()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("x"));
        using var provider = Create(handler);

        var app = new AppPolicy { AppId = "demo", Source = UpdateSource.Web };
        var update = new PendingUpdate { AppId = "demo", Source = UpdateSource.Web };

        var result = await provider.InstallAsync(app, update, SystemContext, null, default);
        Assert.False(result.Success);
        Assert.Contains("download URL", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- file naming

    [Fact]
    public void ResolveFileName_PrefersContentDisposition()
    {
        using var response = FakeHandler.Binary([1, 2, 3], "VSCodeSetup-x64-1.137.0.exe");
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://update.code.visualstudio.com/latest/win32-x64/stable");

        Assert.Equal("VSCodeSetup-x64-1.137.0.exe",
            WebProvider.ResolveFileName(response, "https://update.code.visualstudio.com/latest/win32-x64/stable", "vscode", InstallerType.Exe));
    }

    [Fact]
    public void ResolveFileName_FallsBackToTheFinalUrl()
    {
        using var response = FakeHandler.Binary([1, 2, 3], fileName: null);
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://ask4.mm.fcix.net/videolan-ftp/vlc/3.0.23/win64/vlc-3.0.23-win64.exe");

        Assert.Equal("vlc-3.0.23-win64.exe",
            WebProvider.ResolveFileName(response, "https://get.videolan.org/vlc/3.0.23/win64/vlc-3.0.23-win64.exe", "vlc", InstallerType.Exe));
    }

    [Fact]
    public void ResolveFileName_GeneratesASafeNameWhenTheUrlHasNoFileName()
    {
        using var response = FakeHandler.Binary([1, 2, 3], fileName: null);
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://download.mozilla.org/?product=firefox-latest-ssl");

        var name = WebProvider.ResolveFileName(response, "https://download.mozilla.org/?product=firefox-latest-ssl", "firefox", InstallerType.Msi);
        Assert.StartsWith("firefox-", name);
        Assert.EndsWith(".msi", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void ResolveFileName_RejectsPathTraversalInContentDisposition()
    {
        using var response = FakeHandler.Binary([1, 2, 3], @"..\..\Windows\System32\evil.exe");
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.test/x");

        var name = WebProvider.ResolveFileName(response, "https://example.test/x", "demo", InstallerType.Exe);
        Assert.Equal("evil.exe", name);
        Assert.DoesNotContain("..", name);
    }

    /// <summary>Test double for <see cref="HttpMessageHandler"/>; records the requests it was given.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public int RequestCount => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }

        public static HttpResponseMessage Text(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };

        public static HttpResponseMessage Binary(byte[] bytes, string? fileName)
        {
            var content = new ByteArrayContent(bytes);
            if (fileName is not null)
            {
                content.Headers.ContentDisposition =
                    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = fileName };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}

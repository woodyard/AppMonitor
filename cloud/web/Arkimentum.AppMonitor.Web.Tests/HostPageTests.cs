using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// The host page is plain HTML that no compiler checks, and the first deployment shipped without the MSAL script:
/// every page then died on first render with "AuthenticationService was undefined". These tests read the real
/// <c>wwwroot/index.html</c> and <c>staticwebapp.config.json</c> from the project so that cannot happen silently.
/// </summary>
public sealed class HostPageTests
{
    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Arkimentum.AppMonitor.Web", "wwwroot", "index.html");
            if (File.Exists(candidate)) return Path.Combine(directory.FullName, "Arkimentum.AppMonitor.Web");
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Arkimentum.AppMonitor.Web/wwwroot/index.html was not found above " + AppContext.BaseDirectory);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([ProjectRoot(), .. parts]));

    [Fact]
    public void The_host_page_loads_the_MSAL_browser_script_before_Blazor()
    {
        var html = Read("wwwroot", "index.html");

        var msal = html.IndexOf("_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js", StringComparison.Ordinal);
        var blazor = html.IndexOf("_framework/blazor.webassembly", StringComparison.Ordinal);

        Assert.True(msal >= 0, "index.html must reference the MSAL AuthenticationService.js script.");
        Assert.True(blazor >= 0, "index.html must reference the Blazor WebAssembly script.");
        Assert.True(msal < blazor, "The MSAL script must come before the Blazor script.");
    }

    [Fact]
    public void The_host_page_does_not_reference_a_scoped_css_bundle_the_project_never_produces()
    {
        // No component uses scoped CSS (.razor.css), so no *.styles.css bundle exists; a link to it is served as the
        // fallback HTML page and every browser logs a MIME-type error for it.
        var html = Read("wwwroot", "index.html");
        var project = ProjectRoot();
        var hasScopedCss = Directory.EnumerateFiles(project, "*.razor.css", SearchOption.AllDirectories).Any();

        if (!hasScopedCss) Assert.DoesNotContain(".styles.css", html);
    }

    [Fact]
    public void Missing_files_are_not_rewritten_to_the_app_page()
    {
        // navigationFallback (with its excludes) is the SPA fallback; a blanket 404 override on top of it would hand
        // the Blazor runtime an HTML page for any framework file that failed to publish.
        var config = Read("wwwroot", "staticwebapp.config.json");

        Assert.Contains("\"navigationFallback\"", config);
        Assert.DoesNotContain("\"responseOverrides\"", config);
    }
}

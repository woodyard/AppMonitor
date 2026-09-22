using System.Net.Http.Json;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web;
using Arkimentum.AppMonitor.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// =====================================================================================================================
// The browser admin console for the AppMonitor cloud API.
//
// Startup order matters: the sign-in configuration is not compiled in, it is fetched from the API's anonymous
// /public/auth-config endpoint *before* the host is built, so a re-registered Entra application only needs the API
// redeployed. The one thing the deployment configures locally is ApiBaseUrl in wwwroot/appsettings.json, which the
// cloud deploy script writes at publish time.
//
// Nothing here throws on a misconfigured deployment: an empty URL or a server that does not answer leaves
// StartupState.IsConfigured false, and App.razor renders an explanatory page instead of a blank one.
// =====================================================================================================================

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var startup = new StartupState(StartupState.Normalise(builder.Configuration["ApiBaseUrl"]));

if (startup.IsMissingUrl)
{
    startup.Error = "No API server is configured. Set \"ApiBaseUrl\" in wwwroot/appsettings.json to the URL of the AppMonitor Function App.";
}
else
{
    try
    {
        using var probe = new HttpClient { BaseAddress = new Uri(startup.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        startup.AuthConfig = await probe.GetFromJsonAsync<AuthConfigResponse>(CloudRoutes.AuthConfig.TrimStart('/'), CloudJson.Options);
        if (startup.AuthConfig is null or { ClientId.Length: 0 } or { Authority.Length: 0 })
        {
            startup.AuthConfig = null;
            startup.Error = $"{startup.ApiBaseUrl} answered, but has no Entra ID application registered. Run the cloud deployment script to configure sign-in.";
        }
    }
    catch (Exception ex)
    {
        startup.Error = $"Could not read the sign-in configuration from {startup.ApiBaseUrl}{CloudRoutes.AuthConfig.TrimStart('/')} ({ex.Message}). " +
                        "Check the URL and that the API allows this origin (CORS).";
    }
}

builder.Services.AddSingleton(startup);

// Same-origin client: the catalog and any other static asset the console reads about itself. Never authenticated.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ClipboardService>();

if (startup.AuthConfig is { } authConfig)
{
    builder.Services.AddMsalAuthentication(options =>
    {
        var authentication = options.ProviderOptions.Authentication;
        authentication.ClientId = authConfig.ClientId;
        // Multi-tenant ("…/organizations"): every work account signs in here and the API decides what they may see,
        // so the issuer cannot be validated against a single tenant.
        authentication.Authority = authConfig.Authority;
        authentication.ValidateAuthority = false;
        options.ProviderOptions.LoginMode = "redirect";
        options.ProviderOptions.DefaultAccessTokenScopes.Add(authConfig.Scope);
    });

    builder.Services.AddScoped<ApiAuthorizationMessageHandler>();
    builder.Services.AddScoped(sp =>
    {
        var handler = sp.GetRequiredService<ApiAuthorizationMessageHandler>();
        handler.InnerHandler = new HttpClientHandler();
        return new AdminApiClient(new HttpClient(handler) { BaseAddress = new Uri(startup.ApiBaseUrl) });
    });
    builder.Services.AddScoped<OrganizationState>();
    builder.Services.AddScoped<ConfigWorkspace>();
}

await builder.Build().RunAsync();

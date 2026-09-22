# Browser admin console

`Arkimentum.AppMonitor.Web` is a Blazor WebAssembly **standalone** app (net10.0, no AOT, no PWA, no hosted
server). It is the organization admin console for the cloud API in `cloud/api`: overview, devices, fleet
inventory, applications, settings, activity and enrollment — the pages the WPF console offers under
"Organization", in a browser.

It is published as static files to an Azure Static Web App and calls the Function App cross-origin.

## How configuration reaches the app

The only thing the deployment configures locally is the API URL:

```jsonc
// wwwroot/appsettings.json — the cloud deploy script writes this at publish time
{ "ApiBaseUrl": "https://appmon-prod-func-xxxxxx.azurewebsites.net" }
```

Everything else comes from the API itself. Before the first component renders, `Program.cs` fetches the
anonymous `GET {ApiBaseUrl}/api/v1/public/auth-config` and configures MSAL from it — `clientId`, `authority`
(multi-tenant, `…/organizations`) and `scope`. Re-registering the Entra application therefore needs the API
redeployed, not this app rebuilt.

If `ApiBaseUrl` is empty, or the probe fails, the app still starts and renders a page that says exactly what is
missing instead of a blank screen.

The wire contracts are not copied: `Arkimentum.AppMonitor.Api/Contracts/*.cs` is compiled straight into this
project (`CloudRoutes`, `CloudJson`, the admin DTOs, `SettingsDocument`, `SettingsSchema`), so the console
cannot drift from the server. `catalog/catalog.json` is copied into `wwwroot/catalog.json` by an MSBuild target
and served from the app's own origin.

## Running it locally

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"
dotnet run --project cloud/web/Arkimentum.AppMonitor.Web --launch-profile https
```

The dev server listens on a fixed **https://localhost:7200** (http://localhost:7201), so the redirect URI does
not move between runs. `wwwroot/appsettings.Development.json` points at `http://localhost:7071`, which is where
`func start` serves `cloud/api`.

Two things have to allow that origin before sign-in works:

* **CORS on the API.** `func start` allows `*` by default through `local.settings.json`; a deployed Function App
  needs `https://localhost:7200` added to its CORS allowed origins.
* **The Entra app registration.** Add `https://localhost:7200/authentication/login-callback` as a redirect URI of
  type **SPA** (not Web) on the client application, alongside the deployed console's own callback.

## Publishing

```powershell
dotnet publish cloud/web/Arkimentum.AppMonitor.Web -c Release -o <dir>
```

The uploadable site is `<dir>/wwwroot` — `index.html`, `_framework/`, `css/`, `catalog.json`,
`appsettings.json` and `staticwebapp.config.json` (navigation fallback to `index.html` plus the security
headers). Write the real `ApiBaseUrl` into `<dir>/wwwroot/appsettings.json` before uploading.

## Layout

| Path | What |
| --- | --- |
| `Program.cs` | Reads `ApiBaseUrl`, probes `/public/auth-config`, configures MSAL and the authenticated `HttpClient`. |
| `Services/` | `AdminApiClient` (every admin endpoint, typed, throwing `ApiException`), `OrganizationState`, `ConfigWorkspace` (the shared pending document, publish/409/history), `CatalogService`, `ProvisioningSnippets`. |
| `Editing/` | `ConfigDocumentEditor`, `AppEditor`, `SettingRow` — plain C#, no Blazor, ported from the WPF console's view models and covered by `Arkimentum.AppMonitor.Web.Tests`. |
| `Pages/`, `Layout/`, `Shared/` | The seven pages, the shell and the small components (banner, modal, pager, copy button). |
| `wwwroot/css/app.css` | The whole look: brand tokens on `:root`, a dark scheme, no framework. |

Applications and Settings edit **one** pending document and share the Publish footer. Publishing is optimistic:
the PUT carries the version the editor started from as `If-Match`, and a 409 offers "reload theirs" or "publish
mine over it" rather than resolving silently.

## Trimming

`dotnet publish` trims the framework (`TrimMode=partial`: this app's own assembly is left alone, the .NET
assemblies are not). The console deserializes the API contracts with reflection-based `System.Text.Json`, and the
serializer creates collections through their **parameterless** constructor - which the trimmer removes when nothing
in the app calls it. `SettingsDocument` only ever constructs its `SortedDictionary` fields with a comparer, so the
first deployment failed on every organization configuration with
`NotSupportedException: DeserializeNoConstructor ... SortedDictionary`2` - in the browser only; the unit tests, which
run on the full runtime, were green.

`ILLink.Descriptors.xml` (wired in through `TrimmerRootDescriptor`) keeps those types whole. Add any new contract
collection type there that the app never news up with its default constructor.

**How to prove a change in the trimmed runtime** (nothing in the test suite can): publish a throwaway Blazor
WebAssembly project that links `Contracts/*.cs`, `Editing/*.cs` and `Catalog/*.cs`, put captured API responses into
its `wwwroot`, deserialize each one with `CloudJson.Options` in `Program.cs` and `Console.WriteLine` the outcome
(that goes to the browser console), serve the publish output with `npx serve -s`, and read the console with headless
Edge over the DevTools protocol (`--headless=new --remote-debugging-port=9333`, then `Runtime.enable` and listen for
`Runtime.consoleAPICalled`). That is how the failure above was reproduced and the fix verified on 2026-09-22; the
sanitized configuration and history payloads from that day live in `Arkimentum.AppMonitor.Web.Tests/Fixtures`.

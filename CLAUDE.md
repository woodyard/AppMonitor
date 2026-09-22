# Arkimentum AppMonitor — working notes for Claude

Windows update-management agent for third-party applications, built for cloud-only (Entra ID / Intune) fleets.
Owner: Henrik Skovgaard (Arkimentum). Repository: https://github.com/woodyard/AppMonitor (public).

## What is in here

| Path | What | Notes |
| --- | --- | --- |
| `src/Arkimentum.AppMonitor.Core` | Shared library: models, registry configuration reader, settings schema/document/exporters, IPC (named pipe), providers (winget, web), inventory, prerequisites, cloud client/contracts | **Contracts live here** (Models, Ipc, Cloud/CloudContracts.cs, Configuration/SettingsSchema.cs). Change them deliberately; everything compiles against them. |
| `src/Arkimentum.AppMonitor.Service` | Windows service (LocalSystem): scan loop, policy engine (deferrals, deadlines, forced close), installs, tray launcher, cloud sync, self-update | Console mode: `--console`; testing without admin: `--user-config` (HKCU + %LOCALAPPDATA%). |
| `src/Arkimentum.AppMonitor.Tray` | Per-user WPF tray agent: toasts, update cards, close-apps dialog, user-context scans/installs | Talks to the service over pipe `Arkimentum.AppMonitor.Agent`. |
| `src/Arkimentum.AppMonitor.Admin` | WPF organization console (Entra sign-in, devices, inventory, org config, enrollment). Runs unelevated. The local "This machine" pages are **deprecated** and only appear with `--local` (elevates) | `--user-config` for unprivileged testing; headless `--export` / `--import` (deprecated). Links to the web console when the API returns `webAdminUrl`. |
| `src/Arkimentum.AppMonitor.UI` | Shared WPF brand theme (Fluent + Arkimentum palette, Gelasio wordmark, BrandHeader) | Adopt via `BrandTheme.Initialize()`; see the project README comments. |
| `src/Arkimentum.AppMonitor.Tests` | xunit tests for Core + Service | ~470 tests; registry tests use a throw-away HKCU key. |
| `cloud/` | Azure Functions API (.NET 10 isolated, EF Core, Azure SQL, Blob), Bicep, deploy scripts, OpenAPI | Has its **own copy** of the DTOs and SettingsSchema (Core is net10.0-windows); parity tests in `cloud/api/*.Tests` fail if they drift — update both. |
| `cloud/web/Arkimentum.AppMonitor.Web` | Browser admin console: Blazor WebAssembly standalone, MSAL sign-in, hosted on an Azure Static Web App (Free) provisioned by `main.bicep`; deployed by `Deploy-Cloud.ps1` (SWA CLI via npx) or `.github/workflows/cloud-web.yml` | Compiles the API's `Contracts/*.cs` as linked files (no third copy). Only deploy-time config is `wwwroot/appsettings.json` → `ApiBaseUrl`; everything else comes from `/api/v1/public/auth-config`. Dev origin `https://localhost:7200` needs CORS + a SPA redirect URI. |
| `deploy/` | `Build-Release.ps1`, install/uninstall scripts, sample config | Scripts must stay Windows PowerShell 5.1 compatible. |
| `catalog/catalog.json` | App templates (winget ids incl. `;`-separated alternatives, vendor web URLs, process names) | Catalog supplies identity/detection only, never behaviour. |
| `docs/` | Registry reference, architecture, cloud, self-update, admin console, troubleshooting | Keep in sync with `SettingsSchema.cs` (single source of truth for setting names/defaults/ranges). |
| `.github/workflows` | `ci.yml` (build+test), `release.yml` (tag `v*` -> zip + manifest.json on GitHub Releases) | The release IS the self-update feed (`AgentUpdateFeedUrl` default). |

## Build and test

- No system-wide .NET SDK on Henrik's machine: use the user-local one. PowerShell:
  `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"` (also needed in the environment when launching the WPF exes). See `BUILD-ENV.md`.
- `dotnet build Arkimentum.AppMonitor.slnx` (0 warnings expected), `dotnet test src/Arkimentum.AppMonitor.Tests`, `dotnet test cloud/api/Arkimentum.AppMonitor.Api.Tests`.
- Release package: `deploy\Build-Release.ps1 -Version x.y.z` -> `artifacts\Arkimentum.AppMonitor-x.y.z.zip` (self-contained, Service/Tray/Admin + scripts + docs).
- Publishing a release: commit, `git tag vX.Y.Z`, `git push origin vX.Y.Z`; the workflow builds and publishes. Bump the version in the tag only (Build-Release takes it from the tag).

## Conventions and hard-won rules

- Configuration precedence: HKLM Policies key > organization config from the cloud (`cloud-config.json`) > HKLM preferences > catalog > defaults. Cloud values can never override the `Cloud*` connection values.
- Adding a setting = `SettingsSchema.cs` + `AgentSettings.cs` + `RegistryConfigurationReader.cs` + the mirrored `cloud/api/.../Contracts/SettingsSchema.cs` + `docs/Registry.md`. Tests will remind you about the mirror.
- winget quirks handled in `WingetProvider`: `--scope machine|user` separates contexts; ids may list alternatives (`Mozilla.Firefox;Mozilla.Firefox.MSIX`) and installs fall back through them when winget says "no applicable upgrade"; per-id lookup can miss packages the full listing shows, so there is a full-listing fallback; never report success when the version did not move.
- Installed updates are hidden from tray/admin as soon as they succeed; the service keeps the record briefly (post-install grace) to avoid re-flagging.
- The real service may be installed and running on the dev machine: never stop it or write HKLM from a session; test with `--user-config`. Stop it (`Stop-Service ArkimentumAppMonitor`) only when the user asks for a console-mode test, because both instances share the pipe name.
- Self-update launches the shipped installer detached as SYSTEM; the installer must never prompt.
- Brand: ground #f5f1ee, ink #2a2e22, olive #7a894a (dark: chartreuse #c7ca5c), terracotta #c76239, sky #389dc6; Gelasio for the wordmark, Segoe UI Variable for body.
- Henrik's preference: use Opus subagents for parallel implementation work; the main session integrates, reviews and verifies. Opus session limits reset at 20:00 Europe/Copenhagen; resume interrupted agents rather than restarting them.

## Status and open items (2026-09-15)

- Shipped: v1.1.20 on GitHub Releases (2026-09-22; 1.1.14-1.1.19 the same day; 1.1.18 is broken - manifest and zip from two interleaved release runs - and must not be reused): browser admin console, WPF local mode deprecated, stale scheduled updates pruned, web-source validation in both consoles, per-app `WingetReplaceOnMismatch` (winget uninstall, retried with --all-versions, judged by the listing afterwards, stale Windows Installer registrations that msiexec disowns deleted, then install, on the 0x8A15008E "install technology is different" refusal; Oh My Posh on H-PARALLELSVM is the case), per-app `NotifyInstalling` + `DefaultNotifyInstalling` (install toast: auto/always/never), `AutoInstall` retitled "Install automatically", no stacking of deferrals, ADMX/ADML templates removed, post-install grace skipped when the install verified the version (a manual downgrade is a new update). Devices pick it up by self-update.
- Browser admin console is live on the Static Web App `appmon-prod-web-ifwyoo` (https://brave-moss-0901a6003.4.azurestaticapps.net); `/api/v1/public/auth-config` returns `webAdminUrl`. **The API and the web console must be redeployed** for the new setting: the live API validates every setting name against its own copy of `SettingsSchema` and rejects `WingetReplaceOnMismatch` until then. Re-run `Deploy-Cloud.ps1` with `-MigrationMode Skip` (the `-AdditionalWebOrigins @('https://localhost:7200')` dev origin is optional). Sessions running in auto mode cannot publish or deploy the cloud parts (classifier: production deploy) — Henrik runs it.
- Not yet exercised for real: Azure deployment (`cloud/deploy/Deploy-Cloud.ps1`), a live enrollment/report round-trip, Entra sign-in in the admin console, the self-update installer hand-over. First real deployment is the main risk.
- Follow-ups noted in docs: Authenticode signing of binaries/scripts, private-repo update feeds (token), moving the Fluent CheckBox/RadioButton accent fix from the Admin theme into the shared UI library.

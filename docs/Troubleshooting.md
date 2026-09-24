# Troubleshooting

Start here, always:

```powershell
# service log (today)
Get-Content "$env:ProgramData\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Service_$(Get-Date -Format yyyyMMdd).log" -Tail 200

# tray log for the current user
Get-Content "$env:LOCALAPPDATA\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Tray_$(Get-Date -Format yyyyMMdd).log" -Tail 200

# service state and event log
Get-Service ArkimentumAppMonitor
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Arkimentum AppMonitor' } -MaxEvents 20
```

Raise the detail level while investigating and restart the service:

```powershell
Set-ItemProperty 'HKLM:\SOFTWARE\Arkimentum\AppMonitor' -Name LogLevel -Value 'Debug'
Restart-Service ArkimentumAppMonitor
```

Set it back to `Information` afterwards - `Debug` and `Trace` produce large files.

On a device managed by the cloud service, `LogLevel` may be coming from the organization configuration
and out-rank what you just wrote - `--show-config` says which layer won. See
[The cloud service](#the-cloud-service) and [Agent self-update](#agent-self-update) below.

## Run the service in a console

The fastest way to see what the agent does is to stop the service and run the same executable
interactively, from an **elevated** prompt:

```powershell
Stop-Service ArkimentumAppMonitor
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --console
```

It logs to the console and to the normal log file, and still serves the named pipe, so a tray agent
keeps working against it. Ctrl+C stops it; start the service again afterwards.

The executable is manifested `asInvoker`, so it starts without a UAC prompt; when console mode is not
elevated it logs a warning, because machine-wide installs and the inventory of other users' hives
will fail. A console run as an administrator is still not the same as LocalSystem - problems specific
to the service account (winget, proxy, per-user paths) need the service itself, or a console started
as SYSTEM with PsExec (`psexec -s -i`).

Command-line switches:

| Switch | Effect |
| --- | --- |
| `--console` | Run interactively instead of under the SCM. Ctrl+C stops it. |
| `--scan-once` | Run exactly one scan in the console, then exit. |
| `--show-config` | Print the effective configuration, the resolved applications and the layer each value came from, then exit. |
| `--no-delay` | Skip `StartupDelaySeconds` (implied by `--console` and `--scan-once`). |
| `--user-config` | **Testing only.** Read the configuration from `HKCU\SOFTWARE\Arkimentum\AppMonitor` instead of HKLM and keep logs and state under `%LOCALAPPDATA%\Arkimentum\AppMonitor`, so the whole pipeline can be exercised without administrative rights. |
| `--version` | Print the service version and exit. |

A quick end-to-end check without touching the machine configuration:

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --show-config
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --scan-once --no-delay
```

## winget is not found under SYSTEM

**Symptom:** every winget application fails with "winget not found" or a provider error; web-source
applications work.

winget ships in the App Installer MSIX package, which is installed *per user*. LocalSystem often has
no registered copy, and `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` (the alias) does not exist
for the service account.

**Ask the agent first.** The service repairs this by itself when `AutoInstallPrerequisites = 1` (the
default), so before doing anything by hand, run the check and read what it says:

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --prerequisites
# 0 = winget is healthy for SYSTEM, 2 = still unavailable, 1 = the check itself failed
```

The same information - detected version and path, healthy or not, last action, last error - is on the
admin console's Overview page, together with an *Install or repair prerequisites* button. The
service's own attempts are in the service log; the lines are tagged `Prerequisite`:

```powershell
Select-String -Path "$env:ProgramData\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Service_*.log" -Pattern 'Prerequisite' |
    Select-Object -Last 40
```

Common reasons the automatic repair does not succeed:

- **No internet access.** It needs outbound HTTPS to `github.com` and `objects.githubusercontent.com`
  (App Installer release assets), `aka.ms`, `nuget.org` (VCLibs 14 Desktop, Microsoft.UI.Xaml 2.8) and
  the PowerShell Gallery (`www.powershellgallery.com`, `psg-prod-eastus.azureedge.net`) for the
  `Microsoft.WinGet.Client` module route. Allow those endpoints for the SYSTEM account, or accept that
  **offline machines must have App Installer provisioned in the image** and set
  `AutoInstallPrerequisites = 0` so the service stops trying.
- **A proxy that SYSTEM does not use.** `ProxyUrl` configures the agent's own web downloads, not
  winget and not PowerShell; the machine-wide (WinHTTP) proxy is what the repair sees -
  `netsh winhttp show proxy`.
- **WDAC/AppLocker or an MSIX policy** that blocks `Add-AppxProvisionedPackage` or PSGallery installs.
- **The check is turned off** - `AutoInstallPrerequisites = 0`, or the value is set by Group Policy
  (the admin console shows it locked).

Check what the machine has:

```powershell
Get-AppxPackage -AllUsers Microsoft.DesktopAppInstaller |
    Select-Object Name, Version, PackageUserInformation
Get-ChildItem 'C:\Program Files\WindowsApps' -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' -Directory |
    ForEach-Object { Join-Path $_.FullName 'winget.exe' } | Where-Object { Test-Path $_ }
```

Fixes, in order of preference:

0. Let the service do it: `AutoInstallPrerequisites = 1`, then
   `Arkimentum.AppMonitor.Service.exe --prerequisites` (or the admin console button). It installs or
   repairs the package for all users and the tray agent registers it for each signed-in user.
1. Provision App Installer for all users (Microsoft Store for Business / Intune / `Add-AppxProvisionedPackage`),
   then have each user sign in once so the package is registered. On Windows 11 Enterprise it is
   normally already provisioned.
2. Install or update it from the Microsoft Store on a pilot machine and confirm
   `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe` exists.
3. If the binary exists but the agent does not find it, point it there explicitly:
   `WingetPath = C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_<version>_x64__8wekyb3d8bbwe\winget.exe`
   (REG_SZ). Treat this as temporary - the path contains the package version and breaks on the next
   App Installer update.
4. If winget genuinely cannot be made available on the device, turn it off
   (`WingetEnabled = 0`) and configure the applications you need as web sources instead.

Verify as SYSTEM, not as yourself:

```powershell
psexec -s -accepteula winget --version
```

## winget asks for source agreements

**Symptom:** winget calls hang or fail on first use, or the log shows a prompt about source
agreements or the MSIX terms.

The agreement is per user, and LocalSystem has its own profile. Run once as SYSTEM:

```powershell
psexec -s -accepteula winget list --accept-source-agreements --accept-package-agreements
```

Then confirm `winget source list` works as SYSTEM. If your environment needs additional flags for
every call (for example `--disable-interactivity`), set them once in `WingetGlobalArgs`; avoid
arguments that change winget's output format, because update detection parses that output.

## No tray icon, no notifications

Check in this order:

1. Is the tray process running in the user's session?
   `Get-Process Arkimentum.AppMonitor.Tray -ErrorAction SilentlyContinue`
2. Is launching enabled? `LaunchTrayAgent` must not be 0 in either the policy or the preference key.
3. Is the executable where the service looks? The search order is the `TrayPath` value (read from
   `HKLM\SOFTWARE\Arkimentum\AppMonitor` only, not from the Policies key), then the service's own folder,
   then `..\Tray\`. A non-standard layout needs
   `TrayPath = <full path to Arkimentum.AppMonitor.Tray.exe>` (REG_SZ).
4. Is the logon fallback in place?
   `Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name ArkimentumAppMonitorTray`
5. Start it by hand in the user's session and read the tray log:
   `& "$env:ProgramFiles\Arkimentum\AppMonitor\Tray\Arkimentum.AppMonitor.Tray.exe"`
6. Only one instance runs per session (mutex `Local\Arkimentum.AppMonitor.Tray`); a second start exits
   silently by design.
7. The icon may be hidden in the notification-area overflow - check Taskbar settings.
8. No toasts at all, but the icon works: `NotificationsEnabled` is 0, or Windows Focus assist /
   notification settings suppress them for `Arkimentum.AppMonitor.Tray`.

Remote Desktop and Windows "session 0" services: the tray agent only exists in interactive sessions.
If nobody is logged on, user-context updates simply wait.

## The tray agent cannot connect to the service

**Symptom:** the tray log repeats connection failures to `Arkimentum.AppMonitor.Agent`.

- Is the service running? `Get-Service ArkimentumAppMonitor`. If it is stopped, the pipe does not exist.
- Does the pipe exist? `[System.IO.Directory]::GetFiles('\\.\pipe\') -match 'Arkimentum'`
- Access denied: the pipe allows Authenticated Users, so a denial usually means the client is not an
  authenticated interactive user (a sandbox, a different logon type, or an AppContainer). Run the
  tray agent as the normal signed-in user.
- Third-party endpoint protection can block named-pipe access between a service and user
  applications; allow `Arkimentum.AppMonitor.Service.exe` and `Arkimentum.AppMonitor.Tray.exe`.
- Only one service instance may own the pipe. If you are running `--console` *and* the service, stop
  one of them.

## An application is never detected

The agent compares the version found in the machine's Uninstall keys with the version from the
source. Detection fails when the regex does not match.

```powershell
# what the inventory actually contains
$paths = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
         'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
Get-ItemProperty $paths -ErrorAction SilentlyContinue |
    Where-Object DisplayName -match '7-?zip' |
    Select-Object DisplayName, DisplayVersion, Publisher

# test the configured regex against it
'7-Zip 26.03 (x64)' -match '^7-Zip'
```

Common causes:

- `DetectDisplayNameRegex` too strict (vendor renamed the entry, added "(x64)", a locale suffix or a
  version number). Without the regex, the configured `DisplayName` is matched as a whole-word prefix.
- The application is per-user only, so the service's machine inventory does not see it - set
  `Context = user` (or leave `auto` and make sure the user is logged on with a tray agent running).
- `DetectPublisherRegex` does not match the actual `Publisher` string.
- The entry is filtered out on purpose: entries marked `SystemComponent = 1`, with a
  `ReleaseType` of `Update`/`Hotfix`/`Security Update`, or with a `ParentKeyName` are skipped.
- Web source: `VersionRegex` no longer matches the vendor page (see below).
- winget source: the `WingetId` is wrong and no row of `winget list` passes the identity rule either
  (see [An install asked for administrator rights](#an-install-asked-for-administrator-rights) for how
  the id is resolved), or winget reports the installed version as "Unknown" (then
  `WingetIncludeUnknown` decides whether it counts).
- For an application whose registry version is unreliable, point `DetectFilePath` at the installed
  executable and let the file version decide.

### The organization Inventory says "no winget package" for a per-user install

The winget ids of per-user installs come from the tray agent: the service runs as LocalSystem and its
own `winget list --scope user` only sees SYSTEM's packages, so before each inventory it asks every
connected tray agent for that user's listing (`runUserPackageList`, about 45 seconds). A user who has
no tray agent running, or one from a version that predates the message, therefore shows "no winget
package" for their per-user installs unless the catalog (or a configured application) already knows
the product and supplies the id. The service log says how many user-scope rows each agent reported.

## A web source stopped working

Vendor pages change. Check the two moving parts by hand:

```powershell
$html = Invoke-WebRequest 'https://www.7-zip.org/download.html' -UseBasicParsing
[regex]::Match($html.Content, 'Download 7-Zip (\d+\.\d+)').Groups[1].Value   # -> 26.03
```

Then confirm the download URL that the placeholders produce
(`{version}`, `{version_nodots}`, `{version_major}`, `{version_underscore}`) really exists. If the
vendor moved to a different URL pattern, update `DownloadUrl`, and update `Sha256Url`/`Sha256` if you
verify hashes.

### The application vanished after switching its source to the vendor web site

Switching an application from winget to `web` is not a way around a winget refusal: the web source
needs `VersionUrl`, `VersionRegex` and `DownloadUrl` of its own (or a catalog entry that supplies
them). Without them the configuration reader drops the application on every reload and logs

```text
[WRN] RegistryConfigurationReader: App JanDeDobbeleer.OhMyPosh uses the web source but lacks VersionUrl/DownloadUrl; skipping
```

so it is neither scanned nor listed, whatever `Enabled` says. Both admin consoles now refuse to
publish or save such an application and name the missing fields; fill them in, or set the source back
to winget. For a package winget refuses because the install technology changed, see
[winget says the install technology is different](#winget-says-the-install-technology-is-different).

An update that was already scheduled when the application was dropped used to stay in the tray at
"Preparing updates…" for good: the install flow found no policy to run and left the update scheduled.
The agent now forgets tracked updates of applications that are no longer configured, enabled or valid,
both at the next scan and the moment such an install comes up (`... no longer configured; dropping its
scheduled install` in the service log).

## Proxy and TLS interception

Web sources use the agent's own HTTP client as LocalSystem, which does **not** pick up a user's
Internet Options proxy.

- Set `ProxyUrl` (for example `http://proxy.contoso.com:8080`) for the agent's own downloads.
- `ProxyUrl` does not configure winget. Configure winget's proxy separately (for example
  `winget settings` / `--proxy`, or a machine-wide `WinHTTP` proxy set with
  `netsh winhttp set proxy`), and remember that it must apply to the SYSTEM account.
- A TLS-inspecting proxy must have its root certificate in the machine's Trusted Root store, or
  downloads fail with a trust error.
- If a vendor blocks the default user agent or requires a redirect chain, the error text appears in
  the service log; in that case prefer the winget source for that application.

## The update is found but never installs

- A configured process is running: the update sits in `WaitingForClose`. Check `ProcessNames` and
  whether the process really belongs to that user's session.
- It is deferred: the log and the tray UI show the time it returns.
- It is not mandatory and the user keeps dismissing it - that is by design. Set `Mandatory = 1` with
  a `DeadlineHours` to enforce it.
- Nothing closes at the deadline: `ForceCloseAtDeadline` is 0, or the grace period
  (`CloseGracePeriodMinutes`) has not elapsed yet.
- User-context update with nobody logged on: it waits for a tray agent.
- The installer fails: `LastError` and the exit code are in the log. Try the same installer command
  by hand, silently, as SYSTEM.
- The install times out: raise `InstallTimeoutMinutes` for slow installers.

## winget found no applicable upgrade, but winget upgrade lists the application

Symptom: an update fails with *winget found no applicable upgrade for 'X' (User scope); installed
version is still ...* (or *the installed package type does not match the installer type*), although
`winget upgrade` in a terminal lists that very application. Typical for products the user installed
from the vendor's own download, not through winget: Perplexity Comet, Bing Wallpaper.

Cause: `winget upgrade` compares the manifest's installers with how the product is installed and
refuses when nothing matches. A manifest that declares only a machine-scope installer never matches a
per-user install (Comet); an exe wrapper in the manifest never matches an ARP entry created by a
per-user MSI (Bing Wallpaper). Those filters are built from the installed package's metadata and no
`winget upgrade` argument relaxes them.

What the agent does: it first tries the plain upgrade with the id the scan resolved and then every
winget id configured for the application. Only when all of them refused does it, in the
user's session, retry as `winget install --force`. `--force` makes winget skip the installed-package
lookup, so no such filter is built. The retry is always filtered to installers that need no
administrator rights: `--scope user` first and, when winget has no per-user installer
(`0x8A150010`, *No applicable installer found*), `--installer-type msix`. When neither exists the
update fails with *'X' has no per-user or MSIX installer in winget, only a machine-wide one* and
nothing is started (see [An install asked for administrator rights](#an-install-asked-for-administrator-rights)).
Success is still judged by the version winget reports afterwards. The retry never runs as SYSTEM,
because a user-scope installer started there would land in SYSTEM's profile; a machine-wide install
with the same problem keeps the original message and needs the vendor installer configured as a web
source instead.

## Several installs of one application

Symptom (agents up to 1.1.35): *winget reported success (exit 0x00000000) but 'X' is still at
<older version>, expected <new version>*, or *winget found no applicable upgrade ... installed version
is still ...*, for an application that `winget list --id X` shows more than once - and the same update
comes back after every scan. Typical: the .NET runtimes (`Microsoft.DotNet.DesktopRuntime.8`,
`Microsoft.DotNet.AspNetCore.8`), which keep every patch release registered side by side, so 8.0.30
stays next to 8.0.31; or two builds of PuTTY. The older agents read whichever row winget printed
first, and the scan and the check after the install could each pick a different one.

The rule now, the same for the scan and for every check after an install: winget lists a package
once per installed version, and **the highest installed version is the application's version**. An
update is offered only when a version newer than that highest install is available, and an install
counts as verified as soon as the expected version (or a newer one) is among the installs, however many
older releases stay registered. winget itself keeps offering the upgrade for the older row (8.0.30 ->
8.0.31 while 8.0.31 is installed too); the agent ignores that offer, so there is no re-install loop.
The log shows it at Debug: *winget lists 2 installs of 'X' ... the highest, ..., counts*. The registry
inventory (web sources, icons) already used the highest matching `DisplayVersion`.

Consequence: an older copy that stays registered is never updated or reported on its own. If it
matters (an old PuTTY that users still start), remove it:
`winget uninstall --id X --exact --version <old version>`.

## winget says the install technology is different

Symptom: the update fails and winget's output contains

> A newer version was found, but the install technology is different from the current version
> installed. Please uninstall the package and install the newer version.

(exit code `0x8A15008E`). Oh My Posh (`JanDeDobbeleer.OhMyPosh`) is the typical case: the installed
build came from the old Inno Setup exe while the current manifest ships an MSIX package.

Cause: the product on the device was installed with a different technology than the one the manifest
now uses (exe to MSIX, MSI to exe, ...). winget will not upgrade across that line, and no argument
changes it - `winget install --force` would only put the new package next to the old one.

Fix: set `WingetReplaceOnMismatch = 1` (DWORD) on that application, in the registry or in the
application editor ("Replace mismatched installs"). The agent then does what winget's message asks:
`winget uninstall --id <id> --exact` for the current package, followed by `winget install --id <id>
--exact` for the new one, both silent and in the same context as the upgrade. Success is judged only
by the version winget reports afterwards. If several versions of the package are registered and winget
refuses to choose between them (`0x8A150016`, *Multiple versions of this package are installed*), the
agent retries the uninstall once with `--all-versions` so that every registered version is removed.
Because winget reports such a multi-uninstall as failed (`0x8A150066`, *multiple uninstall failed*) when
even one registration resists, the agent judges the removal by what winget lists afterwards - the
package gone, or a different version than the one being replaced - and goes on to the install with a
warning in the log rather than stopping on the exit code.

If winget still lists the old version after that, the agent looks for the usual reason it cannot be
removed: a stale Windows Installer registration, an Uninstall key whose product Windows Installer no
longer has, so its `MsiExec.exe /I{GUID}` uninstall string answers 1605 (*unknown product*) for ever
and winget reports `0x8A150066`. Only an entry in the scope the take-over runs in (HKLM for the
service, the user's own hive for the tray) whose display name is the one winget lists for the id (or
matches `DetectDisplayNameRegex`), that carries an MSI product code, and whose product
`MsiQueryProductState` reports as *not installed*, qualifies; the agent then deletes that Uninstall key
and asks winget again, and continues with the install when the package is gone from the listing. Every
deleted key is logged at Warning with its path, display name, version, product code and the Windows
Installer state that justified it; when nothing qualifies, the failure says *no stale Windows Installer
registration found for it*.

Caveat: it is off by default because the uninstall comes first. Between the two steps the application
is not installed, and if the install then fails the device is left without it until the next scan
repairs it; the error says so explicitly (*The previous install was removed; installing ... failed
... The application may now be missing on this device.*). The uninstall never passes `--purge`, so
winget leaves the user's data alone, but a product that keeps its settings inside its install
directory can still lose them.

## An install asked for administrator rights

The guarantee: nothing the tray agent starts in a user's session asks for administrator rights.
Machine-wide installs are the service's job (LocalSystem, session 0, never a prompt).

- **The id winget itself names, resolved by the app's identity rule.** The scan does not need a
  curated list of alternative ids. The winget id is the id of the row in winget's own listing whose
  name passes the application's identity rule (`DetectDisplayNameRegex`, or the `DisplayName` as a
  whole-word prefix when no regex is set; winget has no publisher column, so `DetectPublisherRegex`
  plays no part here). Configured ids are hints that take precedence, in this order:
  1. a configured id that `winget list --id <id> --exact` (or the full per-scope listing) finds;
  2. otherwise the row of the full per-scope `winget list` whose name passes the rule - the log says
     *resolved winget id '...' from winget's ... listing by name* (Adobe Reader 32-bit resolves to
     `Adobe.Acrobat.Reader.32-bit` for a policy naming `Adobe.Acrobat.Reader.64-bit`);
  3. then `winget upgrade` (the listing, fetched once per scan and scope), which names the id that can
     actually upgrade the product: a configured id in that listing wins, otherwise a row whose name
     passes the rule and that describes the same install (same id, installed version or name).
     `winget list` shows an installed product under every manifest that matches it, so this is how the
     Firefox MSIX build ends up under `Mozilla.Firefox.MSIX` and a per-user Chrome under
     `Google.Chrome.EXE` (the only Chrome package with a user-scope installer).

  Rows with a pseudo id (`MSIX\...`, `ARP\...`), a truncated id (`…`) or no source are never used.
  When several rows pass the rule, the id sharing the most leading dot-separated segments with the
  first configured id wins, then the highest installed version, then the id in alphabetical order.
  When the upgrade listing names nothing usable, the scan keeps the `winget list` match, so products
  that `winget upgrade` refuses (Perplexity Comet, Bing Wallpaper) are still found. The identity rule
  therefore has to be precise: `^Google Chrome$` does not pick up Chrome Beta, `^Google Chrome` would.
- **The resolved id first, then the configured ids, before any fallback.** The install tries the id
  the scan resolved, then each configured id, with the plain `winget upgrade --scope user`. An id
  winget does not list as installed at all (`0x8A150014`) is skipped: it is a hint for another build
  of the product. The fallbacks (`winget install --force`, the `WingetReplaceOnMismatch` take-over)
  only run once every remaining id refused with *no applicable upgrade* (`0x8A15002B`) or *install
  technology is different* (`0x8A15008E`).
- **Never a machine-wide installer per user.** The fallbacks' `winget install` runs with
  `--scope user`, then with `--installer-type msix` (MSIX installs are per user and never elevate),
  never without a filter. When winget has neither (`0x8A150010`), the update fails without running
  anything. The take-over checks this with `winget show` before it uninstalls, so it never removes
  an application it could not put back.
- **Never a portable copy instead of an update.** Before the `--scope user` attempt the agent asks
  `winget show --scope user` which installer that selects. When it is a portable package (Notepad++
  and VLC only offer a portable zip in user scope) the attempt is skipped and only the MSIX attempt
  remains, because a portable install would put a second copy next to the real one instead of
  updating it. The take-over's pre-check treats a portable user-scope installer as no per-user
  installer at all.
- **RunAsInvoker for everything the tray starts.** winget, and the installers it or a web source
  starts, run with `__COMPAT_LAYER=RunAsInvoker`: Windows does not raise a UAC prompt for an
  installer whose manifest asks for administrator rights; it runs with the user's rights and succeeds
  per user or fails. This does not stop a program that explicitly asks for elevation, which is why
  the filter above comes first.

What the user sees instead of a prompt: a failed update with the reason, for example *'Mozilla.Firefox'
has no per-user or MSIX installer in winget, only a machine-wide one; the agent does not start
installers that need administrator rights in a user's session.*

Example: a device with both Firefox builds - the MSIX package (listed by winget under both
`Mozilla.Firefox` and `Mozilla.Firefox.MSIX`) and the classic build in `C:\Program Files\Mozilla
Firefox`. The scan matched the MSIX build under `Mozilla.Firefox`; `winget upgrade --id
Mozilla.Firefox --scope user` refused (`0x8A15002B`), and older agents then ran an unscoped
`winget install --force --id Mozilla.Firefox`, whose only installer is the machine-wide nullsoft
setup: a UAC prompt. `winget upgrade --scope user` lists exactly one Firefox row, under
`Mozilla.Firefox.MSIX`, so the scan now picks that id - by id when it is configured, by name
(*Mozilla Firefox* passes `^Mozilla Firefox`) when it is not - and its MSIX installer upgrades the
build silently; if the listing is unavailable, the install still tries any configured
`Mozilla.Firefox.MSIX` before any fallback.

## SYSTEM is refused its own files, or machine-wide installs ask for UAC

Symptoms seen on 1.1.2 to 1.1.7, all at once on the same device: `StateStore: Could not save state ...
Access to the path is denied` although the ACL on `state.json` is correct, `winget.exe was not found
(SYSTEM context)` right after a prerequisite check had found it, `Could not close ... Access is
denied` when the service tried to end a process, and installers the SYSTEM service started that ran in
the user's session and showed a UAC prompt (winget printed "The installer will request to run as
administrator. Expect a prompt."; the service log shows `Started "winget.exe" ... in session 1`).

Cause: identifying a pipe client with `NamedPipeServerStream.RunAsClient` leaked the client's
impersonation into the service's async flow on .NET 10. The calling thread was reverted, but every
continuation after the next await - and every task started from them - ran as the tray's user. Fixed in
1.1.8: the client is identified from its process token without impersonating, and a regression test
covers the flow. `ImpersonationGuard` still logs and reverts should any thread ever be found
impersonating (`Thread N was still impersonating ...` in the service log); on 1.1.8 that line should
never appear.

## The close-apps dialog keeps coming back
Since 1.1.6 a process running in session 0 (as SYSTEM, a scheduled task, an RMM agent's script) is not
treated as blocking at all and is left to the installer; only processes in interactive user sessions
are prompted for or closed. If a device on 1.1.4 or 1.1.5 reported "Could not close pwsh (pid …,
session 0, NT AUTHORITY\SYSTEM …): Access is denied", that is this case.

"Close apps and update" closes what the **tray agent** can reach, and that is only windowed processes
in the user's own session at the user's own integrity level. Three kinds of process are out of its
reach:

- **Console processes** - `pwsh` in Windows Terminal, `node`, `python`. They have no main window, so
  `WM_CLOSE` does nothing at all.
- **Elevated processes** - an administrative PowerShell. A medium-integrity agent may not touch them.
- **Processes in another session** - another signed-in user, a scheduled task, or anything in session 0.

Before 1.2 the service re-checked the blocking processes just before installing, found one of those,
and prompted again - so pressing the button simply brought the dialog straight back, forever.

Now the tray kills what it can in its own session and then hands the rest to the service, which runs
as LocalSystem and can end any of them. In the service log this looks like:

```text
PowerShell 7: closing pwsh (pid 4242, session 3, H-SURFACELAP5\bob) before the install (the user chose Close apps and update)
Terminated pwsh (pid 4242, session 3, H-SURFACELAP5\bob) to install PowerShell 7
```

The dialog itself now marks those processes - "pwsh — elevated", "pwsh — another session
(H-SURFACELAP5\bob)" - and says that the service closes them.

If the dialog still reappears:

- Check the **tray version**: an agent older than 1.2 does not send `CloseBlockingProcesses`, so the
  service keeps the old prompt-and-wait behaviour. Update the agent on that device.
- If the install fails instead with `Could not close pwsh (pid …, session …, …)`, the process
  resisted termination even as SYSTEM. Since 1.2 the line says **why** and **which** executable it was:

  ```text
  Could not close pwsh (pid 9, session 0, NT AUTHORITY\SYSTEM, elevated, C:\Program Files\PowerShell\7\pwsh.exe): Win32Exception: Access is denied (Win32 error 5 / 0x00000005)
  ```

  The Win32 code is the part to act on (5 = access denied, typically a protected process; 6 = the
  handle went away). The path tells you whose `pwsh` it is - a scheduled task, an RMM agent, or the
  user's own shell. End it by hand or reboot; the update is retried after the next scan.
- If the line instead reads `pwsh (pid 4711, …, C:\rmm\pwsh.exe): restarted (pid 4711) started again`,
  the kill worked and something restarted the process immediately. The install fails on purpose rather
  than running into files that are still held: find what respawns it (a service, a scheduled task, an
  RMM agent - the path names it), stop that, and let the next scan retry.
- The service only closes processes for a user request that is less than an hour old, or when
  `ForceCloseAtDeadline = 1` and the grace period has run out. Anything else keeps asking the user,
  by design.

## An update is stuck on "Waiting for you to close"

A card that sits on `Waiting for you to close: pwsh` and never moves used to mean the close-apps dialog
had been dismissed with the window's **X**: the service never heard an answer, the update stayed in
`WaitingForClose`, and in `Quiet` mode nothing prompted again until the next notification interval.

Since 1.2:

- Closing the dialog with **X** is treated as **Not now**, so the service knows the user declined.
- The card itself carries a **Close apps and update** button while an update is waiting, and its status
  says so. Pressing it reopens the dialog locally - no waiting for the service to prompt again.

If a card is still stuck, check the tray log (`%LOCALAPPDATA%\Arkimentum\AppMonitor\Logs\`):

```powershell
Select-String -Path "$env:LOCALAPPDATA\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Tray_*.log" `
  -Pattern 'close-apps dialog'
```

`Could not build the close-apps dialog for …` or `Applying the update to the close-apps dialog … failed`
is the agent telling you the dialog itself threw; the line carries the update key and the full blocking
detail, and the exception follows it. That is also what a **blank white dialog** used to look like with
nothing in the log at all: the tray's `DispatcherUnhandledException` handler marks such exceptions
handled, so a half-built window simply stayed on screen. Restarting the tray agent (sign out and in, or
kill `Arkimentum.AppMonitor.Tray.exe` - the service starts it again) clears the window; the log line is
what to report.

## The tray lists applications that are not installed here

`Details → Monitored applications` shows the applications the **last check found on this device**, not
the whole configuration: machine-wide installs for everyone, per-user installs only for the signed-in
user. When the configuration covers more than that, a second line says so - "3 of 12 monitored
applications apply to this device".

If the list still looks wrong:

- **Everything is listed.** No scan has produced results yet (fresh install, or `state.json` was
  deleted), so the agent falls back to showing every enabled application. Press **Check now** and look
  again.
- **Something installed is missing.** The check for it failed, so the previous answer stands. Look for
  `Check failed for <app>` in the service log.
- **A per-user application is missing.** Per-user installs are only checked for users whose tray agent
  is connected; sign in, let the tray connect and run a scan.
- **The agent or the service is older than 1.2.** An older service sends the whole enabled set, and an
  older tray ignores the new count - in both cases the panel reads exactly as it did before.
- The **admin console** deliberately keeps showing the full configured count: it is a view of the
  policy, not of one device.

## Configuration changes have no effect

Ask the agent what it actually sees first - it prints every value with the layer it came from:

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --show-config
```

Then work through:

- Values written to the wrong view: a 32-bit PowerShell or a 32-bit installer writes to
  `WOW6432Node`. Use a 64-bit process.
- Group Policy wins: a value in `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` overrides the same value
  in `HKLM\SOFTWARE\Arkimentum\AppMonitor`. Compare both keys.
- The value is out of range: it is clamped, not rejected (for example `ScanIntervalMinutes = 1`
  becomes 5).
- The application was skipped: a winget application without `WingetId`, or a web application without
  `VersionUrl`/`DownloadUrl`, is dropped with a warning in the log.
- The change is simply not picked up yet - restart the service to apply it immediately:
  `Restart-Service ArkimentumAppMonitor`.
- The catalog is off (`UseCatalog = 0`) and the values you relied on came from it.

## Admin console

Log: `%ProgramData%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Admin_yyyyMMdd.log` (the same
folder as the service log). Headless runs write there too, so start there when `--export` or
`--import` returns exit code 1.

```powershell
Get-Content "$env:ProgramData\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Admin_$(Get-Date -Format yyyyMMdd).log" -Tail 100
```

### The console closes immediately, or nothing happens when I start it

The executable is `asInvoker` and elevates itself: it relaunches with the `runas` verb and the first
process exits. If the UAC prompt is **declined** (or the user is not an administrator and cannot
supply credentials), the relaunch fails, a message says that administrative rights are required, and
the process exits with code **1**. Nothing is written.

- Start it from an account that is a local administrator, or supply administrator credentials in the
  prompt.
- Check `%ProgramData%\...\Arkimentum.AppMonitor.Admin_yyyyMMdd.log` for the "elevation declined"
  line - a genuine crash looks different.
- UAC disabled through "Never notify" plus an unelevated session, or a policy that blocks
  `ShellExecute` with `runas` (`ConsentPromptBehaviorAdmin = 0` with `EnableLUA = 0`, kiosk or LOB
  lockdowns), can make the relaunch fail. Start the executable from an already-elevated prompt
  instead.
- From a script or an RMM, run it in an already-elevated context; there is no `--elevate` or
  credentials switch.

### Values are greyed out: "Managed by Group Policy / Intune"

That value exists under `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor`, which wins over the preference
key, so the console refuses to edit it (a local write would simply be ignored by the agent, and
overwritten at the next policy refresh).

```powershell
# what policy actually sets
reg query 'HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor' /s
gpresult /h "$env:TEMP\gp.html"    # which GPO it came from
```

Change it where it is set - the GPO, or the Intune configuration profile or platform script that
writes the Policies key. If the key was written by a script rather than by real policy (an export run
with `--policy`), remove those values from the Policies key to hand control back to the console.

### Testing without administrative rights

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Admin\Arkimentum.AppMonitor.Admin.exe" --user-config
```

Reads and writes `HKCU\SOFTWARE\Arkimentum\AppMonitor` instead of HKLM, does not elevate, and disables
service control (Start/Stop/Restart and "Scan now" are unavailable, because the service reads HKLM and
runs as LocalSystem). Pair it with `Arkimentum.AppMonitor.Service.exe --user-config --console` to
exercise the whole pipeline unprivileged. Lab use only - nothing the console writes in this mode
affects the installed agent.

### Export or import fails

| Symptom | Cause / fix |
| --- | --- |
| `--export` exits 1, log says access denied | The target folder is not writable, or the path is on a share the elevated token cannot reach (mapped drives are per-logon-session: use a UNC path). |
| `--import` rejects the file: "Unsupported settings document schema" | The profile is not `arkimentum-appmonitor-settings/1` - it was produced by a different (newer) version. Re-export it from a matching build. |
| `--import` reports unknown value names or out-of-range numbers | The profile is validated as a whole before anything is written, so nothing changed. Fix the named entries, or re-export from a machine running this version. |
| Import succeeded but a setting did not change | It is policy-managed; the preference layer was updated but the Policies key still wins. Check `--show-config` on the service. |
| Import removed applications you wanted to keep | **Replace** is the default: `Apps\<AppId>` subkeys that are not in the profile are deleted. Use `--merge` (or *Merge* in the dialog). See the Merge/Replace table in [`AdminConsole.md`](AdminConsole.md#merge-vs-replace-precisely). |
| The `.reg` file imported but the agent ignores the values | It was imported by a 32-bit process, so everything landed in `WOW6432Node`. Re-import from a 64-bit prompt, or use `reg import <file> /reg:64`. |
| "Test detection" works in the console but the service never finds the update | The console checks as the signed-in administrator; the service checks as LocalSystem, where winget may not be provisioned - see [winget is not found under SYSTEM](#winget-is-not-found-under-system). Per-user applications are checked by the tray agent in the user's own session. |

### Changes in the console have no effect on the service

The service re-reads the registry on every policy tick (`PolicyTickSeconds`, default 60 s), so give it
a minute before concluding anything - a restart is not needed. If it still disagrees, ask the service
what it sees: `Arkimentum.AppMonitor.Service.exe --show-config` prints every value with the layer it
came from.

## The cloud service

Only relevant when `CloudServerUrl` is configured. Full background: [`Cloud.md`](Cloud.md).

Start with the status file and the switches - they answer most questions without reading a log:

```powershell
Get-Content "$env:ProgramData\Arkimentum\AppMonitor\cloud-status.json" | ConvertFrom-Json | Format-List

$svc = "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe"
& $svc --cloud-status     # enrolled? device id, last sync, applied config version, last error
& $svc --cloud-enroll     # force an enrolment now and print what the server said
& $svc --report-now       # send a report immediately
& $svc --show-config      # every value with the layer it came from, Cloud included
```

### The device never shows up in the console

- All three values must be present, and in the **64-bit** view. A 32-bit script writes them to
  `WOW6432Node`, where the agent does not look:
  `Get-ItemProperty 'HKLM:\SOFTWARE\Arkimentum\AppMonitor' | Select-Object Cloud*`
- `CloudServerUrl` must be `https` and absolute - the client refuses anything else (loopback excepted).
  No trailing path.
- `CloudOrganizationId` must parse as a GUID.
- Run `--cloud-enroll` and read the error. An invalid-key error usually means the enrollment key was
  rotated after this device was provisioned; devices that already enrolled are unaffected by a
  rotation, only new ones.
- Outbound HTTPS from the **SYSTEM** account to the API host, through `ProxyUrl` if one is set.

### Enrolled, but every call fails with 401 or 403

The device key was revoked, or the device record was deleted in the console. The agent recovers on its
own - the log says *the cloud API rejected the device credential … the device will re-enroll* and it
enrols again on the next attempt, re-attaching to its existing record through `MachineGuid` rather than
creating a duplicate. To force it now:

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --cloud-enroll
```

Only delete `%ProgramData%\Arkimentum\AppMonitor\device.credential` first if the file itself is
unreadable.

### `Device credential at ... could not be read; the device will re-enroll`

DPAPI could not unprotect the file - usually because it was copied from another machine (in a disk
image, for example). Harmless: delete it and let the device enrol again. Never include
`device.credential` in a reference image.

### A published configuration has no effect

In this order:

1. `cloud-status.json` - did the device fetch the version the console shows? It syncs at most every
   `CloudSyncIntervalMinutes`, then applies at the next `PolicyTickSeconds`.
2. `CloudConfigEnabled` - 0 means the device enrols and reports but configures itself locally.
3. `--show-config` - if the value's layer says `Policy`, Group Policy is out-ranking the cloud. That
   is by design; change it in the GPO or unassign it.
4. Is the value one of `CloudServerUrl`, `CloudOrganizationId`, `CloudEnrollmentKey`? Those are always
   dropped from the cloud layer, on purpose.

Note that `LogLevel` itself can come from the organization configuration: if setting it locally seems
to be ignored, the cloud is winning.

### Configuration arrives, but nothing is ever reported

`CloudReportingEnabled = 0`. That is the whole mechanism - the device still fetches configuration and
still runs queued commands, it just sends nothing about itself.

### A queued command never runs

Commands are collected by the device, not pushed to it: expect up to one `CloudSyncIntervalMinutes`,
and nothing at all while the device is offline. **Check now** in the tray pulls the configuration and
its commands immediately, so it is the quickest way to make a device act on what was just published. The console shows a command as pending until the device
acknowledges it in its next report.

### The device is offline for a long time

Nothing breaks. The organization configuration is cached in
`%ProgramData%\Arkimentum\AppMonitor\cloud-config.json` and keeps being used; scans, deadlines,
deferrals and installs carry on; events accumulate and go out with the first report that succeeds. A
failed sync is a warning in the log and a retry on the next interval.

If `cloud-config.json` is corrupt the agent logs
`Cached organization configuration at ... is unreadable and will be ignored` and falls back to the
registry layers. Delete the file and let the next sync rewrite it.

## Agent self-update

Only relevant when `AgentAutoUpdate` is on (the default) and a feed is reachable. Mechanics:
[`SelfUpdate.md`](SelfUpdate.md).

```powershell
$svc = "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe"
& $svc --check-update     # 0 = nothing to do, 2 = an update is available, 1 = the feed could not be read
& $svc --update-now       # 0 = installer started or nothing to do, 1 = failed
& $svc --version
```

The decision is printed as one of `UpToDate`, `ChannelMismatch`, `PinnedByTargetVersion`, `NoManifest`,
`Disabled`, `Blocked`, `UpdateAvailable`, `Failed` or `Launched`, with a reason.

### It never updates

`--check-update` names the reason; the candidates are:

- `AgentAutoUpdate = 0` - deliberate, typically because Intune or an RMM owns the binaries.
- No `AgentUpdateFeedUrl` **and** no `CloudServerUrl`: there is no feed to read.
- The manifest's `channel` does not match `AgentUpdateChannel` (`stable` vs `preview`).
- `AgentTargetVersion` is a ceiling and the published version is above it (`PinnedByTargetVersion`).
  Self-update never downgrades either, so a device on a higher version is left alone.
- An application install is running, or a previous agent update started less than 30 minutes ago
  (`Blocked`).
- The feed host is unreachable from the SYSTEM account. With `AgentUpdateFeedUrl` pointing at GitHub
  that means `api.github.com`, `github.com` and `objects.githubusercontent.com`; leave the value empty
  to use the cloud API's mirror instead on locked-down networks.
- The feed URL is a GitHub **HTML page** (`github.com/<owner>/<repo>/releases/latest`) rather than the
  API URL (`api.github.com/repos/<owner>/<repo>/releases/latest`), or the repository is private - no
  token is ever sent, so private repositories must be mirrored.

### It downloads but fails verification

The SHA-256 of the downloaded package did not match the manifest. The agent logs both hashes and
installs nothing - that is the mechanism working. Causes: a truncated download, a proxy that rewrites
or inspects the body, or a package that is genuinely not the one that was published. Re-run
`--update-now`; if it repeats, fix the release.

### It installed and now the service will not start

The self-updater runs the installer from the extracted package:
`Install-ArkimentumAppMonitor.ps1 -SourceRoot <temp> -NoSampleApps -SkipPrerequisites -Force`, detached
as SYSTEM. Check, in order:

```powershell
Get-Service ArkimentumAppMonitor | Format-List *
sc.exe qc ArkimentumAppMonitor     # binPath must point into %ProgramFiles%\Arkimentum\AppMonitor\Service
$upd = "$env:ProgramData\Arkimentum\AppMonitor\AgentUpdates"
Get-ChildItem $upd -Recurse -Depth 1
Get-Content (Join-Path $upd '<version>\install.log') -Tail 100   # the installer's own output
Get-Content "$env:ProgramData\Arkimentum\AppMonitor\update-pending.json"
```

`install.log` is where the failure will be: the self-updater redirects the installer's output there.
`update-pending.json` records the attempt, and the next service start turns it into either an *Agent
updated* log line or a failed-update warning. The downloaded package and the extracted payload are
kept under `AgentUpdates\<version>\` (the two most recent versions), so you can run the installer by
hand from there - add `-LogFile` to capture a fresh transcript. Configuration, `state.json`,
`device.credential` and `cloud-config.json` all live outside the install folder and are never touched
by an upgrade.

### An update keeps reinstalling itself

Two mechanisms are replacing the same files - typically self-update and an Intune Win32 app deployment
of the same product. Set `AgentAutoUpdate = 0` (locally, from the admin console, or as a policy
registry value) and let the deployment tool own the binaries, or stop deploying it and let the agent
update itself.

## The service does not start

```powershell
Get-Service ArkimentumAppMonitor | Format-List *
Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager' } -MaxEvents 20 |
    Where-Object Message -match 'Arkimentum'
```

- Wrong binary path after a manual move: `sc.exe qc ArkimentumAppMonitor`.
- Framework-dependent build without the .NET Desktop Runtime on the machine - either install the
  runtime or deploy the self-contained build (`Build-Release.ps1 -SelfContained`).
- The log folder is not writable by LocalSystem (only relevant with a custom `LogDirectory`).
- Run `Arkimentum.AppMonitor.Service.exe --console` elevated: startup errors that the SCM only reports as
  "the service did not respond" are printed in full there.

## Collecting a support bundle

```powershell
$dest = "$env:TEMP\ArkimentumAppMonitor-support"
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item "$env:ProgramData\Arkimentum\AppMonitor\Logs\*.log" $dest -ErrorAction SilentlyContinue
Copy-Item "$env:ProgramData\Arkimentum\AppMonitor\state.json" $dest -ErrorAction SilentlyContinue
Copy-Item "$env:LOCALAPPDATA\Arkimentum\AppMonitor\Logs\*.log" $dest -ErrorAction SilentlyContinue
# cloud link and the cached organization configuration (device.credential is deliberately NOT copied:
# it is the device's secret and is useless off this machine anyway - DPAPI is machine-bound)
Copy-Item "$env:ProgramData\Arkimentum\AppMonitor\cloud-status.json" $dest -ErrorAction SilentlyContinue
Copy-Item "$env:ProgramData\Arkimentum\AppMonitor\cloud-config.json" $dest -ErrorAction SilentlyContinue
# a machine-readable snapshot of the configuration (same content as the admin console's JSON export)
& "$env:ProgramFiles\Arkimentum\AppMonitor\Admin\Arkimentum.AppMonitor.Admin.exe" --export "$dest\settings.json"
reg export 'HKLM\SOFTWARE\Arkimentum\AppMonitor' "$dest\preferences.reg" /y
reg export 'HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor' "$dest\policy.reg" /y 2>$null
Compress-Archive -Path "$dest\*" -DestinationPath "$env:TEMP\ArkimentumAppMonitor-support.zip" -Force
```

The logs contain application names, versions, user names and SIDs; treat the bundle accordingly.

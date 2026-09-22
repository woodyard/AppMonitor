# Registry reference

Arkimentum AppMonitor is configured **from the registry**, and optionally from an organization
configuration delivered by the cloud service. There is no configuration file and no command line
configuration. Everything below is read by
`src\Arkimentum.AppMonitor.Core\Configuration\RegistryConfigurationReader.cs`.

When the cloud service is in use, only three values - `CloudServerUrl`, `CloudOrganizationId` and
`CloudEnrollmentKey` - have to be provisioned per device; everything else can be published once for the
organization. Intune / Group Policy registry values, `.reg` files and the admin console keep working
exactly as before and remain the
right answer for a stand-alone fleet. See [`Cloud.md`](Cloud.md).

You do not have to write these values by hand. The **admin console**
(`%ProgramFiles%\Arkimentum\AppMonitor\Admin\Arkimentum.AppMonitor.Admin.exe`, Start Menu → Arkimentum
→ *Arkimentum AppMonitor Admin*) is the graphical front end for exactly this reference: it edits every
global and per-application value in the preference key with the correct registry type and range,
marks values that Group Policy manages as locked, and exports the result as a JSON profile, a `.reg`
file or an idempotent PowerShell script for Intune, Group Policy or an RMM - also headless, with
`--export` and `--import`. This page remains the authority on what each value means and on the
precedence rules below. See [AdminConsole.md](AdminConsole.md).

## Layers and precedence

| # | Layer | Key | Written by |
| --- | --- | --- | --- |
| 1 (wins) | Policy | `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` | Intune or Group Policy registry values |
| 2 | Organization configuration | `%ProgramData%\Arkimentum\AppMonitor\cloud-config.json` | published in the cloud, cached on the device |
| 3 | Preference | `HKLM\SOFTWARE\Arkimentum\AppMonitor` | the installer, admins, scripts |
| 4 | Catalog | `catalog.json` (per-app identity/source/detection data only) | shipped with the product / `CatalogPath` |
| 5 | Built-in default | - | the code |

Layer 2 exists only when the cloud service is configured; without it the order is the familiar
policy → preference → catalog → default.

Per application the order is finer grained, because the registry layers have two shapes each:

1. `Policies\...\Apps\<AppId>` (policy subkey)
2. `Policies\...\AppList` value named `<AppId>` (policy list entry)
3. the application in the organization configuration
4. `Arkimentum\AppMonitor\Apps\<AppId>` (preference subkey)
5. `Arkimentum\AppMonitor\AppList` value named `<AppId>` (preference list entry)
6. catalog entry with the same `AppId`
7. built-in default

Precedence is **per value**, not per key: an application can get its deadline from Group Policy, its
process names from the catalog and everything else from the organization configuration. The service
logs the layer each value came from (`Policy`, `PolicyAppList`, `Cloud`, `Preference`,
`PreferenceAppList`, `Default`).

Two things the cloud layer deliberately cannot do:

- **It cannot enable itself.** Whether the organization configuration applies at all is read from
  `CloudConfigEnabled` in the registry layers only, so an administrator can always switch it off on a
  device.
- **It cannot set the connection values.** `CloudServerUrl`, `CloudOrganizationId` and
  `CloudEnrollmentKey` are dropped from the cloud layer if the server sends them - they are how the
  device reaches the server in the first place.

Worked examples of all of this: [`Cloud.md`](Cloud.md#precedence).

Both keys live in the 64-bit registry view. Always write them from a 64-bit process; a 32-bit host is
redirected to `WOW6432Node`, where the agent does not look.

For testing without administrative rights, `Arkimentum.AppMonitor.Service.exe --user-config` reads the
same two paths under **HKCU** instead (and keeps logs and state under `%LOCALAPPDATA%`). That switch
is for lab use only; production configuration always lives in HKLM.

## How values are read

The reader is deliberately forgiving about types, so the same configuration can come from regedit, a
`.reg` file, Intune or Group Policy:

| Kind | Accepted types | Notes |
| --- | --- | --- |
| Number | `REG_DWORD`, `REG_QWORD`, numeric `REG_SZ` | Out-of-range values are **clamped** to the documented range, not rejected. |
| Boolean | `REG_DWORD` 0/1, `REG_SZ` `0`/`1`/`true`/`false` | Any non-zero number is true. |
| String | `REG_SZ`, `REG_EXPAND_SZ`, `REG_MULTI_SZ` (joined with commas), `REG_DWORD` | Leading/trailing whitespace is trimmed. |
| Number list | `REG_SZ` separated by `,` `;` or space, `REG_MULTI_SZ` | Non-numeric and non-positive entries are dropped; the result is de-duplicated and sorted. |
| String list | `REG_SZ` separated by `,` or `;`, `REG_MULTI_SZ` | A trailing `.exe` is stripped; the result is de-duplicated (case-insensitive). |

Only `LogDirectory`, `StateDirectory` and `CatalogPath` expand environment variables
(`%ProgramData%`, `%ProgramFiles%`, ...). Other string values are used verbatim.

An empty string means "not configured": the next layer is used.

## Global values

All of these live directly under `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` or
`HKLM\SOFTWARE\Arkimentum\AppMonitor`. All are optional.

| Value | Type | Default | Range | Meaning |
| --- | --- | --- | --- | --- |
| `ScanIntervalMinutes` | DWORD | 240 | 5-10080 | Minutes between update scans. |
| `NotificationIntervalMinutes` | DWORD | 240 | 1-10080 | Minutes before the same pending update is announced again. Only used when `NotificationMode` is `Reminders`. |
| `StartupDelaySeconds` | DWORD | 120 | 0-3600 | Delay after service start before the first scan. |
| `ScanOnStartup` | DWORD | 1 | 0/1 | Scan once when the service starts, in addition to the interval. |
| `WingetEnabled` | DWORD | 1 | 0/1 | Process applications with `Source=winget`. |
| `WebSourcesEnabled` | DWORD | 1 | 0/1 | Process applications with `Source=web`. |
| `LaunchTrayAgent` | DWORD | 1 | 0/1 | Service launches `Arkimentum.AppMonitor.Tray.exe` into interactive sessions. |
| `NotificationsEnabled` | DWORD | 1 | 0/1 | Tray agent shows toast notifications. |
| `NotificationMode` | SZ | `Quiet` | `Quiet`, `Reminders` | `Quiet` announces an update once and afterwards only interrupts when the user has to act (deadline approaching, applications must be closed, install failed); no "Installing ..." toast unless `NotifyInstalling` asks for one. `Reminders` repeats every `NotificationIntervalMinutes`. |
| `ShowInstalledNotifications` | DWORD | 0 | 0/1 | Notify the user after a successful install. Changed in 1.2: this was on by default up to 1.1.1. |
| `LogLevel` | SZ | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error` | Minimum level written to the log files. |
| `LogDirectory` | SZ / EXPAND_SZ | `%ProgramData%\Arkimentum\AppMonitor\Logs` | full path | Service log folder. Must be writable by LocalSystem. Does not affect the tray agent. |
| `LogRetentionDays` | DWORD | 30 | 1-3650 | Log files older than this are deleted. |
| `MaxLogFileSizeMB` | DWORD | 10 | 1-1024 | Roll the log file at this size (`..._1.log`, `..._2.log`, ...). |
| `StateDirectory` | SZ / EXPAND_SZ | `%ProgramData%\Arkimentum\AppMonitor` | full path | Holds `state.json` and the `Downloads` sub-folder. |
| `CatalogPath` | SZ / EXPAND_SZ | *(empty)* | full path | Alternative catalog file. Empty = `catalog.json` next to the service executable. |
| `UseCatalog` | DWORD | 1 | 0/1 | Use the catalog as a fallback layer for per-app values. |
| `EnableAllCatalogApps` | DWORD | 0 | 0/1 | Monitor every application in the catalog, not only the configured ones. |
| `InstallTimeoutMinutes` | DWORD | 30 | 1-600 | Maximum runtime of a single installer. |
| `CheckTimeoutMinutes` | DWORD | 3 | 1-60 | Maximum runtime of a single update check (one winget call or one web request). |
| `WingetPath` | SZ / EXPAND_SZ | *(empty)* | full path | Explicit path to `winget.exe`. Empty = auto-detect (including the `WindowsApps` package folder when running as SYSTEM). Mainly a troubleshooting escape hatch. |
| `ProxyUrl` | SZ | *(empty)* | URL | Proxy for web sources. Empty = the SYSTEM account's system proxy. Does not configure winget. |
| `WingetGlobalArgs` | SZ | *(empty)* | text | Extra arguments appended to every winget invocation. |
| `WingetIncludeUnknown` | DWORD | 0 | 0/1 | Treat winget rows with an unknown installed version as updatable. |
| `AutoInstallPrerequisites` | DWORD | 1 | 0/1 | Let the service install or repair winget (the `Microsoft.DesktopAppInstaller` package) on its own when it is missing, broken or too old for the SYSTEM account. |
| `PrerequisiteCheckIntervalHours` | DWORD | 24 | 1-720 | How often the prerequisite check runs after the one at service start. |
| `WingetMinimumVersion` | SZ | `1.6.0` | version | The winget version the service insists on. A lower (or unreadable) version triggers the install/repair when `AutoInstallPrerequisites = 1`. |
| `PolicyTickSeconds` | DWORD | 60 | 10-600 | How often deadlines, deferrals and blocking processes are evaluated. |
| `DefaultMandatory` | DWORD | 0 | 0/1 | Default for `Mandatory`. |
| `DefaultDeadlineHours` | DWORD | 0 | 0-8760 | Default for `DeadlineHours`. 0 = no deadline. |
| `DefaultMaxDeferrals` | DWORD | 3 | 0-1000 | Default for `MaxDeferrals`. 0 = unlimited. |
| `DefaultDeferralOptions` | SZ / MULTI_SZ | `60,240,1440` | minutes | Default deferral choices offered to the user. |
| `DefaultAutoInstall` | DWORD | 0 | 0/1 | Default for `AutoInstall`: start the install on its own when none of the application's processes are running. Whether a toast announces it is decided by `DefaultNotifyInstalling` / `NotifyInstalling`. |
| `DefaultNotifyInstalling` | SZ | `auto` | `auto`, `always`, `never` | Default for `NotifyInstalling`: whether the "Installing ..." toast is shown when an install starts. `auto` follows `NotificationMode` (`Reminders` shows it, `Quiet` does not). An unreadable value falls back to `auto`. |
| `DefaultCloseGracePeriodMinutes` | DWORD | 15 | 0-1440 | Default for `CloseGracePeriodMinutes`. |
| `DefaultForceCloseAtDeadline` | DWORD | 1 | 0/1 | Default for `ForceCloseAtDeadline`. |
| `TrayPath` | SZ | *(empty)* | full path | Explicit path to `Arkimentum.AppMonitor.Tray.exe`. Only needed when the tray agent is not in the service folder or in `..\Tray\`. |

### Organization and updates

These values connect the agent to the cloud service and control how it updates itself. All are
optional; with none of them set the agent is stand-alone and contacts nothing but winget and the
vendor sites it is configured for.

| Value | Type | Default | Range | Meaning |
| --- | --- | --- | --- | --- |
| `CloudServerUrl` | SZ | *(empty)* | https URL | Base URL of the organization's AppMonitor API, e.g. `https://appmonitor-contoso.azurewebsites.net`. Must be `https` (loopback excepted). Empty = stand-alone. |
| `CloudOrganizationId` | SZ | *(empty)* | GUID | The organization id from the console's Enrollment page. |
| `CloudEnrollmentKey` | SZ | *(empty)* | text | The organization enrollment key. Used **once** to obtain a per-device key; never sent again. |
| `CloudSyncIntervalMinutes` | DWORD | 15 | 1-1440 | Minutes between syncs (configuration, report, commands). The server may ask for a longer interval, never a shorter one. |
| `CloudConfigEnabled` | DWORD | 1 | 0/1 | Apply the organization configuration. 0 = enrol and report, but configure from the registry only. Read from the registry layers only. |
| `CloudReportingEnabled` | DWORD | 1 | 0/1 | Send inventory, update state, events and prerequisite status. 0 = fetch configuration only; nothing about the device is sent. |
| `AgentAutoUpdate` | DWORD | 1 | 0/1 | Let the agent install newer releases of itself. Set to 0 when Intune, Configuration Manager or an RMM owns the binaries. |
| `AgentUpdateFeedUrl` | SZ | `https://api.github.com/repos/woodyard/AppMonitor/releases/latest` | https URL or `cloud` | Release feed: a GitHub releases API URL (`.../releases/latest` or `.../releases/tags/v1.2.0`) or a direct `manifest.json` URL. Public repositories only - no token is sent. Empty = the cloud API's mirror, or no self-update when there is no cloud either. |
| `AgentUpdateChannel` | SZ | `stable` | `stable`, `preview` | Only manifests naming this channel are installed. |
| `AgentUpdateCheckIntervalHours` | DWORD | 12 | 1-720 | Hours between release checks. The check is one HTTPS request for the manifest. The scheduler currently caps the wait at 168 hours (7 days), so a larger value behaves as 168. |
| `AgentTargetVersion` | SZ | *(empty)* | version | Ceiling: a published version newer than this is skipped, so the device stays where you pinned it. Empty = the newest release in the channel. Never downgrades. |

The three `Cloud*` connection values are the whole per-device provisioning surface: with them in place
everything else in this reference can be published for the organization instead of written to each
machine. The agent exchanges the enrollment key for a per-device key at its first sync and stores it
DPAPI-protected in `%ProgramData%\Arkimentum\AppMonitor\device.credential` (ACL: SYSTEM and
Administrators only); the organization configuration is cached in the same folder as
`cloud-config.json`, and the link's state in `cloud-status.json`.

Check the state with `Arkimentum.AppMonitor.Service.exe --cloud-status`; force the steps with
`--cloud-enroll`, `--report-now`, `--check-update` and `--update-now`.

Provisioning recipes (Intune platform script, Intune Win32 app, Group Policy, manual), the data flows,
the exact list of fields that leave the device, and offline behaviour: [`Cloud.md`](Cloud.md).
Self-update mechanics: [`SelfUpdate.md`](SelfUpdate.md).

### Prerequisites

`AutoInstallPrerequisites`, `PrerequisiteCheckIntervalHours` and `WingetMinimumVersion` drive the
service's prerequisite manager. winget is the agent's **only** runtime prerequisite - the binaries
themselves are published self-contained - and it is the one component that is regularly missing for
the SYSTEM account, because App Installer is a per-user MSIX.

With `AutoInstallPrerequisites = 1` (the default) the service checks at startup and then every
`PrerequisiteCheckIntervalHours` whether winget is available to SYSTEM and at least
`WingetMinimumVersion`. When it is not, it repairs it without involving the user: first through the
`Microsoft.WinGet.Client` PowerShell module (`Repair-WinGetPackageManager -AllUsers -Latest`,
installed for all users from the PowerShell Gallery), otherwise by downloading the current App
Installer `.msixbundle` and licence from the `microsoft/winget-cli` GitHub release, together with the
VCLibs 14 Desktop and Microsoft.UI.Xaml 2.8 dependencies, and provisioning them with
`Add-AppxProvisionedPackage`. The tray agent then registers the provisioned package for the signed-in
user (`Add-AppxPackage -RegisterByFamilyName`) so per-user winget operations work without
administrative rights.

This needs outbound HTTPS to `github.com` (and `objects.githubusercontent.com`), `aka.ms`,
`nuget.org` and the PowerShell Gallery (`www.powershellgallery.com`,
`psg-prod-eastus.azureedge.net`). On devices that cannot reach them, provision App Installer in the
image and set `AutoInstallPrerequisites = 0`.

Run the check by hand with
`Arkimentum.AppMonitor.Service.exe --prerequisites` (exit code 0 = healthy, 2 = unhealthy, 1 = error);
the admin console shows the same status on its Overview page, with an *Install or repair
prerequisites* button.

`TrayPath` is read by the service's tray launcher, not by the settings reader, and only from
`HKLM\SOFTWARE\Arkimentum\AppMonitor` - **not** from the Policies key. The search order is: this value
(environment variables expanded), then the service's own folder, then `..\Tray\`.

## Per-application values

One subkey per application under `Apps`, for example
`HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome`. The subkey name is the **AppId**: any stable
identifier you choose. Use the id of the matching catalog entry (`chrome`, `7zip`, `vscode`,
`firefox`, `powershell`, ...) and everything you leave out is filled in from the catalog; use your
own id and the application is configured entirely from the registry.

### Identity and source

| Value | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Enabled` | DWORD | 1 | 0 disables the application without deleting the configuration. |
| `DisplayName` | SZ | catalog value, else the AppId | Name shown in notifications and the tray UI. |
| `Source` | SZ | `winget` | `winget`, or `web` (accepted spellings: `web`, `site`, `official`, `url`). |
| `Context` | SZ | `auto` | `auto`, `system` (or `machine`/`device`), `user`. See [Architecture](Architecture.md). |

### winget source

| Value | Type | Default | Meaning |
| --- | --- | --- | --- |
| `WingetId` | SZ | catalog value | winget package id. **Required** for `Source=winget`; the application is skipped with a warning without it. Alternatives may be listed separated by `;` (or `|` inside an `AppList` value), e.g. `Mozilla.Firefox;Mozilla.Firefox.MSIX` for products that exist both as a classic installer and as a Store/MSIX package; the first id that is installed is used. |
| `WingetSource` | SZ | `winget` | winget source name, e.g. `msstore` or a private REST source. |
| `WingetExtraArgs` | SZ | catalog value | Extra arguments for this application only, e.g. `--scope user`. |
| `WingetReplaceOnMismatch` | DWORD | 0 | 1 lets the agent take the application over when `winget upgrade` refuses with *"the install technology is different from the current version installed"* (exit `0x8A15008E`): it runs `winget uninstall` for the package and then `winget install` for the new version. If the package is still listed afterwards because a stale Windows Installer registration survived - an Uninstall key whose product Windows Installer no longer has, so no uninstall can ever remove it - the agent deletes that one key (same scope, matching display name, MSI product code, `MsiQueryProductState` says not installed), logs it at Warning and continues with the install. Off by default, because the application is briefly absent between the two steps. Behaviour, so it is never supplied by the catalog. See [Troubleshooting](Troubleshooting.md). |

### Web source

| Value | Type | Default | Meaning |
| --- | --- | --- | --- |
| `VersionUrl` | SZ | catalog value | Page or API response containing the latest version. **Required** for `Source=web`. |
| `VersionRegex` | SZ | catalog value | Regex with one capture group (or a group named `version`) applied to that response. |
| `DownloadUrl` | SZ | catalog value | Installer URL. **Required** for `Source=web`. Supports `{version}`, `{version_nodots}`, `{version_major}`, `{version_underscore}`. |
| `InstallerType` | SZ | `exe` | `exe`, `msi`, `msix` (`appx` is accepted as a synonym). |
| `InstallerArgs` | SZ | catalog value | Silent-install arguments, e.g. `/S` or `/qn /norestart`. |
| `Sha256` | SZ | catalog value | Expected SHA-256 of the download. |
| `Sha256Url` | SZ | catalog value | URL of a text file containing the SHA-256 of the download. |
| `UserDownloadUrl` | SZ | catalog value | Download URL used instead of `DownloadUrl` for user-context installs (e.g. a "user setup" build). |
| `UserInstallerArgs` | SZ | catalog value | Installer arguments used instead of `InstallerArgs` for user-context installs. |

A web-source application without both `VersionUrl` and `DownloadUrl` (after the catalog layer) is
skipped with a warning.

### Detection

Used to find the installed version, and to resolve `Context=auto`.

| Value | Type | Default | Meaning |
| --- | --- | --- | --- |
| `DetectDisplayNameRegex` | SZ | catalog value | Regex matched against `DisplayName` in the Uninstall keys. Without it, the configured `DisplayName` is matched as a whole-word prefix. |
| `DetectPublisherRegex` | SZ | catalog value | Optional regex matched against `Publisher`. |
| `DetectFilePath` | SZ | catalog value | Optional file whose file/product version is used as the installed version. Environment variables are expanded. |

Regexes are case-insensitive with a 1-second timeout; an invalid pattern is ignored rather than
failing the scan.

### Behaviour

Behaviour is policy, not catalog data: these values come from the registry, and otherwise from the
global `Default...` value. The catalog never supplies them.

| Value | Type | Default | Range | Meaning |
| --- | --- | --- | --- | --- |
| `Mandatory` | DWORD | `DefaultMandatory` | 0/1 | The update is enforced: deferrals are bounded and the deadline applies. |
| `DeadlineHours` | DWORD | `DefaultDeadlineHours` | 0-8760 | Hours after first detection at which a mandatory update is enforced. 0 = no deadline. |
| `MaxDeferrals` | DWORD | `DefaultMaxDeferrals` | 0-1000 | How often the user may postpone. 0 = unlimited. |
| `DeferralOptions` | SZ / MULTI_SZ | `DefaultDeferralOptions` | minutes | Deferral choices offered in the tray agent, e.g. `60,240,1440`. |
| `ProcessNames` | MULTI_SZ / SZ | catalog value | names | Processes that must not run during the install. `.exe` is optional and stripped: `chrome` and `chrome.exe` are identical. |
| `AutoInstall` | DWORD | `DefaultAutoInstall` | 0/1 | Start the install on its own as soon as no listed process is running. Whether a toast announces it is decided by `NotifyInstalling`. |
| `CloseGracePeriodMinutes` | DWORD | `DefaultCloseGracePeriodMinutes` | 0-1440 | Time the user gets to save work after a forced close is announced. |
| `ForceCloseAtDeadline` | DWORD | `DefaultForceCloseAtDeadline` | 0/1 | Terminate the blocking processes once the deadline has passed and the grace period has elapsed. |
| `MinimumVersion` | SZ | catalog value | version | Only report an update when the installed version is below this one. |
| `NotificationIntervalMinutes` | DWORD | *(unset = use the global value)* | minutes | Per-application override of the notification cadence. Not range-clamped. |
| `NotificationMode` | SZ | *(unset = use the global value)* | `Quiet`, `Reminders` | Per-application override of the notification style. An unreadable value falls back to the global mode. |
| `NotifyInstalling` | SZ | `DefaultNotifyInstalling` | `auto`, `always`, `never` | Show the "Installing ..." toast when this application's install starts. `auto` follows the notification style (`Reminders` shows it, `Quiet` does not), `always` and `never` decide it regardless. An unreadable value falls back to the global value. |

## The flat `AppList` format

Group Policy list elements and Intune cannot create nested registry keys, so the same per-application
values can be written as a single string under an `AppList` key:

```
HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor\AppList
    Google.Chrome  (REG_SZ) = DisplayName=Google Chrome;Enabled=1;Source=winget;WingetId=Google.Chrome;Mandatory=1;DeadlineHours=72;MaxDeferrals=3;DeferralOptions=60|240|1440;ProcessNames=chrome;CloseGracePeriodMinutes=15;ForceCloseAtDeadline=1
```

Rules:

- The **value name** is the AppId; the **value data** is `Name=Value;Name=Value;...`.
- Names are exactly the per-application value names above, case-insensitive.
- `;` separates pairs, so lists inside an entry use `|`: `ProcessNames=chrome|chrome_proxy`,
  `DeferralOptions=60|240|1440`. The reader converts `|` to `,` for `ProcessNames` and
  `DeferralOptions` only.
- Pairs without `=`, and empty segments, are ignored.
- An `Apps\<AppId>` subkey in the same layer wins over an `AppList` entry for the same value.

Both shapes can be mixed: a device can get five applications from Group Policy's `AppList` and two
more from a local `Apps` subkey.

## Catalog merge rules

The catalog (`catalog.json` next to `Arkimentum.AppMonitor.Service.exe`, or `CatalogPath`) holds
ready-made application definitions - winget ids, vendor version/download URLs, detection regexes and
process names - so the registry only has to say *which* applications to manage and *how strict* to be.

- Matching is by `AppId`, case-insensitive. The shipped catalog uses short ids (`chrome`, `7zip`,
  `vscode`, `firefox`, `powershell`, ...); use the same id in the registry to inherit its data.
- The catalog is a **fallback layer**: any value present in the policy or preference layer wins.
- It supplies **identity, source and detection data only**: `DisplayName`, `Source`, `Context`,
  `WingetId`, `WingetSource`, `WingetExtraArgs`, `VersionUrl`, `VersionRegex`, `DownloadUrl`,
  `InstallerType`, `InstallerArgs`, `Sha256`, `Sha256Url`, `UserDownloadUrl`, `UserInstallerArgs`,
  the three `Detect*` values, `ProcessNames` and `MinimumVersion`.
- It never supplies **behaviour**: `Mandatory`, `DeadlineHours`, `MaxDeferrals`, `DeferralOptions`,
  `AutoInstall`, `CloseGracePeriodMinutes`, `ForceCloseAtDeadline`, `NotifyInstalling` and `WingetReplaceOnMismatch` always come from the
  application's own registry values, and otherwise from the global `Default...` values
  (`WingetReplaceOnMismatch` has no global counterpart and is simply off unless the application sets it). How strict
  you are with your users is your policy decision, not the catalog author's.
- `UseCatalog=0` switches the layer off entirely. Applications that then lack a `WingetId`, or a
  `VersionUrl`/`DownloadUrl`, are skipped with a warning in the log.
- `EnableAllCatalogApps=1` adds every catalog entry to the monitored set. Explicit configuration
  still wins, so individual applications can be excluded again with `Enabled=0`.

## Examples

### 1. Mandatory winget application with a deadline (`reg add`)

```bat
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v DisplayName /t REG_SZ /d "Google Chrome" /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v Enabled /t REG_DWORD /d 1 /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v Source /t REG_SZ /d winget /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v WingetId /t REG_SZ /d "Google.Chrome" /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v Mandatory /t REG_DWORD /d 1 /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v DeadlineHours /t REG_DWORD /d 72 /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v MaxDeferrals /t REG_DWORD /d 3 /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v DeferralOptions /t REG_SZ /d "60,240,1440" /f
reg add "HKLM\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome" /v ProcessNames /t REG_MULTI_SZ /d "chrome" /f
```

### 2. Optional web-source application (PowerShell)

```powershell
$key = 'HKLM:\SOFTWARE\Arkimentum\AppMonitor\Apps\7zip'
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name DisplayName            -Value '7-Zip'                                        -PropertyType String      -Force | Out-Null
New-ItemProperty -Path $key -Name Enabled                -Value 1                                              -PropertyType DWord       -Force | Out-Null
New-ItemProperty -Path $key -Name Source                 -Value 'web'                                          -PropertyType String      -Force | Out-Null
New-ItemProperty -Path $key -Name VersionUrl             -Value 'https://www.7-zip.org/download.html'          -PropertyType String      -Force | Out-Null
New-ItemProperty -Path $key -Name VersionRegex           -Value 'Download 7-Zip (\d+\.\d+)'                    -PropertyType String      -Force | Out-Null
New-ItemProperty -Path $key -Name DownloadUrl            -Value 'https://www.7-zip.org/a/7z{version_nodots}-x64.exe' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name InstallerArgs          -Value '/S'                                           -PropertyType String      -Force | Out-Null
New-ItemProperty -Path $key -Name DetectDisplayNameRegex -Value '^7-Zip'                                       -PropertyType String      -Force | Out-Null
New-ItemProperty -Path $key -Name ProcessNames           -Value @('7zFM','7zG')                                -PropertyType MultiString -Force | Out-Null
```

### 3. Global settings and a per-user application (PowerShell)

```powershell
$root = 'HKLM:\SOFTWARE\Arkimentum\AppMonitor'
New-Item -Path $root -Force | Out-Null
Set-ItemProperty -Path $root -Name ScanIntervalMinutes         -Value 240
Set-ItemProperty -Path $root -Name NotificationIntervalMinutes -Value 240
Set-ItemProperty -Path $root -Name NotificationMode            -Value 'Quiet'
Set-ItemProperty -Path $root -Name LogLevel                    -Value 'Information'
Set-ItemProperty -Path $root -Name DefaultDeadlineHours        -Value 72
Set-ItemProperty -Path $root -Name DefaultDeferralOptions      -Value '60,240,1440'

# Visual Studio Code User Setup: installed in the user profile, so the tray agent does the work.
$code = "$root\Apps\vscode"
New-Item -Path $code -Force | Out-Null
Set-ItemProperty -Path $code -Name Source          -Value 'winget'
Set-ItemProperty -Path $code -Name WingetId        -Value 'Microsoft.VisualStudioCode'
Set-ItemProperty -Path $code -Name Context         -Value 'user'
Set-ItemProperty -Path $code -Name WingetExtraArgs -Value '--scope user'
Set-ItemProperty -Path $code -Name ProcessNames    -Value 'Code'      # comma-separated REG_SZ is fine
```

### 4. Group Policy style `AppList` entry (PowerShell)

```powershell
$appList = 'HKLM:\SOFTWARE\Policies\Arkimentum\AppMonitor\AppList'
New-Item -Path $appList -Force | Out-Null
New-ItemProperty -Path $appList -Name 'Mozilla.Firefox' -PropertyType String -Force -Value (
    'DisplayName=Mozilla Firefox;Enabled=1;Source=winget;WingetId=Mozilla.Firefox;Mandatory=1;' +
    'DeadlineHours=48;MaxDeferrals=2;DeferralOptions=60|240|1440;ProcessNames=firefox|crashreporter'
) | Out-Null
```

### Verifying what the agent actually read

The service prints the effective configuration, the applications it resolved, and the layer each
value came from:

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --show-config
```

The raw keys, for comparison:

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\Arkimentum\AppMonitor'
Get-ChildItem    'HKLM:\SOFTWARE\Arkimentum\AppMonitor\Apps'
Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Arkimentum\AppMonitor' -ErrorAction SilentlyContinue

# what the service made of it
Get-Content "$env:ProgramData\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Service_$(Get-Date -Format yyyyMMdd).log" -Tail 100
```

Configuration is re-read by the service on its own schedule; restart the `ArkimentumAppMonitor` service to
apply a change immediately.

## Related

- Graphical editor and the export formats: [AdminConsole.md](AdminConsole.md)
- Organization-managed configuration, provisioning and precedence examples: [Cloud.md](Cloud.md)
- Agent self-update: [SelfUpdate.md](SelfUpdate.md)
- Ready-made example: `deploy\Sample-Configuration.reg`, `deploy\Set-SampleConfiguration.ps1`
- Behaviour of these settings: [Architecture.md](Architecture.md)
- When something does not work: [Troubleshooting.md](Troubleshooting.md)

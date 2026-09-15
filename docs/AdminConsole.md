# Admin console

`Arkimentum.AppMonitor.Admin.exe` is the graphical front end for the agent's configuration on **one
machine**. It edits the local preference layer, `HKLM\SOFTWARE\Arkimentum\AppMonitor`, shows what the
service is doing right now, and turns the result into artefacts you can push to the rest of the fleet:
a JSON profile, a `.reg` file or a PowerShell script.

It is a convenience layer over [`docs/Registry.md`](Registry.md) - everything it writes can also be
written by hand, by `reg import` or by a script. In this mode it stores nothing of its own: no
configuration file, no database, no cloud call.

The same executable also has an **[Organization mode](#organization-mode)**: signed in with Entra ID
against the [cloud service](Cloud.md), it configures a whole organization rather than one machine, and
shows the devices and the inventory of the fleet. That mode does talk to the cloud - and still changes
nothing on the machine it runs on.

| | |
| --- | --- |
| Executable | `%ProgramFiles%\Arkimentum\AppMonitor\Admin\Arkimentum.AppMonitor.Admin.exe` |
| Start Menu | All users → `Programs\Arkimentum\Arkimentum AppMonitor Admin` |
| Edits | `HKLM\SOFTWARE\Arkimentum\AppMonitor` (preferences) |
| Reads (locked) | `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` (Group Policy / Intune) |
| Talks to | The service over the named pipe `Arkimentum.AppMonitor.Agent`, as an **admin** client; in organization mode also the cloud API over HTTPS with an Entra ID token |
| Log | `%ProgramData%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Admin_yyyyMMdd.log` |
| Exit codes | `0` success, `1` failure (including "UAC declined") |

## Elevation

The executable is manifested `asInvoker` and elevates **itself** at startup: it relaunches its own
image with the `runas` verb, which produces the normal UAC prompt, and the first (unelevated)
instance exits.

- Accept the prompt → the elevated instance opens and can write HKLM and control the service.
- Decline the prompt → a short message explains that administrative rights are required, and the
  process exits with code **1**. Nothing is changed.
- The only mode that does not elevate is `--user-config` (see [Headless use](#headless-use)), which is
  a testing mode against HKCU.

Because elevation happens inside the process rather than through a `requireAdministrator` manifest,
the console can be started from a normal Start Menu shortcut, and the unprivileged testing mode works
without a UAC prompt at all.

## Pages

### Overview

- **Service** - state of `ArkimentumAppMonitor` (Stopped / Running / Start pending) with **Start**,
  **Stop** and **Restart**.
- **Scan now** - connects to the service's named pipe as an `IpcClientKind.Admin` client and sends a
  `requestScan`. An admin client receives the state snapshot and may request scans; it never receives
  notifications, close prompts or user-context install work - those stay with the tray agents.
- **Prerequisites** - whether winget is available to the SYSTEM account: the detected version and
  path, healthy or not, and the last action or error of the service's prerequisite manager. The
  **Install or repair prerequisites** button runs the same check and repair the service runs on its
  own (`AutoInstallPrerequisites`, `PrerequisiteCheckIntervalHours`, `WingetMinimumVersion` - see
  [`Registry.md`](Registry.md#prerequisites)); it needs outbound HTTPS to github.com, aka.ms,
  nuget.org and the PowerShell Gallery.
- **Pending updates** - what the last scan found: application, installed and available version, state
  (available, deferred, scheduled, waiting for close, installing, failed), deadline and deferral count.
- **Configuration summary** - the effective values with the layer each came from (Policy, Preference,
  Default) and whether the Policies key is present at all.

Nothing on this page changes configuration.

### Settings

Every **global** value in the schema
(`src\Arkimentum.AppMonitor.Core\Configuration\SettingsSchema.cs`), grouped exactly as the agent
groups them:

| Group | Contains |
| --- | --- |
| Scanning | `ScanIntervalMinutes`, `ScanOnStartup`, `StartupDelaySeconds`, `PolicyTickSeconds`, `CheckTimeoutMinutes`, `InstallTimeoutMinutes` |
| Notifications | `NotificationsEnabled`, `NotificationIntervalMinutes`, `ShowInstalledNotifications`, `LaunchTrayAgent` |
| Default update behaviour | `DefaultMandatory`, `DefaultDeadlineHours`, `DefaultMaxDeferrals`, `DefaultDeferralOptions`, `DefaultAutoInstall`, `DefaultCloseGracePeriodMinutes`, `DefaultForceCloseAtDeadline` |
| Sources | `WingetEnabled`, `WebSourcesEnabled`, `UseCatalog`, `EnableAllCatalogApps`, `CatalogPath`, `ProxyUrl`, `WingetGlobalArgs`, `WingetIncludeUnknown`, `WingetPath`, and the prerequisite settings `AutoInstallPrerequisites`, `PrerequisiteCheckIntervalHours`, `WingetMinimumVersion` |
| Logging and storage | `LogLevel`, `LogDirectory`, `LogRetentionDays`, `MaxLogFileSizeMB`, `StateDirectory` |
| Advanced | `TrayPath`, and every value marked advanced in the groups above |

Each value is written with the registry type the agent expects (`REG_DWORD` for numbers and
booleans, `REG_EXPAND_SZ` for paths, `REG_MULTI_SZ` for string lists), so you never have to think
about types. Ranges are enforced in the UI; the agent itself clamps out-of-range values instead of
rejecting them.

Changes apply without a service restart: the service re-reads the registry on every policy tick
(`PolicyTickSeconds`, default 60 s).

### Applications

- **Add from the catalog** - pick an entry from `catalog.json` (winget id, vendor URLs, detection
  regexes, process names come along) and set only the behaviour you care about.
- **Add a custom AppId** - for anything not in the catalog; you supply identity, source and detection
  yourself.
- **Edit** - every per-app value: `Enabled`, `DisplayName`, `Source`, `Context`, the winget fields,
  the web-source fields (`VersionUrl`, `VersionRegex`, `DownloadUrl`, `InstallerType`,
  `InstallerArgs`, `Sha256`/`Sha256Url`), the detection fields and the behaviour fields
  (`Mandatory`, `DeadlineHours`, `MaxDeferrals`, `DeferralOptions`, `AutoInstall`, `ProcessNames`,
  `CloseGracePeriodMinutes`, `ForceCloseAtDeadline`, `NotificationIntervalMinutes`).
- **Test detection** - runs the real check for that one application (the winget query or the web
  request plus the version regex) **as the administrator running the console**, and shows what it
  found: installed version, available version, and the error if it failed. It is a check only - it
  never installs anything, and it does not touch the service's state.

  A result here is not a guarantee for the service: the service checks as LocalSystem, where winget
  may behave differently (see the App Installer notes in
  [`Troubleshooting.md`](Troubleshooting.md)), and per-user applications are checked by the tray agent
  in the user's own session.

Applications are written as `Apps\<AppId>` subkeys. Entries in the flat `AppList` format are read and
shown, and are converted to subkeys when you save that application.

### Export & Import

Export the current configuration in any of the three formats below, or import a JSON profile
(**Replace** or **Merge**) produced on another machine.

## Organization mode

Everything above configures **this machine**. Organization mode is the same console pointed at the
[cloud service](Cloud.md) instead: it configures an organization, and every enrolled device collects
the result on its own.

Switch with **Organization** in the console's header. Local mode stays exactly as it is - the two do
not interfere, and the console remains usable on a machine that has no cloud at all.

### Signing in

Organization mode signs in with **Entra ID**, not with local administrative rights:

1. You give the console the API's base URL (the same value as `CloudServerUrl`; it offers the one
   configured on this machine).
2. The console fetches `/api/v1/public/auth-config` - the Entra client id, the authority and the scope
   to request. Nothing about the sign-in is hard-coded in the console.
3. It acquires a token interactively (the normal Entra sign-in window, MFA and Conditional Access
   included) and calls `/api/v1/admin/me`.
4. `AdminMeResponse` comes back with your name, your tenant and the organizations you may administer.
   With more than one, you pick; with one, it opens straight away.

Local elevation is irrelevant here: organization mode changes nothing on this machine, so it works
whether or not the console elevated. Your Entra account's permissions decide what you can do, not
membership of the local Administrators group.

### Devices

Every enrolled device in the organization: name, last logon user, OS version, agent version, when it
enrolled, when it was last seen, when it last scanned, the configuration version it has applied,
pending and failed update counts, and whether its prerequisites are healthy. Sort and filter to find
the devices that are behind, have failing updates, or have not reported.

Open one for the detail view: its full inventory, its tracked updates with states, deadlines and
deferral counts, its recent events, and the commands still queued for it.

The four commands an administrator can queue for a device, from the list or the detail view:

| Command | Effect on the device |
| --- | --- |
| **Scan now** | A full scan immediately, out of band with the scan interval. |
| **Report now** | Sends a report from the current state without scanning first. |
| **Repair prerequisites** | The same winget check and repair as `--prerequisites`. |
| **Update agent** | Checks the release feed and installs a newer agent, regardless of `AgentAutoUpdate`. |

Commands are **queued, not pushed**: the device picks them up on its next sync, so expect them to run
within one `CloudSyncIntervalMinutes` (15 by default), and never at all while the device is offline.
The console shows a command as pending until the device acknowledges it in a report. Nothing here
opens a connection to the machine.

Deleting a device removes its records from the organization. It does not touch the machine - if the
agent is still installed and still provisioned, it enrols again on its next sync.

### Inventory

Every application seen across the organization, aggregated: display name, publisher, winget id,
catalog id, how many devices have it, how many have an update available, the distinct installed
versions with a device count each, the predominant install context, and when it was last seen.

This is the page that answers "how much of the fleet is on the old version?" without visiting a
machine. From a row you can start monitoring the application - it is added to the organization's
applications with the catalog's identity and detection data filled in, and you choose only the
behaviour (mandatory, deadline, deferrals, processes to close).

### Organization settings and applications

The same editors as local Settings and Applications - the same schema, the same ranges, the same
"Override" per value - but writing the organization's configuration instead of this machine's
registry. Values you do not override are simply absent, and each device falls back through its own
local preference, the catalog and the built-in default.

Changes are staged until you press **Publish**. Publishing writes a new configuration version, records
who published it and when, and hands it out on each device's next sync. Because the console sent the
version it started from (`baseConfigVersion`), a publish is rejected with a conflict if someone else
published while you were editing, rather than quietly overwriting their work - reload and redo your
change.

Two limits worth knowing:

- The connection values (`CloudServerUrl`, `CloudOrganizationId`, `CloudEnrollmentKey`) cannot be
  published. Devices ignore them in the cloud layer by design, so the editor does not offer them.
- A value that a device's Group Policy also sets will not take effect there: policy outranks the
  organization configuration. The Devices page makes this visible - the device reports the
  configuration version it applied, and `--show-config` on the device names the winning layer.

Precedence in full, with worked examples: [`Cloud.md`](Cloud.md#precedence).

### Enrollment

The page that hands out the three values a device needs, and generates the snippets that set them:

- the server URL and the organization id, always shown;
- the enrollment key - shown **once**, when it is created or rotated, and never again (the server keeps
  only a hash of it);
- **Rotate key**, for when a device that carried the key is lost. Devices that already enrolled are
  unaffected: they authenticate with their own per-device key, not with this one.

The provisioning snippets it generates, with your organization's real values filled in (and the key
replaced by `<enrollment key>` once it can no longer be shown):

| Snippet | Use |
| --- | --- |
| **PowerShell** | An idempotent, parameterised script for an Intune platform script, a Win32 app, a GPO startup script or an RMM. It re-launches itself in the 64-bit host when started from a 32-bit agent, so the values never land in `WOW6432Node`. |
| **`.reg`** | `reg import`, a Group Policy Preferences item, or a double-click. |
| **Installer command line** | `Install-ArkimentumAppMonitor.ps1` with the three parameters, for a fresh install that enrols on its first start. |
| **Intune Win32 detection rule** | The registry detection rule that proves the values landed, plus the install, uninstall and install-behaviour settings to go with it. |

Treat all of them as secrets while the key is in them. Full recipes, the Group Policy route and the
network requirements: [`Cloud.md`](Cloud.md#provisioning-the-three-values).

## Override, default and policy lock

Three states per value:

| State | What it means | Registry |
| --- | --- | --- |
| **Default** | No override. The built-in default applies (it is shown next to the field). | The value does not exist in the preference key. |
| **Override** | You ticked *Override* and set a value. | The value exists under `HKLM\SOFTWARE\Arkimentum\AppMonitor`. |
| **Locked** | Managed by Group Policy / Intune: shown greyed out with "Managed by Group Policy / Intune" and the policy value. | The value exists under `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor`. |
| **Locked (organization)** | The organization configuration from the cloud sets this value, so it overrides whatever is written locally: shown greyed out with "Managed by the organization …" and the organization's value. A policy value still wins over it. Applications that exist only in the organization configuration are listed read-only with an *Organization* badge. | The value is in the service's cached `cloud-config.json` (state directory) and `CloudConfigEnabled` is not turned off. Edit it under *Organization → Settings / Applications* instead. |

- Clearing *Override* **deletes** the value from the preference key - it does not write the default.
  This keeps the key small and makes "what did the administrator actually decide?" answerable.
- A locked value cannot be edited by the console at all. The console never writes to the Policies key
  in interactive mode; Group Policy owns it, and a write would be overwritten at the next policy
  refresh anyway. To change a locked value, change it in Group Policy or Intune.
- Precedence is per value, not per key: an application can take its deadline from policy, its process
  names from the catalog and everything else from the preferences. Full rules:
  [`Registry.md`](Registry.md#layers-and-precedence).
- The `--export --policy` switch and the *target the Policies key* option in the export dialog exist
  for the deployment artefacts only - they generate a file that writes the Policies key on **other**
  machines. See [Relation to the ADMX policy layer](#relation-to-the-admx-policy-layer).

## Export formats

All three are generated by
`src\Arkimentum.AppMonitor.Core\Configuration\SettingsExporter.cs` from the same
`SettingsDocument`, so they contain exactly the same values.

| Format | Extension | Good for |
| --- | --- | --- |
| JSON profile | `.json` | Moving a configuration between machines through the console (or `--import`); keeping it in source control. |
| Registration entries | `.reg` | `reg import`, Group Policy Preferences, RMM one-liners, a double-click on a technician's machine. |
| PowerShell script | `.ps1` | Intune platform scripts, Intune Win32 apps, GPO startup scripts, any RMM that runs PowerShell. |

### JSON profile

Schema `arkimentum-appmonitor-settings/1` (`SettingsDocument.cs`). Global values and per-app values in
natural JSON types, plus provenance (`exportedUtc`, `exportedBy`, `exportedFrom`, `productVersion`,
`description`). It is the only format that can be re-imported by the console, and the only one that
is validated on import (unknown names, out-of-range numbers and invalid choices are reported before
anything is written).

```json
{
  "$schema": "arkimentum-appmonitor-settings/1",
  "description": "Standard workstation profile",
  "exportedUtc": "2026-09-14T13:05:11+00:00",
  "global": {
    "ScanIntervalMinutes": 240,
    "DefaultForceCloseAtDeadline": true,
    "LogLevel": "Information"
  },
  "apps": {
    "chrome": {
      "Enabled": true,
      "Source": "winget",
      "WingetId": "Google.Chrome",
      "Mandatory": true,
      "DeadlineHours": 72,
      "ProcessNames": ["chrome"]
    }
  }
}
```

### .reg file

`Windows Registry Editor Version 5.00`, written for the **64-bit** view. With *replace applications*
on (the default) the file starts with

```
[-HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor\Apps]
[-HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor\AppList]
```

so the application set in the file is authoritative: applications configured on the target that are
not in the file are deleted. With `--no-replace-apps` those two lines are omitted and the file only
adds and overwrites.

The root can be the preference key or `...\Policies\Arkimentum\AppMonitor` (the `--policy` switch).

### PowerShell script

Idempotent, `[CmdletBinding(SupportsShouldProcess)]`, exits with 0. Two parameters:

| Parameter | Default | Effect |
| --- | --- | --- |
| `-RegistryPath` | the key the export targeted | Write somewhere else, e.g. the Policies key, without re-exporting. |
| `-ReplaceApps` | `1` unless exported with `--no-replace-apps` (`0`) | `1` deletes `Apps\<AppId>` subkeys that are not in the script and removes the flat `AppList` key. |

It re-launches itself through `%WINDIR%\SysNative\WindowsPowerShell\v1.0\powershell.exe` when it was
started by a 32-bit agent, so `HKLM\SOFTWARE` is never redirected to `WOW6432Node`. Supports
`-WhatIf`.

## Merge vs Replace, precisely

| | JSON import **Merge** | JSON import **Replace** |
| --- | --- | --- |
| Global value present in the profile | written | written |
| Global value **absent** from the profile, known to the schema | left as it is | **deleted** |
| Value in the key that the schema does not know (a foreign or future value) | left alone | **left alone** |
| `Apps\<AppId>` present in the profile | written | written |
| `Apps\<AppId>` **absent** from the profile | left as it is | **deleted, whole subkey** |
| Per-app value absent from that app's entry, known to the schema | left as it is | **deleted** |
| Flat `AppList` entry for an app that is written as a subkey | removed (the subkey supersedes it) | removed |
| `AppList` entry for an app not in the profile | left as it is | **removed** |
| The Policies key | never touched | never touched |

So *Replace* means "make the preference layer look exactly like this profile, as far as this product
understands it". It never deletes values it does not own, and it never touches Group Policy.

The other two formats are coarser, because a `.reg` file and a script can only add and delete whole
keys:

- `.reg` and `.ps1` with **replace applications**: the *application set* becomes authoritative
  (`Apps` and `AppList` are cleared first), but **global values are only ever written, never
  removed**. A global override that exists on the target and is not in the file survives.
- `.reg` and `.ps1` with `--no-replace-apps`: purely additive.

If you need a target to be byte-for-byte identical to the source, use the JSON profile with Replace,
or clear the key first (`Remove-Item 'HKLM:\SOFTWARE\Arkimentum\AppMonitor' -Recurse`) and then
import.

## Distribution recipes

Export once on a reference machine, then pick the channel that matches your fleet.

### (a) Intune platform script (the `.ps1`)

Devices → **Scripts and remediations** → *Platform scripts* → **Add** → Windows 10 and later.

| Setting | Value |
| --- | --- |
| Script location | the exported `Settings.ps1` |
| Run this script using the logged-on credentials | **No** (runs as SYSTEM) |
| Enforce script signature check | as your tenant requires |
| Run script in 64-bit PowerShell host | **Yes** |

The script is idempotent, so the one-time execution model of platform scripts is fine; re-running it
after a change is what you get by uploading a new version. The service picks the values up within one
policy tick - no reboot, no service restart.

### (b) Intune Win32 app

Wrap the exported script with `IntuneWinAppUtil.exe` (source folder = the folder containing
`Settings.ps1`, setup file = `Settings.ps1`).

| Setting | Value |
| --- | --- |
| Install command | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Settings.ps1` |
| Uninstall command | `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Remove-Item 'HKLM:\SOFTWARE\Arkimentum\AppMonitor' -Recurse -Force -ErrorAction SilentlyContinue"` |
| Install behaviour | System |
| Detection rule | Manually configure → **Registry** |

Detection rule, for example:

| Field | Value |
| --- | --- |
| Key path | `HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor` |
| Value name | `ScanIntervalMinutes` |
| Detection method | **Value exists** (or *Integer comparison → equals 240* to detect a specific profile version) |
| Associated with a 32-bit app on 64-bit clients | **No** |

Use any value the export actually writes. An integer comparison is the better choice when you push
profile revisions and want Intune to re-run the app after a change - bump a value you control (for
example `ScanIntervalMinutes`, or a deliberately versioned setting) each time.

Win32 apps are preferable to platform scripts when you need detection, requirements, dependencies or
a supersedence chain; platform scripts are simpler when you just want the values on every device.

### (c) Group Policy

Two ways, both with the `.reg` export:

1. **Group Policy Preferences → Registry wizard.** Copy the `.reg` file to the machine you edit the
   GPO from, then Computer Configuration → Preferences → Windows Settings → **Registry** →
   right-click → New → **Registry Wizard**, choose *Local computer*, and tick the values under
   `HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor` after importing the file into that machine's
   registry (`reg import Settings.reg` in an elevated 64-bit prompt). The wizard creates one
   preference item per value, which you can then set to *Update* or *Replace* per item. This gives
   you per-value control and item-level targeting.
2. **Startup script.** Computer Configuration → Policies → Windows Settings → Scripts →
   **Startup**, with a one-line `.cmd` next to the `.reg` file in the GPO's `Machine\Scripts\Startup`
   folder:

   ```bat
   reg import "%~dp0Settings.reg" /reg:64
   ```

   Startup scripts run as SYSTEM in the 64-bit context; `/reg:64` makes that explicit and harmless.
   Or use the exported `.ps1` as a PowerShell startup script instead - it handles the 64-bit
   redirection itself.

Both write the **preference** key, which Group Policy does not clean up when the GPO is unlinked. For
settings that must disappear with the policy, use the ADMX template instead (below).

### (d) RMM or one-off

```powershell
# elevated, 64-bit
reg import C:\temp\Settings.reg
```

`reg.exe` inherits the bitness of the calling process. From a 32-bit RMM agent add `/reg:64`, or let
the exported `.ps1` relaunch itself:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\temp\Settings.ps1
```

Double-clicking the `.reg` file works too: it prompts for elevation and then for confirmation, and
imports into the 64-bit view on 64-bit Windows.

### (e) JSON profile on another client

Either through the UI - **Export & Import** → *Import profile* → choose the file → **Replace** or
**Merge** - or headless:

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Admin\Arkimentum.AppMonitor.Admin.exe" --import C:\temp\Standard.json
```

The import is validated first; a profile with unknown value names or out-of-range numbers is rejected
as a whole and nothing is written.

## Relation to the ADMX policy layer

| | Preference key | Policies key |
| --- | --- | --- |
| Path | `HKLM\SOFTWARE\Arkimentum\AppMonitor` | `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` |
| Written by | installer, admin console, exported `.reg`/`.ps1` | Group Policy (ADMX), Intune ADMX ingestion, or an export targeting it |
| Precedence | loses | **wins** |
| Standard users | cannot write (administrators only) | cannot write at all |
| Removed when the policy is unassigned | no | yes (Group Policy cleans its own key) |

The exports can target the Policies key (`--policy`, or the option in the export dialog) so a script
can *emulate* policy on machines that are not managed by Group Policy or Intune. That is a fallback,
not the recommended path:

- For a managed fleet, deploy the real ADMX template (`deploy\policy\ArkimentumAppMonitor.admx` and
  `en-US\*.adml`) - see [`deploy/policy/README.md`](../deploy/policy/README.md). Real policy is
  reported in `gpresult`, is refreshed automatically, is tamper-resistant, and disappears when you
  unassign it.
- A script-written Policies key looks like policy to the agent but is invisible to Group Policy
  tooling, and nothing removes it when you stop wanting it.
- The admin console **cannot edit policy-managed values** - it only displays them as locked. If you
  write the Policies key with an exported script, you are also removing those values from what the
  console (and any local administrator) can change. That is sometimes exactly the point.

A sensible split: ADMX/Intune for the handful of settings the organisation mandates, the admin console
and the preference key for everything else.

## Headless use

Every export and import is available on the command line, so a technician's profile can be produced
in a pipeline and the console never has to be opened. Exit code `0` on success, `1` on failure.

```powershell
$admin = "$env:ProgramFiles\Arkimentum\AppMonitor\Admin\Arkimentum.AppMonitor.Admin.exe"

# Export the current configuration; the format follows the extension
& $admin --export C:\temp\Standard.json
& $admin --export C:\temp\Settings.reg
& $admin --export C:\temp\Settings.ps1

# Artefacts that write the Group Policy key instead of the preference key
& $admin --export C:\temp\Policy.reg --policy
& $admin --export C:\temp\Policy.ps1 --policy

# Purely additive artefacts: do not delete applications on the target
& $admin --export C:\temp\Extra.reg --no-replace-apps

# Import a profile (Replace is the default; --merge keeps what the profile does not mention)
& $admin --import C:\temp\Standard.json
& $admin --import C:\temp\Standard.json --merge

# Testing without admin rights: HKCU instead of HKLM, no service control
& $admin --user-config
& $admin --export "$env:TEMP\lab.json" --user-config
```

| Switch | Effect |
| --- | --- |
| `--export <file>` | Write the configuration to `<file>`; `.json`, `.reg` and `.ps1` select the format. |
| `--policy` | With `--export`: the artefact writes `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor`. |
| `--no-replace-apps` | With `--export`: do not clear `Apps`/`AppList` on the target. |
| `--import <file.json>` | Import a JSON profile; **Replace** unless `--merge` is given. |
| `--merge` | With `--import`: merge instead of replace. |
| `--user-config` | Testing only: `HKCU` instead of `HKLM`, no elevation, no service control. |

A headless run still writes to
`%ProgramData%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Admin_yyyyMMdd.log` - check it when
an exit code of 1 does not explain itself.

`--user-config` mirrors the service's switch of the same name: `HKCU\SOFTWARE\Arkimentum\AppMonitor`
and `HKCU\SOFTWARE\Policies\Arkimentum\AppMonitor`. It is for lab work and for trying an export
without administrative rights; production configuration always lives in HKLM.

## Installing and removing the console

`deploy\Install-ArkimentumAppMonitor.ps1` copies `Admin\` to
`%ProgramFiles%\Arkimentum\AppMonitor\Admin` next to `Service\` and `Tray\` and creates the all-users
Start Menu shortcut. `-NoAdminConsole` skips the shortcut (and removes one left by an earlier
install); the binaries are still copied, so headless use and a manual start keep working.

`deploy\Uninstall-ArkimentumAppMonitor.ps1` stops the console, removes the shortcut, removes the
`Programs\Arkimentum` folder when it is empty, and deletes the install folder.

## Related

- Registry reference for every value: [`Registry.md`](Registry.md)
- The cloud service, provisioning and precedence: [`Cloud.md`](Cloud.md)
- Agent self-update: [`SelfUpdate.md`](SelfUpdate.md)
- How the agent behaves: [`Architecture.md`](Architecture.md)
- Group Policy / Intune ADMX: [`../deploy/policy/README.md`](../deploy/policy/README.md)
- When something does not work: [`Troubleshooting.md`](Troubleshooting.md)

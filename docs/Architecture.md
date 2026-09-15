# Architecture

## Components

| Component | Project | Runs as | Responsibility |
| --- | --- | --- | --- |
| Service | `src\Arkimentum.AppMonitor.Service` → `Arkimentum.AppMonitor.Service.exe` | LocalSystem, service `ArkimentumAppMonitor` (delayed auto-start) | Reads the configuration, scans for updates, tracks state, enforces deadlines, installs machine-wide updates, hosts the named pipe, launches tray agents. |
| Tray agent | `src\Arkimentum.AppMonitor.Tray` → `Arkimentum.AppMonitor.Tray.exe` | The logged-on user, one instance per interactive session | Tray icon and toasts, deferral/install choices, closing the user's applications, and per-user scans and installs in that user's session. |
| Admin console | `src\Arkimentum.AppMonitor.Admin` → `Arkimentum.AppMonitor.Admin.exe` | An administrator, elevated (manifest `asInvoker` + self-elevation with the `runas` verb at startup; exit code 1 when UAC is declined) | Edits `HKLM\SOFTWARE\Arkimentum\AppMonitor`, shows policy-managed values as locked, starts/stops the service, asks it to scan, and exports the configuration as a JSON profile, a `.reg` file or a deployment script. See [AdminConsole.md](AdminConsole.md). |
| Core | `src\Arkimentum.AppMonitor.Core` | library | Registry reader, models, inventory scanner, update providers, version comparison, file logging, IPC, and the settings schema / store / exporters used by the admin console (`Configuration\SettingsSchema.cs`, `RegistrySettingsStore.cs`, `SettingsDocument.cs`, `SettingsExporter.cs`). |
| Cloud client | `src\Arkimentum.AppMonitor.Core\Cloud` (`CloudClient`, `CloudConfigCache`, `DeviceCredentialStore`, `CloudContracts`) | library, used by the service and the console | Enrolment, configuration fetch with ETag, reporting, release manifests, the DPAPI-protected device credential, and the wire contracts both sides serialise. Inert until `CloudServerUrl` is configured. |
| Cloud service | `cloud\` (Azure Functions, Azure SQL, Blob, Bicep) | Azure, optional | Organization configuration, device records and inventory, queued commands, the release mirror, and the Entra-authenticated admin API. See [`Cloud.md`](Cloud.md) and [`cloud/README.md`](../cloud/README.md). |
| Brand UI | `src\Arkimentum.AppMonitor.UI` | library | WPF theme, styles, fonts and shared controls used by the tray agent and the admin console. |
| Catalog | `catalog\catalog.json`, published next to the service exe | data | Ready-made application definitions (winget ids, vendor URLs, detection regexes, process names) used as a fallback layer for per-app **identity, source and detection** values. It never supplies behaviour. |

```mermaid
flowchart LR
    subgraph Machine["Machine (session 0)"]
        SVC["Arkimentum.AppMonitor.Service.exe<br/>LocalSystem"]
        STATE[("state.json<br/>%ProgramData%")]
        SLOG[("Service log<br/>%ProgramData%\...\Logs")]
        CAT[("catalog.json")]
        CCFG[("cloud-config.json<br/>cached org configuration")]
        CRED[("device.credential<br/>DPAPI")]
    end

    subgraph Registry["Registry (HKLM, read-only)"]
        POL["SOFTWARE\Policies\Arkimentum\AppMonitor<br/>Group Policy / Intune"]
        PREF["SOFTWARE\Arkimentum\AppMonitor<br/>local preferences"]
    end

    subgraph Sessions["Interactive sessions"]
        T1["Arkimentum.AppMonitor.Tray.exe<br/>session 1"]
        T2["Arkimentum.AppMonitor.Tray.exe<br/>session 2"]
        TLOG[("Tray log<br/>%LOCALAPPDATA%")]
        ADM["Arkimentum.AppMonitor.Admin.exe<br/>elevated, on demand"]
    end

    subgraph Sources["Update sources"]
        WG["winget"]
        WEB["Vendor web sites<br/>version page + installer"]
    end

    subgraph CloudSvc["Cloud service (optional)"]
        API["AppMonitor API<br/>/api/v1/device/*, /api/v1/admin/*"]
        GH["GitHub Releases<br/>manifest.json + package.zip"]
    end

    POL -- "wins over" --> CCFG
    CCFG -- "wins over" --> PREF
    POL --> SVC
    CCFG --> SVC
    PREF --> SVC
    CAT --> SVC
    SVC -- "enroll / config / report<br/>outbound HTTPS" --> API
    API --> CCFG
    API --> CRED
    CRED --> SVC
    SVC -- "manifest + package,<br/>SHA-256 verified" --> GH
    ADM -- "Entra ID token:<br/>devices, inventory, publish" --> API
    SVC <--> STATE
    SVC --> SLOG
    SVC -- "machine-wide checks and installs" --> WG
    SVC --> WEB
    SVC <-- "named pipe Arkimentum.AppMonitor.Agent<br/>newline-delimited JSON" --> T1
    SVC <-- "named pipe" --> T2
    SVC -- "launches into session (LaunchTrayAgent=1)" --> T1
    T1 -- "per-user checks and installs" --> WG
    T1 --> WEB
    T1 --> TLOG
    ADM -- "reads and writes" --> PREF
    POL -. "read-only: shown locked" .-> ADM
    SVC <-- "named pipe (admin client):<br/>state, requestScan" --> ADM
```

There are no network listeners. The only local interface is the named pipe; everything else is
outbound HTTPS - to winget sources and vendor sites, and, when the cloud service is configured, to the
API and the release feed. Administrator commands are queued server-side and collected by the device on
its next poll; nothing reaches into the machine.

With no `CloudServerUrl` configured, the whole *Cloud service* box is absent: the agent behaves exactly
as it did before, and `cloud-config.json` and `device.credential` never exist.

## Scan cycle

```mermaid
sequenceDiagram
    participant S as Service
    participant R as Registry + catalog
    participant I as Inventory (Uninstall keys)
    participant P as Providers (winget / web)
    participant T as Tray agent (user session)

    S->>R: Read settings and app policies
    S->>I: Enumerate installed apps (HKLM + every loaded user hive)
    S->>S: Resolve Context for each app (auto -> system / user)
    S->>P: Check system-context apps (winget serialised, web in parallel)
    S->>T: RunUserScan (apps resolved to that user)
    T->>P: Check in the user's session (HKCU inventory, winget --scope user)
    T-->>S: UserScanResult
    S->>S: Merge results into the pending-update set, persist state.json
    S->>T: Notify / PromptClose / RunUserInstall as the policy state machine dictates
```

- The first scan runs `StartupDelaySeconds` after service start (when `ScanOnStartup` is on), then
  every `ScanIntervalMinutes`.
- Between scans a lightweight tick every `PolicyTickSeconds` re-evaluates deferrals, deadlines,
  blocking processes and notification cadence; it does not contact any source.
- Up to `UpdateChecker.MaxParallelChecks` (3) applications are checked at a time; winget calls are
  additionally serialised because winget keeps machine-wide state.
- A single check may not take longer than `CheckTimeoutMinutes` (default 3); a single install may not
  take longer than `InstallTimeoutMinutes` (default 30).
- A failure for one application never fails the scan: it is recorded as an error result for that
  application.

### Service command line

The service executable doubles as its own diagnostic tool (`asInvoker`, so it starts without a UAC
prompt; console mode warns when it is not elevated).

| Switch | Effect |
| --- | --- |
| *(none)* | Run under the Service Control Manager. |
| `--console` | Run interactively; still hosts the pipe, so a tray agent can connect. |
| `--scan-once` | One scan in the console, then exit. |
| `--show-config` | Print the effective configuration, the resolved applications, and the layer each value came from. |
| `--no-delay` | Skip `StartupDelaySeconds` (implied by `--console` and `--scan-once`). |
| `--prerequisites` | Run the prerequisite check once in the console and exit: `0` = winget is healthy for SYSTEM, `2` = still unavailable, `1` = the check itself failed. Used by the installer and by the admin console's *Install or repair prerequisites* button. |
| `--user-config` | Testing only: configuration from `HKCU`, logs and state under `%LOCALAPPDATA%`, no admin rights needed. |
| `--cloud-status` | Print the cloud link's state - enrolled or not, device id, last configuration version and time, last report, last error, plus the effective settings and what is cached - and exit. Read-only, so it is safe while the service is running. Exit code 0. |
| `--cloud-enroll` | Enrol now using `CloudOrganizationId` and `CloudEnrollmentKey`, write `device.credential`, then exit: `0` = enrolled, `1` = failed. |
| `--report-now` | Build and send a report from the current state, then exit: `0` = sent, `1` = failed. |
| `--check-update` | Read the release manifest, print the decision (`UpToDate`, `ChannelMismatch`, `PinnedByTargetVersion`, `NoManifest`, `UpdateAvailable`, `Disabled`) and exit without installing: `0` = nothing to do, `2` = an update is available, `1` = the feed could not be read. |
| `--update-now` | Check, download, verify the SHA-256 and start the installer: `0` = started or nothing to do, `1` = failed. |
| `--version` | Print the service version. |

## Cloud sync cycle

Runs only when `CloudServerUrl` and `CloudOrganizationId` are configured. It is a separate loop from
the scan cycle, on `CloudSyncIntervalMinutes` (default 15), and a failure in it never affects update
management.

```mermaid
sequenceDiagram
    participant S as Service
    participant D as device.credential
    participant A as Cloud API
    participant C as cloud-config.json

    S->>D: Load the device credential
    alt None yet
        S->>A: POST /api/v1/device/enroll (organizationId + enrollmentKey + machineGuid)
        A-->>S: deviceId + deviceKey
        S->>D: DPAPI-protect, write, restrict the ACL
    end
    S->>A: GET /api/v1/device/config (Authorization: Device id:key, If-None-Match)
    alt 304
        S->>S: Keep the cached configuration
    else 200
        S->>C: Cache settings + configVersion
        S->>S: Queue the commands that came with it
    end
    S->>S: Execute queued commands (ScanNow, ReportNow, RepairPrerequisites, UpdateAgent)
    opt CloudReportingEnabled
        S->>A: POST /api/v1/device/report (inventory, updates, events, acknowledged commands)
        A-->>S: accepted, configChanged
    end
```

- The configuration is resolved from the **cached file**, not from a network call: the reader layers
  `cloud-config.json` between the policy key and the local preferences on every read, so an unreachable
  server changes nothing about how the device behaves.
- Whether the cloud layer applies at all (`CloudConfigEnabled`) is read from the registry layers only,
  and the layer can never supply `CloudServerUrl`, `CloudOrganizationId` or `CloudEnrollmentKey`.
- A report is also sent right after a scan and after an install finishes, not only on the interval.
- `ReportResponse.ConfigChanged` lets the server tell the device to fetch a new configuration at once
  instead of waiting for the next interval.

Data flows in full, the security model, and the exact fields that leave the device:
[`Cloud.md`](Cloud.md).

## Self-update

Runs on `AgentUpdateCheckIntervalHours` (default 12) when `AgentAutoUpdate` is on, and on demand
(`--update-now`, or the *Update agent* command).

1. Read the manifest from `AgentUpdateFeedUrl` - a GitHub releases API URL (`.../releases/latest` or
   `.../releases/tags/v1.2.0`, public repositories only) or a direct `manifest.json` - or from the cloud
   API's mirror (`/api/v1/device/release`) when the value is empty.
2. Compare `channel` with `AgentUpdateChannel`, and `version` with the running version and
   `AgentTargetVersion` (a ceiling: anything newer is skipped). `minimumSupportedVersion` is advisory -
   an agent below it logs a warning and updates.
3. Postpone while an application install is running, or while a previous agent update started less
   than 30 minutes ago.
4. Download `packageUrl` to `%ProgramData%\Arkimentum\AppMonitor\AgentUpdates\<version>\`, hashing as it
   streams, and compare with `sha256`. A mismatch deletes the download and abandons the update.
5. Extract, check the package really contains `Install-ArkimentumAppMonitor.ps1` and
   `Service\Arkimentum.AppMonitor.Service.exe`, write `update-pending.json`, then start
   `Install-ArkimentumAppMonitor.ps1 -SourceRoot <extracted> -NoSampleApps -SkipPrerequisites -Force`
   **detached as SYSTEM**, output redirected to `install.log` in the same folder - detached because the
   installer stops the service that started it.
6. The installer mirrors the binaries, reconfigures the service and starts it again. Configuration,
   `state.json`, `device.credential` and `cloud-config.json` live outside the install folder and are
   untouched.
7. On the next start the service reads `update-pending.json`: a changed version is logged as *Agent
   updated* and reported as an `AgentUpdated` event; an unchanged version after 30 minutes is logged as
   a failed update. The two most recent package folders are kept.

Releases come from `.github/workflows/release.yml` on a `v*` tag; see [`SelfUpdate.md`](SelfUpdate.md).

## Prerequisites

winget - the `Microsoft.DesktopAppInstaller` package - is the agent's only runtime prerequisite (the
executables are published self-contained). It is also the component most likely to be missing for the
`SYSTEM` account, because App Installer is a per-user MSIX that is not necessarily provisioned for
all users.

The service's **PrerequisiteManager** closes that gap when `AutoInstallPrerequisites = 1` (default):

```mermaid
flowchart TD
    START["Service start, then every<br/>PrerequisiteCheckIntervalHours (24)"] --> CHECK{"winget available to SYSTEM<br/>and >= WingetMinimumVersion?"}
    CHECK -- yes --> OK["Healthy - record version and path"]
    CHECK -- no --> MODULE["Microsoft.WinGet.Client module:<br/>Repair-WinGetPackageManager -AllUsers -Latest"]
    MODULE -- "module missing or failed" --> BUNDLE["Download App Installer .msixbundle + licence<br/>(microsoft/winget-cli release) plus<br/>VCLibs 14 Desktop and Microsoft.UI.Xaml 2.8"]
    BUNDLE --> PROV["Add-AppxProvisionedPackage (all users)"]
    MODULE --> RECHECK{"available now?"}
    PROV --> RECHECK
    RECHECK -- yes --> OK
    RECHECK -- no --> UNHEALTHY["Unhealthy - record last action and error;<br/>winget applications fail their check"]
    OK --> TRAY["Tray agent registers the provisioned package<br/>for the signed-in user<br/>(Add-AppxPackage -RegisterByFamilyName)"]
```

- Everything runs as `LocalSystem`, without user involvement and without a UAC prompt.
- The status (winget version and path, healthy or not, last action and last error) is part of what the
  admin console shows on its Overview page, which can also trigger a repair.
- `Arkimentum.AppMonitor.Service.exe --prerequisites` runs the same check once from a console.
- Outbound HTTPS required: `github.com` and `objects.githubusercontent.com` (the release assets),
  `aka.ms` (redirects), `nuget.org` (the VCLibs / UI.Xaml dependency packages) and the PowerShell
  Gallery (`www.powershellgallery.com`, `psg-prod-eastus.azureedge.net`) for the module route. With
  `AutoInstallPrerequisites = 0` nothing of this runs and winget has to be provisioned in the image.

Settings: `AutoInstallPrerequisites`, `PrerequisiteCheckIntervalHours`, `WingetMinimumVersion` - see
[Registry.md](Registry.md#prerequisites).

## Update state machine

Each detected update is tracked as a `PendingUpdate`, keyed by `AppId|Context|UserSid`.

```mermaid
stateDiagram-v2
    [*] --> Available: update detected
    Available --> Deferred: user defers (deferrals left, before deadline)
    Deferred --> Available: DeferredUntilUtc reached
    Available --> Scheduled: user chooses Install now / AutoInstall
    Deferred --> Scheduled: deadline reached (mandatory)
    Scheduled --> WaitingForClose: a configured process is running
    Scheduled --> Installing: nothing blocking
    WaitingForClose --> Installing: user closes the apps, or forced close at ForceCloseAtUtc
    WaitingForClose --> Deferred: user defers (still allowed)
    Installing --> Installed: installer succeeded
    Installing --> Failed: installer failed or timed out
    Failed --> Available: next scan still sees the update
    Installed --> [*]
```

Rules encoded in the model (`PendingUpdate`):

| Rule | Behaviour |
| --- | --- |
| Deferral allowed | Not while `Installing`, `Installed` or `Scheduled`; not past the deadline; and only while `MaxDeferrals = 0` (unlimited) or `DeferralCount < MaxDeferrals`. |
| Deadline | Only mandatory updates have one: `DeadlineUtc = FirstDetectedUtc + DeadlineHours`. `DeadlineHours = 0` means no deadline, so a mandatory update waits for the user but can no longer be deferred once deferrals run out. |
| Past deadline | `Mandatory` and `now >= DeadlineUtc`: deferral is refused and the install is forced. |
| Blocking processes | The `ProcessNames` of the application that are currently running. For user-context updates only processes in that user's session count. |
| Forced close | When the deadline has passed and `ForceCloseAtDeadline = 1`, the user is warned and `ForceCloseAtUtc = now + CloseGracePeriodMinutes`. At that moment the tray agent asks the windows to close (`WM_CLOSE`), waits the graceful period, and kills what is left; the service kills any survivor it can see. With `ForceCloseAtDeadline = 0` the update simply waits. |
| Auto install | `AutoInstall = 1` installs without asking as soon as no blocking process is running - no notification other than the optional "installed" toast. |
| Dismiss | A non-mandatory update the user dismissed is re-announced after `NotificationIntervalMinutes` (per-app override allowed). |
| Failure | `FailureCount` and `LastError` are kept; the update reappears in the next scan and is retried. |

Pending state is persisted to `state.json` so deferrals, deadlines and deferral counts survive a
service restart or reboot.

## IPC

Transport: named pipe `Arkimentum.AppMonitor.Agent`, message-per-line UTF-8 JSON, `System.Text.Json`
polymorphism with a `$type` discriminator (`Core\Ipc\IpcMessages.cs`). The service is the server and
accepts multiple concurrent clients; every tray agent is a client that reconnects with back-off.

Security: the pipe ACL grants `Authenticated Users` read/write (plus `CreateNewInstance`), and full
control to `LocalSystem` and `BUILTIN\Administrators`. The server does **not** trust the identity in
the message: it impersonates the caller to establish the SID and user name and reads the client's
session id from the pipe handle, so one user cannot act on another user's updates.

### Client → server

| `$type` | Class | Payload | Meaning |
| --- | --- | --- | --- |
| `hello` | `HelloMessage` | `SessionId`, `UserSid`, `UserName`, `AgentVersion`, `ClientKind` | Sent on every (re)connect. The server uses its own impersonated values for identity. |
| `getState` | `GetStateMessage` | - | Ask for a fresh snapshot. |
| `requestScan` | `RequestScanMessage` | - | User pressed "Check for updates". |
| `installNow` | `InstallNowMessage` | `UpdateKey` | Install this update now. |
| `defer` | `DeferMessage` | `UpdateKey`, `Minutes` | Postpone by the chosen number of minutes. |
| `dismiss` | `DismissMessage` | `UpdateKey` | Hide a non-mandatory update until the next notification interval. |
| `userInstallProgress` | `UserInstallProgressMessage` | `UpdateKey`, `Status` | Progress text from a user-context install. |
| `userInstallResult` | `UserInstallResultMessage` | `UpdateKey`, `Result` (`InstallResult`) | Outcome of a user-context install. |
| `userScanResult` | `UserScanResultMessage` | `ScanId`, `Results` (`UpdateCheckResult[]`) | Outcome of a user-context scan. |
| `processesClosed` | `ProcessesClosedMessage` | `UpdateKey`, `StillRunning`, `Declined` | Result of a close request. |

### Server → client

| `$type` | Class | Payload | Meaning |
| --- | --- | --- | --- |
| `state` | `StateMessage` | `Updates`, `LastScanUtc`, `NextScanUtc`, `ScanInProgress`, `ServiceVersion`, `Settings` (`SettingsSummary`, including `CloudConfigured`, `CloudEnrolled` and `OrganizationName` so the tray can show which organization manages the device) | Snapshot of the updates relevant to that session: machine-wide updates plus that user's own. |
| `notify` | `NotifyMessage` | `Kind` (`NotificationKind`), `Title`, `Body`, `Update` | Show a toast. |
| `promptClose` | `PromptCloseMessage` | `Update` | Blocking processes are running; ask the user to close them (with the forced-close countdown when one applies). |
| `runUserInstall` | `RunUserInstallMessage` | `Update`, `TimeoutMinutes` | Install this update in the user's session. |
| `runUserScan` | `RunUserScanMessage` | `ScanId`, `Apps`, `WingetEnabled`, `WebSourcesEnabled`, `ProxyUrl`, `WingetGlobalArgs`, `WingetIncludeUnknown` | Check these applications in user context. |
| `closeProcesses` | `CloseProcessesMessage` | `UpdateKey`, `ProcessNames`, `Force`, `GracefulWaitSeconds` | Close (or with `Force`, kill) those processes in the user's session. |
| `ack` | `AckMessage` | `InReplyTo`, `Ok`, `Message` | Acknowledgement / error text. |

Every message carries a `MessageId` and `SentUtc`.

### Client kinds

`HelloMessage.ClientKind` (`IpcClientKind`) tells the server what the connection is for. The kind is
advertised by the client and only ever **narrows** what it gets - identity and session are still taken
from the impersonated pipe handle, never from the message.

| Kind | Who | Gets | Does not get |
| --- | --- | --- | --- |
| `Tray` (0) | The per-session tray agent | `state`, `notify`, `promptClose`, `runUserInstall`, `runUserScan`, `closeProcesses` | - |
| `Admin` (1) | The admin console (and diagnostic clients) | `state` snapshots, `ack`; may send `getState` and `requestScan` | Never receives notifications, close prompts or user-context work - `notify`, `promptClose`, `runUserInstall`, `runUserScan` and `closeProcesses` are only sent to tray clients. |

So an elevated admin console connected in a session where the user is also signed in does not steal
that user's toasts or installs: the tray agent remains the only client that acts on behalf of the
user, and the console is a read-and-request observer that happens to have write access to the
registry instead.

### Admin console command line

`Arkimentum.AppMonitor.Admin.exe` opens its window with no arguments, and otherwise runs headless:
`--export <file>` (`.json`, `.reg` or `.ps1`, with `--policy` and `--no-replace-apps`),
`--import <file.json> [--merge]`, and `--user-config` for unprivileged lab use. Exit code 0 on
success, 1 on failure. Details: [AdminConsole.md](AdminConsole.md).

## Context resolution

`Context` decides **who** checks and installs an application.

| Configured | Resolved to | Who does the work |
| --- | --- | --- |
| `system` | System | The service, as LocalSystem. |
| `user` | User (per user SID) | The tray agent in that user's session. |
| `auto` | System if the application is found in the machine hive (HKLM Uninstall keys, 64- and 32-bit); User if it is only found in a user hive (`HKU\<SID>`) | Whichever matches. |

Notes:

- Running as LocalSystem the inventory scanner sees HKLM **and** every loaded user hive, so per-user
  installs of all logged-on users are visible to the service - but installing them still has to
  happen in the user's own session, which is why the work is delegated over IPC.
- A user hive that is not loaded (the user is not logged on) is invisible; that user's per-user
  applications are checked the next time they log on and the tray agent connects.
- Per-user applications produce one pending update **per user** (`AppId|User|<SID>`); a machine-wide
  application produces exactly one (`AppId|System|-`).
- If a user-context update is pending but no tray agent is connected for that user, nothing is
  installed for them until they log on again.

## Files and paths

| Path | Written by | Content |
| --- | --- | --- |
| `%ProgramFiles%\Arkimentum\AppMonitor\Service\` | installer | Service binaries and `catalog.json`. |
| `%ProgramFiles%\Arkimentum\AppMonitor\Tray\` | installer | Tray agent binaries. The service finds them through `TrayPath`, its own folder, then `..\Tray\`. |
| `%ProgramFiles%\Arkimentum\AppMonitor\Admin\` | installer | Admin console binaries. Started from the all-users Start Menu shortcut `Programs\Arkimentum\Arkimentum AppMonitor Admin`. |
| `%ProgramData%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Admin_yyyyMMdd.log` | admin console | What the console changed, and every headless export/import. |
| `%ProgramData%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Service_yyyyMMdd.log` | service | Service log, rolled daily and at `MaxLogFileSizeMB` (`..._1.log`, `..._2.log`), pruned after `LogRetentionDays`. |
| `%ProgramData%\Arkimentum\AppMonitor\state.json` | service | Pending updates, deferrals, deadlines, failure counts. |
| `%ProgramData%\Arkimentum\AppMonitor\Downloads\` | service | Installers downloaded from web sources (`StateDirectory\Downloads`). |
| `%ProgramData%\Arkimentum\AppMonitor\device.credential` | service | The per-device cloud key, DPAPI-protected (LocalMachine) with the ACL replaced: `SYSTEM` and `BUILTIN\Administrators` only. Machine-bound - never put it in a reference image. |
| `%ProgramData%\Arkimentum\AppMonitor\cloud-config.json` | service | The last organization configuration fetched, with its `configVersion`. Read on every configuration resolve, so it keeps working offline. |
| `%ProgramData%\Arkimentum\AppMonitor\cloud-status.json` | service | `serverUrl`, `organizationName`, `deviceId`, `enrolledUtc`, `lastConfigVersion`, `lastConfigUtc`, `lastReportUtc`, `lastError`. Also printed by `--cloud-status`. |
| `%ProgramData%\Arkimentum\AppMonitor\AgentUpdates\<version>\` | service | The downloaded release package, the extracted payload, and `install.log` from the installer run. The two most recent versions are kept. |
| `%ProgramData%\Arkimentum\AppMonitor\update-pending.json` | service | Written before the self-updater hands over; read on the next start to report the outcome. |
| `%LOCALAPPDATA%\Arkimentum\AppMonitor\Logs\Arkimentum.AppMonitor.Tray_yyyyMMdd.log` | tray agent | One log per user. |
| `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` | Group Policy / Intune | Policy configuration (wins). |
| `HKLM\SOFTWARE\Arkimentum\AppMonitor` | installer / admin | Local preference configuration. |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\ArkimentumAppMonitorTray` | installer | Logon fallback that starts the tray agent. |
| Application event log, source `Arkimentum AppMonitor` | service | Service lifecycle and severe errors. |

Log line format: `2026-09-14 15:59:01.123 +02:00 [INF] Category: message`.

## Identifiers

| Name | Value |
| --- | --- |
| Service name | `ArkimentumAppMonitor` |
| Service display name | `Arkimentum AppMonitor Agent` |
| Pipe | `Arkimentum.AppMonitor.Agent` |
| Event log source | `Arkimentum AppMonitor` (Application log) |
| Tray single-instance mutex | `Local\Arkimentum.AppMonitor.Tray` |
| Toast AppUserModelID | `Arkimentum.AppMonitor.Tray` |

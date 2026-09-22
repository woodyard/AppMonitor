# Agent self-update

The service can update **itself**: it reads a release manifest, compares the published version with the one it is
running, downloads and verifies the release package and hands over to that release's own
`Install-ArkimentumAppMonitor.ps1`, which stops the service, replaces the binaries and starts it again. Nothing else
in the fleet has to be scripted.

Self-update is on by default and reads the official releases at `https://api.github.com/repos/woodyard/AppMonitor/releases/latest`; set `AgentAutoUpdate=0` to turn it off, or point `AgentUpdateFeedUrl` at your own manifest, or at the keyword `cloud` to use the cloud API's mirror.


## Configuration

Registry values under `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` (or the preference key) - see
[`Registry.md`](Registry.md):

| Value | Meaning |
| --- | --- |
| `AgentAutoUpdate` | `1` (default) lets the service update itself on a timer. `0` = only on request. |
| `AgentUpdateFeedUrl` | Where the manifest comes from (see below). Default: `https://api.github.com/repos/woodyard/AppMonitor/releases/latest`. The keyword `cloud` (or an empty value under policy) asks the cloud API's mirror instead. |
| `AgentUpdateChannel` | `stable` (default) or `preview`. A manifest from another channel is ignored. |
| `AgentUpdateCheckIntervalHours` | How often the check runs (default 12, clamped to 1-720). |
| `AgentTargetVersion` | Pins this device: a manifest **newer** than this version is skipped. Empty = always the latest. |

## Feed URL forms

| Form | Example | What happens |
| --- | --- | --- |
| A `manifest.json` URL | `https://downloads.example.com/appmonitor/manifest.json` | The URL is fetched and parsed as a release manifest. |
| A GitHub releases API URL | `https://api.github.com/repos/<owner>/<repo>/releases/latest`<br>`https://api.github.com/repos/<owner>/<repo>/releases/tags/v1.2.0` | The release is fetched with `Accept: application/vnd.github+json`, its `manifest.json` asset is downloaded, and the manifest's `packageUrl` (the zip asset's `browser_download_url`) is used. |
| Empty, with the cloud connection configured | - | `GET /api/v1/device/release?channel=<channel>` on the cloud API returns the same manifest shape. |
| Anything else | `https://github.com/<owner>/<repo>/releases/latest` (the HTML page) | Logged as unsupported; nothing happens. |

Only **public** repositories are supported: no token is ever sent. For a private repository, mirror the release
through the cloud API (or any authenticated-free URL of your own) and point `AgentUpdateFeedUrl` there. Token support
is a possible follow-up.

## The manifest

`manifest.json` is the JSON form of `ReleaseManifest` (`Core/Cloud/CloudContracts.cs`), published next to the release
zip. `.github/workflows/release.yml` writes it on every `v*` tag:

```json
{
  "version": "1.2.0",
  "channel": "stable",
  "packageUrl": "https://github.com/<owner>/<repo>/releases/download/v1.2.0/Arkimentum.AppMonitor-1.2.0.zip",
  "sha256": "9f2c…",
  "sizeBytes": 177187637,
  "publishedUtc": "2026-01-02T03:04:05Z",
  "minimumSupportedVersion": "1.0.0",
  "releaseNotesUrl": "https://github.com/<owner>/<repo>/releases/tag/v1.2.0"
}
```

- `version`, `packageUrl` and `sha256` are required; a manifest without them is rejected.
- `sha256` is the lower-case hex SHA-256 of the zip. A mismatch is an error: the download is deleted and the update
  is abandoned.
- `channel` must match `AgentUpdateChannel` (an empty value means `stable` on both sides).
- `minimumSupportedVersion` is advisory: an agent below it logs a warning and updates.
- Versions are compared with the same lenient comparer the application updates use, so `1.0.10` > `1.0.9`.

## The update flow

1. **Check** - every `AgentUpdateCheckIntervalHours` (and on the `UpdateAgent` cloud command or the CLI switches), the
   manifest is resolved and the decision is logged: `UpToDate`, `ChannelMismatch`, `PinnedByTargetVersion`,
   `NoManifest` or `UpdateAvailable`.
2. **Guard** - an update is postponed while an application install is in progress, or while an earlier agent update
   started less than 30 minutes ago.
3. **Download** - to `<StateDirectory>\AgentUpdates\<version>\` (`%ProgramData%\Arkimentum\AppMonitor\AgentUpdates`),
   then the SHA-256 is verified and the zip extracted into the same folder. The package must contain
   `Install-ArkimentumAppMonitor.ps1` and `Service\Arkimentum.AppMonitor.Service.exe`, or the update is abandoned.
4. **Hand over** - `update-pending.json` (from/to version, start time) is written in the state directory and the
   installer is started **detached** and not waited for:

   ```text
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<dir>\Install-ArkimentumAppMonitor.ps1"
                  -SourceRoot "<dir>" -NoSampleApps -SkipPrerequisites -Force        > <dir>\install.log 2>&1
   ```

   The script stops the service, mirrors the new binaries into `%ProgramFiles%\Arkimentum\AppMonitor` and starts it
   again; the tray agent's Run value and the Start Menu shortcut are handled by that same script.
5. **Confirm** - on its next start the service reads `update-pending.json`: a changed version is logged as
   *Agent updated* and reported to the cloud as an `AgentUpdated` event; an unchanged version after 30 minutes is
   logged as a failed update (with a pointer to `install.log`). The two most recent package folders are kept.

In `--user-config` testing mode everything up to step 4 runs, but the installer is never started: the log says exactly
what would have been executed.

## Command line

```powershell
& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --check-update
# 0 = up to date / nothing to do, 2 = an update is available, 1 = the feed could not be read

& "$env:ProgramFiles\Arkimentum\AppMonitor\Service\Arkimentum.AppMonitor.Service.exe" --update-now
# 0 = installer started (or nothing to do), 1 = download, verification or start failed
```

An administrator can also queue the `UpdateAgent` command for a device in the cloud console; its optional argument is
a target version, applied like `AgentTargetVersion` for that one run.

## From the client

The user does not have to wait for the 12-hour check. The tray agent's details footer and its About dialog show the
agent version with what the last check concluded ("1.1.3 · up to date (checked 16:22)") and offer **Check for
updates**; when a newer release is known they also offer **Update now**, and the tray's context menu turns into
*Update AppMonitor to 1.1.4*. The admin console's Overview page has an **Update agent** button next to *Install or
repair prerequisites*.

All of them send one `updateAgent` IPC message (`CheckOnly` = true for a check) to the service, which does exactly
what the scheduled check does - as SYSTEM, without a UAC prompt, ending in the shipped installer that stops the
service, replaces the binaries and starts it again. Clients never touch a file.

The service refuses the request, with the reason in its acknowledgement, when:

| Refused because | The client is told |
| --- | --- |
| `AgentAutoUpdate` is `0` | *Agent updates are disabled by policy* - Intune, an RMM or a GPO owns the binaries, and a user must not overrule that. |
| An application update is installing | *An application is being updated; try the agent update again when it has finished.* |
| An agent update is already running | *An agent update is already running.* |
| The process has no self-updater (the CLI entry points) | *Agent updates are not available in this mode.* |

Otherwise the answer is the outcome of the check: *Up to date: 1.1.3*, *Update 1.1.4 available*, *Updating to 1.1.4 -
the agent will restart*, or *Check failed: …*. The result is broadcast to every tray agent, so a check started in one
session updates the version row in all of them, and `StateMessage.AgentUpdate` keeps `InProgress` up while the agent
replaces itself.

## Publishing a release

`.github/workflows/release.yml` runs on a `v*` tag (or `workflow_dispatch` with a version):
`deploy/Build-Release.ps1 -Version <tag without v>` builds and tests, the zip's SHA-256 is computed, `manifest.json`
is written, and both files are attached to the GitHub release. A tag containing `-preview` publishes on the `preview`
channel and marks the release as a pre-release.

## Where to look when it does not work

| What | Where |
| --- | --- |
| Decisions and download/verification | Service log, lines starting with `Agent update` |
| Installer output | `<StateDirectory>\AgentUpdates\<version>\install.log` |
| An update that is still in flight | `<StateDirectory>\update-pending.json` |
| Downloaded packages (last two kept) | `<StateDirectory>\AgentUpdates\` |

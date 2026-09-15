# Arkimentum AppMonitor - Group Policy template

| File | Purpose |
| --- | --- |
| `ArkimentumAppMonitor.admx` | Policy definitions. Writes to `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor`. |
| `en-US\ArkimentumAppMonitor.adml` | English (US) display strings and dialog layouts. |

Settings appear under **Computer Configuration → Policies → Administrative Templates → Arkimentum → AppMonitor**,
with two sub-categories:

| Sub-category | Contains |
| --- | --- |
| **Organization connection** | *Organization connection* (server URL, organization id, enrollment key), *Organization sync interval*, *Apply the organization configuration*, *Report inventory to the organization* |
| **Agent updates** | *Update the agent automatically*, *Agent update source* (feed URL, channel, check interval), *Pin the agent to a version* |

Everything written by these policies overrides the local preferences in `HKLM\SOFTWARE\Arkimentum\AppMonitor`.
A policy left **Not configured** falls back to the organization configuration delivered by the cloud
service (when one is in use), then to the local value, then to the agent's built-in default.
Full value reference: [`docs\Registry.md`](../../docs/Registry.md); the cloud layer and worked
precedence examples: [`docs\Cloud.md`](../../docs/Cloud.md).

Setting *Organization connection* here is the tamper-resistant way to connect devices: the values land
in the Policies key, which the admin console shows as locked and which the cloud layer can never
override. Restrict who may read the GPO - the enrollment key is in it.

Values managed here are shown as locked ("Managed by Group Policy / Intune") in the admin console and
cannot be edited there. The console can export a `.reg` file or a PowerShell script that targets the
Policies key (`--policy`), which is useful for machines outside Group Policy and Intune - but for a
managed fleet this template is the better instrument, because real policy is refreshed automatically,
is visible in `gpresult`, and is removed again when the GPO is unlinked. See
[`docs\AdminConsole.md`](../../docs/AdminConsole.md).

## Import into the domain Central Store

1. Create the Central Store if it does not exist yet (on a domain controller):

   ```
   \\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions
   \\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions\en-US
   ```

2. Copy the files:

   ```powershell
   $store = "\\contoso.com\SYSVOL\contoso.com\Policies\PolicyDefinitions"
   Copy-Item .\ArkimentumAppMonitor.admx        -Destination $store
   Copy-Item .\en-US\ArkimentumAppMonitor.adml  -Destination (Join-Path $store 'en-US')
   ```

3. Open the Group Policy Management Editor on any machine with RSAT. The category appears under
   Administrative Templates (the title bar says "Policy definitions (ADMX files) retrieved from the
   central store" when the Central Store is in use).

Without a Central Store, copy the same two files to `%SystemRoot%\PolicyDefinitions` and
`%SystemRoot%\PolicyDefinitions\en-US` on the machine that runs the editor.

Verify a target machine after a policy refresh (`gpupdate /target:computer /force`):

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Arkimentum\AppMonitor'
Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Arkimentum\AppMonitor\AppList'
```

## Intune

### Option A - ADMX ingestion (this template)

Intune can ingest a custom ADMX and then set its policies through OMA-URI. This is the only way to
use *this* template in Intune.

1. **Ingest the ADMX.** Devices → Configuration → Create → Windows 10 and later → Templates → Custom.
   Add an OMA-URI setting:

   - OMA-URI: `./Device/Vendor/MSFT/Policy/ConfigOperations/ADMXInstall/Arkimentum/Policy/UpgradeAdmx`
   - Data type: String
   - Value: the entire text of `ArkimentumAppMonitor.admx`

   The path segments are chosen by you, but must stay stable:
   `ADMXInstall/{AppName}/Policy/{SettingType}`. Changing them later re-ingests the template as a
   different app. (Verify the exact casing and segment rules against current Microsoft documentation
   for `ConfigOperations/ADMXInstall` before rolling out widely.)

2. **Set an ingested policy.** Add one OMA-URI per setting, using the ADMX category prefix and the
   policy `name` attribute from the ADMX:

   - OMA-URI: `./Device/Vendor/MSFT/Policy/Config/Arkimentum~Policy~Arkimentum~Upgrade/ScanInterval`
   - Data type: String
   - Value (enabled, with its decimal element):

     ```xml
     <enabled/><data id="ScanIntervalMinutes" value="240"/>
     ```

   The `Arkimentum~Policy~Arkimentum~Upgrade` part is `{AppName}~Policy~{category path}` - here the
   `Arkimentum` and `Upgrade` categories from this ADMX. Policy names to use after the last `/` are
   the `name` attributes in `ArkimentumAppMonitor.admx`, for example `ScanInterval`,
   `NotificationInterval`, `LogLevel`, `DefaultUpdateBehaviour`, `MonitoredApps`.

   Value formats:

   | Policy shape | Value |
   | --- | --- |
   | Simple on/off (`WingetEnabled`) | `<enabled/>` or `<disabled/>` |
   | One element (`ScanInterval`) | `<enabled/><data id="ScanIntervalMinutes" value="240"/>` |
   | Enum (`LogLevel`) | `<enabled/><data id="LogLevel" value="Information"/>` |
   | Multi-element (`DefaultUpdateBehaviour`) | `<enabled/><data id="DefaultMandatory" value="false"/><data id="DefaultDeadlineHours" value="72"/><data id="DefaultMaxDeferrals" value="3"/><data id="DefaultDeferralOptions" value="60,240,1440"/><data id="DefaultAutoInstall" value="false"/><data id="DefaultCloseGracePeriodMinutes" value="15"/><data id="DefaultForceCloseAtDeadline" value="true"/>` |
   | List (`MonitoredApps`) | `<enabled/><data id="MonitoredApps" value="Google.Chrome&#xF000;DisplayName=Google Chrome;Enabled=1;Source=winget;WingetId=Google.Chrome;Mandatory=1;DeadlineHours=72;ProcessNames=chrome"/>` |

   In list values, `&#xF000;` separates the value name from the value data, and repeated pairs are
   separated by another `&#xF000;`. Verify this encoding against current Microsoft documentation for
   ADMX-backed list policies before a large rollout - it is easy to get wrong and produces silently
   empty keys.

Note that the Settings Catalog does **not** show ingested custom ADMX policies; they are managed
through these Custom OMA-URI profiles only.

### Option B - plain registry settings (no ADMX)

If you would rather not ingest an ADMX, write the same registry values directly. Both work; do not
use both for the same value.

- **Settings Catalog / Custom profile:** there is no built-in CSP for third-party registry keys, so
  use a Win32 app, a PowerShell platform script, or a Proactive Remediation that writes
  `HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` (or `HKLM\SOFTWARE\Arkimentum\AppMonitor` for preferences).
  `deploy\Set-SampleConfiguration.ps1 -Scope Policy` shows exactly which values to write.
- **Win32 app / script:** run as system, 64-bit. A 32-bit host would write to `WOW6432Node`, where
  the agent does not look.
- **The three cloud values** (`CloudServerUrl`, `CloudOrganizationId`, `CloudEnrollmentKey`) are often
  the *only* thing worth deploying this way: with them in place the rest of the configuration is
  published once in the admin console's organization mode instead of being pushed to every device.
  Ready-made Intune snippets: [`docs\Cloud.md`](../../docs/Cloud.md#provisioning-the-three-values).

Whichever route you choose, confirm on a pilot device that the values landed in
`HKLM\SOFTWARE\Policies\Arkimentum\AppMonitor` and that the service picked them up (the service logs the
effective configuration and the layer each value came from at the start of every scan).

## Editing the template

After changing the ADMX or ADML, check that both files are well-formed and consistent:

```powershell
[xml](Get-Content .\ArkimentumAppMonitor.admx -Raw)       | Out-Null
[xml](Get-Content .\en-US\ArkimentumAppMonitor.adml -Raw) | Out-Null
```

Every `$(string.X)` and `$(presentation.X)` reference in the ADMX must exist in the ADML, every
element `id` must have a control with the matching `refId`, and the control type must match the
element type (`decimal` → `decimalTextBox`, `text` → `textBox`, `enum` → `dropdownList`,
`boolean` → `checkBox`, `list` → `listBox`). A mismatch makes the Group Policy editor show the
category as an error rather than failing loudly.

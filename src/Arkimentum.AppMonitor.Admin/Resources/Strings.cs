using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Admin.Resources;

/// <summary>
/// Every user-visible string of the admin console. English only; keeping them here means no literal ever lives
/// in a view or a code-behind file.
/// </summary>
public static class Strings
{
    // ---------------------------------------------------------------- product
    public const string ProductName = AgentSettings.ProductName + " Admin";
    public const string Copyright = "© 2026 Arkimentum";
    public const string WordmarkBrand = "Arkimentum";
    public const string WordmarkProduct = "APPMONITOR ADMIN";
    public const string WindowTitle = ProductName;
    public const string HeaderSubtitle = "Configure the update agent on this machine";
    public const string HeaderSubtitleOrganization = "Manage an organization in the AppMonitor cloud";

    // ---------------------------------------------------------------- elevation / startup
    public const string MustRunElevated = "Arkimentum AppMonitor Admin must run as an administrator.";
    public const string MustRunElevatedDetail =
        "Windows refused the elevation prompt, so the console cannot read or write the machine configuration. " +
        "Start it again from an elevated context, or use --user-config for an unprivileged test run against HKCU.";
    public const string TestingModeBanner = "Testing mode: reading and writing HKCU, service control disabled";
    public const string TestingModeDetail =
        "--user-config was given. Nothing here affects the machine configuration under HKLM, and the service is not touched.";
    public const string RegistryNotWritableTitle = "The configuration cannot be opened for writing";
    public static string RegistryNotWritable(string path, string message) =>
        $"{path} could not be opened for writing.\n\n{message}";

    // ---------------------------------------------------------------- navigation
    public const string NavOverview = "Overview";
    public const string NavSettings = "Settings";
    public const string NavApplications = "Applications";
    public const string NavExportImport = "Export & import";
    public const string NavAbout = "About";

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyphs.</summary>
    public const string GlyphOverview = "\uE80F";      // Home
    public const string GlyphSettings = "\uE713";      // Settings
    public const string GlyphApplications = "\uECAA";  // AppIconDefault
    public const string GlyphExportImport = "\uEDE1";  // CloudDownload-ish (Share)
    public const string GlyphAbout = "\uE946";         // Info
    public const string GlyphRefresh = "\uE72C";
    public const string GlyphWarning = "\uE7BA";
    public const string GlyphSuccess = "\uE73E";
    public const string GlyphError = "\uEA39";
    public const string GlyphLocked = "\uE72E";
    public const string GlyphFolder = "\uE838";
    public const string GlyphAdd = "\uE710";
    public const string GlyphCopy = "\uE8C8";
    public const string GlyphRemove = "\uE74D";
    public const string GlyphDuplicate = "\uE8C8";
    public const string GlyphSearch = "\uE721";

    // ---------------------------------------------------------------- overview
    public const string OverviewTitle = "Overview";
    public const string CardService = "Service";
    public const string CardAgent = "Agent connection";
    public const string CardPending = "Pending updates";
    public const string CardConfiguration = "Configuration summary";

    public const string ServiceNotInstalled = "Not installed";
    public const string ServiceInstalled = "Installed";
    public const string LabelState = "State";
    public const string LabelStartType = "Start type";
    public const string LabelAccount = "Account";
    public const string LabelVersion = "Version";
    public const string LabelExecutable = "Executable";
    public const string LabelConfigurationKey = "Configuration key";

    public const string ButtonStart = "Start";
    public const string ButtonStop = "Stop";
    public const string ButtonRestart = "Restart";
    public const string ButtonScanNow = "Scan now";
    public const string ButtonOpenLogFolder = "Open log folder";
    public const string ButtonOpenStateFolder = "Open state folder";
    public const string ButtonRefresh = "Refresh";

    public const string TipStart = "Start the Arkimentum AppMonitor service";
    public const string TipStop = "Stop the Arkimentum AppMonitor service";
    public const string TipRestart = "Restart the service so it re-reads its configuration";
    public const string TipScanNow = "Ask the running service to scan for updates now";
    public const string TipOpenLogFolder = "Open the folder holding the service log files";
    public const string TipOpenStateFolder = "Open the folder holding state.json and downloaded installers";
    public const string TipRefresh = "Re-read the service state and the registry";

    public const string ServiceNotInstalledHint =
        "The ArkimentumAppMonitor service is not installed on this machine. Settings written here take effect as soon as it is.";
    public const string ServiceControlDisabledHint = "Service control is disabled in --user-config testing mode.";

    public const string AgentConnected = "Connected";
    public const string AgentDisconnected = "Not connected";
    public const string AgentDisconnectedHint =
        "The named pipe Arkimentum.AppMonitor.Agent is not answering. The service is probably stopped or not installed.";
    public const string LabelLastScan = "Last scan";
    public const string LabelNextScan = "Next scan";
    public const string LabelMonitoredApps = "Monitored applications";
    public const string LabelServiceVersion = "Service version";
    public const string None = "—";
    public const string ScanRequested = "Scan requested.";
    public const string ScanRequestFailed = "The service did not accept the scan request.";

    // ---------------------------------------------------------------- prerequisites
    public const string CardPrerequisites = "Prerequisites";
    public const string PrerequisitesUnknown =
        "The service has not reported a prerequisite check yet. Connect to a running service to see the winget state.";
    public const string PrerequisitesHealthy = "Healthy";
    public const string PrerequisitesUnhealthy = "Needs attention";
    public const string LabelWingetVersion = "winget version";
    public const string LabelWingetPath = "winget path";
    public const string LabelMinimumVersion = "Minimum version";
    public const string LabelAppInstaller = "App Installer provisioned";
    public const string LabelCheckedAt = "Last checked";
    public const string LabelLastAction = "Last action";
    public const string LabelLastError = "Last error";
    public const string LabelAutoInstall = "Automatic installation";
    public const string ButtonRepairPrerequisites = "Install or repair prerequisites";
    public const string TipRepairPrerequisites =
        "Ask the service to install or repair the Windows Package Manager as SYSTEM.";
    public const string RepairRunning = "A repair is running…";
    public const string RepairRequestFailed = "The service did not accept the repair request.";
    public const string ButtonUpdateThisAgent = "Update agent";
    public const string TipUpdateThisAgent =
        "Ask the service to check the release feed and install a newer AppMonitor release as SYSTEM. Refused when AgentAutoUpdate is off.";
    public const string AgentUpdateRunning = "An agent update is running…";
    public const string AgentUpdateRequestFailed = "The service did not accept the agent update request.";
    public const string Unknown = "Unknown";
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";

    public const string ColumnApplication = "Application";
    public const string ColumnVersions = "Versions";
    public const string ColumnState = "State";
    public const string ColumnMandatory = "Mandatory";
    public const string ColumnDeadline = "Deadline";
    public const string ColumnDeferrals = "Deferrals";
    public const string ColumnContext = "Context";
    public const string NoPendingUpdates = "No updates are pending.";
    public const string Yes = "Yes";
    public const string No = "No";

    public const string LabelAppsConfigured = "Applications configured";
    public const string LabelAppsLocked = "Locked by policy";
    public const string LabelLogLevel = "Log level";
    public const string LabelScanInterval = "Scan interval";
    public const string ConfigurationWarnings = "Problems found in the saved configuration";
    public const string ConfigurationOk = "No problems found in the saved configuration.";

    public static string MinutesText(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

    public static string AppsCount(int count, int policy) =>
        policy > 0 ? $"{count} ({policy} from policy)" : count.ToString();

    // ---------------------------------------------------------------- settings
    public const string SettingsTitle = "Settings";
    public const string SettingsSubtitle =
        "Machine-wide settings of the update agent. Values not overridden here use the built-in default.";
    public const string ShowAdvanced = "Show advanced settings";
    public const string Override = "Override";
    public const string TipOverride = "Write this value to the registry. Unchecked, the agent uses the default.";
    public const string BadgePolicy = "Policy";
    public const string BadgePreference = "Preference";
    public const string BadgeDefault = "Default";
    public const string PolicyLockedTooltip = "Managed by Group Policy / Intune — cannot be changed here.";

    public static string PolicyValueTooltip(string value) => $"Policy value: {value}\n{PolicyLockedTooltip}";

    // The organization configuration from the cloud sits above the local preferences: a value it sets is read-only here.
    public static string OrganizationLockedTooltip(string? organization) => string.IsNullOrWhiteSpace(organization)
        ? "Managed by your organization — cannot be changed here."
        : $"Managed by the organization {organization} — cannot be changed here.";

    public static string OrganizationValueTooltip(string value, string? organization) =>
        $"Organization value: {value}\n{OrganizationLockedTooltip(organization)}";

    public static string OrganizationAppHint(string? organization) => string.IsNullOrWhiteSpace(organization)
        ? "This application comes from the organization configuration and cannot be changed here. Edit it under Organization → Applications."
        : $"This application comes from the configuration of {organization} and cannot be changed here. Edit it under Organization → Applications.";

    public static string DefaultHint(string value) => string.IsNullOrEmpty(value) ? "Default: (empty)" : $"Default: {value}";

    public static string InheritedHint(string value, string from) =>
        string.IsNullOrEmpty(value) ? $"{from}: (empty)" : $"{from}: {value}";

    public const string InheritedFromCatalog = "Catalog";
    public const string InheritedFromGlobal = "Global default";
    public const string InheritedFromBuiltIn = "Default";

    public static string RangeHint(int min, int max) => $"{min}–{max}";

    public const string IntListHint = "Comma-separated minutes, e.g. 60,240,1440";
    public const string ProcessNamesHint = "One process name per line, without .exe";
    public const string Browse = "Browse…";
    public const string TipBrowse = "Pick a file or folder";

    public const string ButtonApply = "Apply";
    public const string ButtonDiscard = "Discard";
    public const string UnsavedChanges = "Unsaved changes";
    public const string NoChanges = "No changes";
    public const string SavedNotice = "Saved. The service applies changes within about a minute.";
    public const string SavedScanLink = "Scan now";

    public static string ValidationSummary(int count) =>
        count == 1 ? "1 problem prevents saving" : $"{count} problems prevent saving";

    public const string ConfirmDiscardTitle = "Discard unsaved changes?";
    public const string ConfirmDiscardBody =
        "The configuration has unsaved changes. Leaving this page discards them.";
    public const string ConfirmDiscardCloseBody =
        "The configuration has unsaved changes. Closing the console discards them.";
    public const string Discard = "Discard";
    public const string Cancel = "Cancel";
    public const string KeepEditing = "Keep editing";

    public static string WriteFailed(string message) => $"The configuration could not be written: {message}";

    // ---------------------------------------------------------------- applications
    public const string ApplicationsTitle = "Applications";
    public const string FilterPlaceholder = "Filter applications";
    public const string AddFromCatalog = "Add from catalog…";
    public const string AddCustom = "Add custom…";
    public const string Duplicate = "Duplicate";
    public const string Remove = "Remove";
    public const string TipAddFromCatalog = "Pick applications from the shipped catalog";
    public const string TipAddCustom = "Add an application by AppId and configure it by hand";
    public const string TipDuplicate = "Copy the selected application to a new AppId";
    public const string TipRemove = "Remove the selected application from the configuration";
    public const string NoAppsConfigured = "No applications are configured yet.";
    public const string NoAppSelected = "Select an application on the left, or add one.";
    public const string AppEnabled = "Enabled";
    public const string AppDisabled = "Disabled";
    public const string BadgeMandatory = "Mandatory";
    public const string PolicyAppHint =
        "This application comes from the policy layer and cannot be changed here. Unassign the policy to edit it.";

    public const string TestDetection = "Test detection";
    public const string TestDetectionRunning = "Testing…";
    public const string TestDetectionCaption =
        "Runs the inventory scan and the configured provider for this application only, first machine-wide and then for " +
        "the current user. The test runs as the current administrator, not as SYSTEM, so per-machine winget results may " +
        "differ slightly from the service's.";
    public const string TestDetectionUnsavedCaption =
        "This application is not in the registry yet; the test uses the values currently in the editor.";
    public const string LabelInstalledVersion = "Installed version";
    public const string LabelAvailableVersion = "Available version";
    public const string LabelDetectionResult = "Result";
    public const string DetectionNotInstalled = "Not installed (checked the machine-wide and the current user's context)";
    public const string DetectionUpToDate = "Up to date";
    public const string DetectionUpdateAvailable = "An update is available";

    public static string DetectionFailed(string error) => $"Failed: {error}";
    public static string DetectionContextSuffix(string? context) =>
        context is null ? string.Empty : context == "User" ? " (installed for the current user)" : " (installed machine-wide)";

    public const string AddCustomTitle = "Add an application";
    public const string AddCustomPrompt = "AppId (letters, digits, dot, dash and underscore only)";
    public const string AddCustomInvalid = "Use letters, digits, '.', '-' and '_' only.";
    public const string AddCustomExists = "An application with that AppId is already configured.";
    public const string AddCustomEmpty = "Enter an AppId.";
    public const string DuplicateTitle = "Duplicate the application";
    public const string ButtonAdd = "Add";
    public const string ButtonOk = "OK";
    public const string ButtonClose = "Close";

    public const string CatalogDialogTitle = "Add from catalog";
    public const string CatalogDialogSubtitle =
        "Catalog entries supply identity, source and detection data. Adding one configures the application with Enabled = 1 " +
        "only, so the catalog stays in charge of the details.";
    public const string CatalogEmpty = "No catalog entries are available, or all of them are already configured.";
    public const string CatalogNotFound = "catalog.json was not found; add applications by AppId instead.";

    public static string CatalogSelected(int count) => count == 1 ? "1 selected" : $"{count} selected";

    // ---------------------------------------------------------------- discovery
    public const string DiscoverInstalled = "Discover installed…";
    public const string TipDiscoverInstalled = "List the applications installed on this machine and pick the ones to monitor";
    public const string DiscoverTitle = "Discover installed applications";
    public const string DiscoverSubtitle =
        "The registry inventory merged with what winget reports. Entries the catalog recognises are added by catalog id, " +
        "so the shipped identity, source and detection data apply; the rest are added as winget applications.";
    public const string DiscoverStarting = "Starting…";
    public const string DiscoverOnlyAddable = "Only apps that can be added";
    public const string DiscoverSelectAll = "Select all";
    public const string DiscoverSelectNone = "Select none";
    public const string ButtonAddSelected = "Add selected";
    public const string DiscoverEmpty = "Nothing was found. Check that winget works for this account.";
    public const string DiscoverNoMatches = "No application matches the filter.";
    public const string DiscoverCancelled = "Discovery was cancelled.";
    public const string BadgeInCatalog = "In catalog";
    public const string NoWingetPackage = "no winget package";
    public const string WingetIdTruncated = "winget shortened the package id; add this one by hand";

    public static string DiscoverFailed(string message) => $"Discovery failed: {message}";

    public static string BadgeUpdateAvailable(string version) => $"Update available {version}";

    public static string AlreadyMonitored(string appId) => $"Already monitored ({appId})";

    public static string DiscoverAdded(int added, int skipped)
    {
        var text = added == 1 ? "Added 1 application." : $"Added {added} applications.";
        return skipped == 0 ? text
            : skipped == 1 ? text + " 1 was already in the list and was skipped."
            : text + $" {skipped} were already in the list and were skipped.";
    }

    public const string ConfirmRemoveTitle = "Remove the application?";

    public static string ConfirmRemoveBody(string appId) =>
        $"'{appId}' will be removed from the configuration when you apply the changes.";

    // ---------------------------------------------------------------- export / import
    public const string ExportImportTitle = "Export & import";
    public const string ExportCard = "Export";
    public const string ExportSubtitle = "Exports the saved preference layer — not the unsaved edits in this console.";
    public const string ExportDirtyWarning = "There are unsaved changes. Apply them first if you want them in the export.";
    public const string LabelTargetLayer = "Target layer";
    public const string LayerPreference = "Preference (HKLM\\SOFTWARE\\Arkimentum\\AppMonitor)";
    public const string LayerPolicy = "Policy (HKLM\\SOFTWARE\\Policies\\Arkimentum\\AppMonitor)";
    public const string LabelReplaceApps = "Replace existing applications on import";
    public const string TipReplaceApps =
        "The generated artefact removes application entries that it does not define, so it is authoritative.";
    public const string LabelDescription = "Description";
    public const string LabelFormat = "Format";
    public const string FormatJson = "JSON profile (.json)";
    public const string FormatReg = "Registry file (.reg)";
    public const string FormatPs1 = "PowerShell script (.ps1)";
    public const string FormatJsonCaption = "JSON: re-import with this console on another machine.";
    public const string FormatRegCaption = "Registry file: deploy with reg import, a GPO Preference item or a double-click.";
    public const string FormatPs1Caption = "PowerShell: run as an Intune platform script or Win32 app, or from an RMM.";
    public const string ButtonSaveAs = "Save as…";
    public const string ButtonCopy = "Copy";
    public const string CopiedNotice = "Copied to the clipboard.";

    public static string ExportedNotice(string path) => $"Saved to {path}";

    public const string ImportCard = "Import";
    public const string ImportSubtitle = "Reads a JSON profile and writes it to the preference layer.";
    public const string ButtonOpenProfile = "Open JSON profile…";
    public const string LabelMode = "Mode";
    public const string ModeReplace = "Replace — the preference layer becomes exactly the profile";
    public const string ModeMerge = "Merge — only the values in the profile are written";
    public const string ButtonImport = "Import";
    public const string ImportNoFile = "No profile loaded.";
    public const string ImportValid = "The profile is valid.";
    public const string ImportProblems = "The profile cannot be imported";
    public const string ImportSummary = "Changes compared with the saved preference layer";
    public const string ImportNoChanges = "The profile matches the saved preference layer; nothing would change.";
    public const string ImportedNotice = "Imported. The service applies changes within about a minute.";

    public static string ImportedFrom(string path) => $"Loaded {path}";

    public static string ReadFailed(string message) => $"The file could not be read: {message}";

    public const string SummaryGlobalsAdded = "Global settings added";
    public const string SummaryGlobalsChanged = "Global settings changed";
    public const string SummaryGlobalsRemoved = "Global settings removed";
    public const string SummaryAppsAdded = "Applications added";
    public const string SummaryAppsChanged = "Applications changed";
    public const string SummaryAppsRemoved = "Applications removed";

    // ---------------------------------------------------------------- about
    public const string AboutTitle = "About " + ProductName;
    public const string AboutDescription =
        "The administrative console of Arkimentum AppMonitor: it edits the registry configuration the service reads, " +
        "and packages it for Group Policy, Intune or an RMM.";
    public const string AboutCommandLine = "Command line";
    public const string AboutSwitchNone = "(no switches) — open the console.";
    public const string AboutSwitchExport =
        "--export <file> [--policy] [--no-replace-apps] — export the preference layer headlessly; the format comes from the extension (.json, .reg, .ps1).";
    public const string AboutSwitchImport =
        "--import <file.json> [--merge] — import a JSON profile into the preference layer headlessly (Replace unless --merge).";
    public const string AboutSwitchUserConfig =
        "--user-config — testing only: read and write HKCU instead of HKLM and keep logs under %LOCALAPPDATA%; service control is disabled.";
    public const string AboutLogFolder = "Log folder";
    public const string AboutOpen = "Open";

    public static string AboutVersion(string version) => $"Version {version}";

    // =================================================================================================== organization
    // Everything below belongs to the cloud ("Organization") half of the console. The local pages above are
    // unchanged; the two halves never share a string whose wording differs between them.

    // ---------------------------------------------------------------- navigation and scope
    public const string NavGroupLocal = "This machine";
    public const string NavGroupOrganization = "Organization";
    public const string NavConnect = "Connect";
    public const string NavDevices = "Devices";
    public const string NavActivity = "Activity";
    public const string NavInventory = "Inventory";
    // Short rail labels; the "Organization" group header above them supplies the rest. The full names are
    // the page titles, and the automation names of the rail entries.
    public const string NavOrganizationSettings = "Settings";
    public const string NavOrganizationApplications = "Applications";
    public const string NavEnrollment = "Enrollment";

    public const string GlyphCloud = "\uE753";        // Cloud
    public const string GlyphConnect = "\uE71B";      // Link
    public const string GlyphDevices = "\uE772";      // Devices
    public const string GlyphActivity = "\uE9D9";     // Diagnostic
    public const string GlyphInventory = "\uE71D";    // AllApps
    public const string GlyphOrgSettings = "\uE713";  // Settings
    public const string GlyphEnrollment = "\uEB95";   // Certificate
    public const string GlyphSignOut = "\uF3B1";      // Leave
    public const string GlyphPerson = "\uE77B";       // Contact
    public const string GlyphDelete = "\uE74D";       // Delete
    public const string GlyphHistory = "\uE81C";      // History
    public const string GlyphShow = "\uE7B3";         // RedEye
    public const string GlyphHide = "\uED1A";         // Hide

    public const string ScopeLocal = "Local machine";
    public const string ScopeLocalTip = "The pages in this group edit this machine's registry configuration.";

    public static string ScopeOrganization(string name) => $"Organization: {name}";

    public const string ScopeOrganizationNone = "Organization: not connected";
    public const string ScopeOrganizationTip =
        "The pages in this group edit the organization's configuration in the AppMonitor cloud, not this machine.";

    // ---------------------------------------------------------------- connect / sign in
    public const string ConnectTitle = "Connect to the AppMonitor cloud";
    public const string ConnectSubtitle =
        "Sign in with your work account to manage an organization's configuration and see its devices. " +
        "Nothing on the local pages changes while you are signed in.";
    public const string LabelServerUrl = "Cloud server URL";
    public const string ServerUrlHint = "For example https://appmonitor.contoso.com";
    public const string ServerUrlFromMachine = "Pre-filled from this machine's CloudServerUrl.";
    public const string ServerUrlRemembered = "Remembered from your last sign-in on this machine.";
    public const string ButtonConnect = "Connect";
    public const string ButtonSignIn = "Sign in";
    public const string ButtonSignOut = "Sign out";
    public const string TipConnect = "Read the server's sign-in configuration";
    public const string TipSignIn = "Sign in with Entra ID in your default browser";
    public const string TipSignOut = "Forget the cached token for this machine's user";
    public const string ConnectWorking = "Working…";
    public const string ConnectRestoring = "Restoring your previous sign-in…";
    public const string ConnectNotConnected = "Not connected.";
    public const string CloudSignInRequired = "Sign in to continue.";
    public const string CloudCancelled = "The operation was cancelled.";
    public const string CardSignIn = "Sign-in";
    public const string CardOrganizations = "Organizations";
    public const string LabelSignedInAs = "Signed in as";
    public const string LabelTenant = "Tenant";
    public const string LabelAuthority = "Authority";
    public const string LabelScope = "Scope";
    public const string LabelClientId = "Application (client) id";
    public const string OrganizationsEmpty = "Your account does not administer any organization on this server.";
    public const string OrganizationPickPrompt = "Choose the organization to manage.";
    public const string BadgeGlobalAdmin = "Global administrator";
    public const string ButtonUseOrganization = "Manage this organization";
    public const string ButtonReloadOrganizations = "Reload";
    public const string ButtonCreateOrganization = "New organization…";
    public const string TipCreateOrganization =
        "Create an organization on this server. Global administrators only; the enrollment key is shown once afterwards.";
    public const string CreateOrganizationTitle = "Create an organization";
    public const string CreateOrganizationPrompt = "Name of the organization";
    public const string CreateOrganizationEmpty = "Enter a name.";
    public const string ButtonCreate = "Create";

    public static string OrganizationCreated(string name) =>
        $"'{name}' was created and selected. Its enrollment key is on the Enrollment page — it is shown only once.";

    public static string CloudNotAuthorised(string message) =>
        $"The server refused the request: {message}";

    public static string CloudUnreachable(string message) => $"The server could not be reached: {message}";

    public static string ConnectedTo(string url) => $"Connected to {url}.";

    public static string OrganizationDeviceSummary(int devices, int pending, int stale) =>
        $"{devices} device{(devices == 1 ? "" : "s")} · {pending} with updates · {stale} not seen in 7 days";

    public const string SignInFirst = "Sign in on the Connect page to use the organization pages.";

    // ---------------------------------------------------------------- relative time
    public const string RelativeJustNow = "just now";

    public static string RelativeAgo(string span) => $"{span} ago";

    // ---------------------------------------------------------------- devices
    public const string DevicesTitle = "Devices";
    public const string DevicesSubtitle = "Every device enrolled in this organization.";
    public const string DevicesFilterPlaceholder = "Search by name or user";
    public const string DevicesEmpty = "No device matches the search.";
    public const string NoDeviceSelected = "Select a device on the left.";
    public const string BadgeStale = "Not seen in 7 days";
    public const string BadgePrerequisites = "winget needs attention";
    public const string LabelUser = "Last logon user";
    public const string LabelOs = "Operating system";
    public const string LabelAgentVersion = "Agent version";
    public const string LabelLastSeen = "Last seen";
    public const string LabelEnrolled = "Enrolled";
    public const string LabelConfigVersion = "Configuration version";
    public const string LabelDeviceId = "Device id";
    public const string LabelMachineGuid = "Machine GUID";
    public const string CardDeviceSummary = "Device";
    public const string CardInstalledApps = "Installed applications";
    public const string CardTrackedUpdates = "Tracked updates";
    public const string CardRecentEvents = "Recent events";
    public const string CardPendingCommands = "Pending commands";
    public const string InstalledAppsFilter = "Filter installed applications";
    public const string InstalledAppsEmpty = "The device has not reported any installed application yet.";
    public const string TrackedUpdatesEmpty = "No update is being tracked for this device.";
    public const string RecentEventsEmpty = "No event has been reported yet.";
    public const string PendingCommandsEmpty = "No command is waiting for this device.";
    public const string ColumnPublisher = "Publisher";
    public const string ColumnWingetId = "winget id";
    public const string ColumnVersion = "Version";
    public const string ColumnAvailable = "Available";
    public const string ColumnDevices = "Devices";
    public const string ColumnWhen = "When";
    public const string ColumnEvent = "Event";
    public const string ColumnMessage = "Message";
    public const string ColumnCommand = "Command";
    public const string ColumnIssuedBy = "Issued by";

    public const string ButtonScanNowDevice = "Scan now";
    public const string ButtonReportNow = "Report now";
    public const string ButtonRepairDevice = "Repair prerequisites";
    public const string ButtonUpdateAgent = "Update agent";
    public const string ButtonDeleteDevice = "Delete device";
    public const string TipScanNowDevice = "Queue a scan; the device runs it at its next sync";
    public const string TipReportNow = "Queue a fresh inventory report";
    public const string TipRepairDevice = "Queue an install or repair of the Windows Package Manager";
    public const string TipUpdateAgent = "Queue an agent self-update to the current release";
    public const string TipDeleteDevice = "Remove the device and its reports from the organization";
    public const string ConfirmDeleteDeviceTitle = "Delete the device?";
    public const string ButtonDelete = "Delete";

    public static string ConfirmDeleteDeviceBody(string name) =>
        $"'{name}' and everything it has reported are removed from the organization. " +
        "If the agent is still installed and running, the device enrols again on its next sync.";

    public static string DeviceDeleted(string name) => $"{name} was deleted.";

    public static string CommandQueued(string command) => $"{command} was queued for the device.";

    public static string DevicesPageOf(int page, int pages, int total) =>
        $"Page {page} of {pages} · {total} device{(total == 1 ? "" : "s")}";

    public const string ButtonPreviousPage = "Previous";
    public const string ButtonNextPage = "Next";
    public const string PendingCountsLabel = "pending";
    public const string FailedCountsLabel = "failed";

    public static string PendingAndFailed(int pending, int failed) =>
        failed > 0 ? $"{pending} pending · {failed} failed" : $"{pending} pending";

    // ---------------------------------------------------------------- activity
    public const string ActivityTitle = "Activity";
    public const string ActivitySubtitle =
        "What the organization's devices have actually done: installs, deferrals and repairs, newest first.";
    public const string ActivityEmpty = "No event has been reported by this organization yet.";
    public const string ColumnDevice = "Device";

    public static string ActivityPageOf(int page, int pages, int total) =>
        $"Page {page} of {pages} · {total} event{(total == 1 ? "" : "s")}";

    // ---------------------------------------------------------------- inventory
    public const string InventoryTitle = "Inventory";
    public const string InventorySubtitle =
        "Every application the organization's devices have reported. Add the ones you want to keep up to date " +
        "to the organization configuration.";
    public const string InventoryFilterPlaceholder = "Filter applications";
    public const string InventoryOnlyUnmonitored = "Only unmonitored";
    public const string InventoryEmpty = "No application matches the filter.";
    public const string BadgeMonitored = "Monitored";
    public const string ButtonAddToOrganization = "Add to organization configuration";
    public const string TipAddToOrganization =
        "Adds the ticked applications to the organization configuration. Publish on the Organization applications page to apply them.";
    public const string InventoryNothingSelected = "Tick one or more applications first.";

    public static string InventoryUpdatesAvailable(int devices) =>
        devices == 1 ? "1 device has an update" : $"{devices} devices have an update";

    public static string InventoryDeviceCount(int devices) =>
        devices == 1 ? "1 device" : $"{devices} devices";

    public static string InventoryVersions(IEnumerable<string> parts) => string.Join(" · ", parts);

    public static string InventoryAdded(int added, int skipped) => DiscoverAdded(added, skipped);
    public const string InventoryConfigurationNotLoaded = "The organization configuration could not be loaded, so nothing was added. Open the Applications page, resolve the error shown there and try again.";

    // ---------------------------------------------------------------- organization configuration
    public const string OrganizationSettingsTitle = "Organization settings";
    public const string OrganizationSettingsSubtitle =
        "Settings every device in this organization applies. Values not overridden here fall back to the device's " +
        "own configuration and then to the built-in default.";
    public const string OrganizationApplicationsTitle = "Organization applications";
    public const string OrganizationApplicationsSubtitle =
        "Applications every device in this organization monitors.";
    public const string BadgeOrganization = "Organization";
    public const string ButtonPublish = "Publish";
    public const string ButtonReload = "Reload";
    public const string TipPublish = "Write the configuration to the organization; devices pick it up at their next sync";
    public const string OrganizationNotLoaded = "The organization configuration has not been loaded yet.";
    public const string OrganizationLoading = "Loading the organization configuration…";
    public const string PublishedNotice = "Published. Devices apply the change at their next sync.";
    public const string PublishCommentPrompt = "Describe the change (optional; shown in the version history).";
    public const string PublishCommentTitle = "Publish the organization configuration";
    public const string ConfirmDiscardOrganizationBody =
        "The organization configuration has unsaved changes. Leaving this page discards them.";

    public static string OrganizationConfigVersion(string version) => $"Version {version}";

    public static string OrganizationUpdatedBy(string? who, string when) =>
        string.IsNullOrWhiteSpace(who) ? when : $"{when} by {who}";

    public const string ConflictTitle = "The organization configuration changed";
    public const string ConflictBody =
        "Somebody else published a new version while you were editing. Reload to take their version (your edits are " +
        "lost), or overwrite to publish yours on top of theirs.";
    public const string ConflictReload = "Reload";
    public const string ConflictOverwrite = "Overwrite";
    public const string OverwroteNotice = "Published over the newer version.";

    public const string CardVersionHistory = "Version history";
    public const string VersionHistoryEmpty = "This organization has no earlier configuration versions.";
    public const string VersionHistoryCaption =
        "Who changed what, and why. Restoring loads that version into the editor; publishing it then adds a new " +
        "version on top of the current one, so nothing in the history is ever rewritten.";
    public const string ButtonRestoreVersion = "Restore this version";
    public const string TipRestoreVersion =
        "Load this version into the editor. Nothing is published until you press Publish.";

    public static string RestoredNotice(string version) =>
        $"Loaded version {version} into the editor. Review it and publish to make it current.";

    public static string RestoredFromVersion(string version) => $"Restored from version {version}";

    public static string VersionHistoryCount(int shown, int total) =>
        total > shown ? $"{shown} of {total} versions" : total == 1 ? "1 version" : $"{total} versions";

    // ---------------------------------------------------------------- enrollment
    public const string EnrollmentTitle = "Enrollment";
    public const string EnrollmentSubtitle =
        "What a device needs to join this organization: the server URL, the organization id and the enrollment key. " +
        "Deploy them with any of the snippets below; the agent exchanges the key for its own device key once and " +
        "never uses it again.";
    public const string CardEnrollmentValues = "Enrollment values";
    public const string LabelOrganizationId = "Organization id";
    public const string LabelEnrollmentKey = "Enrollment key";
    public const string LabelKeyRotated = "Key last rotated";
    public const string KeyHidden = "••••••••••••••••••••••••";
    public const string KeyNotShown =
        "The server only returns the key when it is created or rotated. Rotate it to see a new one, or use a snippet " +
        "below and paste the key you stored.";
    public const string ButtonShowKey = "Show";
    public const string ButtonHideKey = "Hide";
    public const string ButtonRotateKey = "Rotate key";
    public const string TipRotateKey = "Issue a new enrollment key; the old one stops working immediately";
    public const string ConfirmRotateTitle = "Rotate the enrollment key?";
    public const string ConfirmRotateBody =
        "The current key stops working at once. Devices that are already enrolled keep working — they use their own " +
        "device key — but every provisioning package, script and Intune app that still carries the old key must be " +
        "updated. The new key is shown once.";
    public const string ButtonRotate = "Rotate";
    public const string KeyRotatedNotice = "A new enrollment key was issued. Copy it now — it is shown only this once.";
    public const string KeyIssuedNotice =
        "This is the new organization's enrollment key. Copy it now — the server never returns it again.";
    public const string CardSnippets = "Provisioning snippets";
    public const string SnippetPowerShell = "PowerShell for Intune";
    public const string SnippetPowerShellCaption =
        "Platform script or Win32 app, run as SYSTEM. Re-launches itself in the 64-bit host so HKLM is not redirected.";
    public const string SnippetReg = "Registry file";
    public const string SnippetRegCaption = "reg import, a Group Policy Preference item or a double-click.";
    public const string SnippetInstaller = "Installer command line";
    public const string SnippetInstallerCaption = "A fresh install that enrols on first start.";
    public const string SnippetDetection = "Intune Win32 detection";
    public const string SnippetDetectionCaption = "The detection rule that proves the enrolment values landed.";

    public static string SnippetWritesTo(string path) => $"Writes {path}";

    // ---------------------------------------------------------------- overview card
    public const string CardOrganizationStatus = "Organization";
    public const string CloudNotConnected = "Not connected";
    public const string CloudNotConnectedHint =
        "This machine is not enrolled in an organization. Deploy the enrollment values to connect it.";
    public const string LabelOrganization = "Organization";
    public const string LabelCloudServer = "Server";
    public const string LabelLastConfig = "Configuration applied";
    public const string LabelLastReport = "Last report";
    public const string BadgeConnected = "Connected";

    // ---------------------------------------------------------------- headless
    public const string HeadlessErrorTitle = ProductName;

    public static string HeadlessExportOk(string path) => $"Exported the preference layer to {path}.";

    public static string HeadlessExportFailed(string message) => $"Export failed: {message}";

    public static string HeadlessImportOk(string path, int globals, int apps) =>
        $"Imported {path}: {globals} global value(s), {apps} application(s).";

    public static string HeadlessImportFailed(string message) => $"Import failed: {message}";

    public const string HeadlessUnknownFormat =
        "Unknown export format. Use a file name ending in .json, .reg or .ps1.";

    public const string HeadlessMissingFile = "The file was not found.";
}

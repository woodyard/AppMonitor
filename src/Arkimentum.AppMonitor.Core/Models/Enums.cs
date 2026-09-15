namespace Arkimentum.AppMonitor.Models;

/// <summary>Where an application is installed / where its update must run.</summary>
public enum InstallContext
{
    /// <summary>Detect automatically from the installed-app inventory (machine hive vs. user hive).</summary>
    Auto = 0,
    /// <summary>Machine-wide install; update executed by the service (LocalSystem).</summary>
    System = 1,
    /// <summary>Per-user install; update executed by the tray agent in the user's session.</summary>
    User = 2,
}

/// <summary>Where update information and installers come from.</summary>
public enum UpdateSource
{
    /// <summary>Windows Package Manager (winget).</summary>
    Winget = 0,
    /// <summary>Vendor's official web site (version URL + download URL).</summary>
    Web = 1,
}

public enum InstallerType
{
    Exe = 0,
    Msi = 1,
    Msix = 2,
}

/// <summary>Lifecycle of a detected update.</summary>
public enum UpdateState
{
    /// <summary>Update detected; user has not acted yet.</summary>
    Available = 0,
    /// <summary>User deferred the update until <see cref="PendingUpdate.DeferredUntilUtc"/>.</summary>
    Deferred = 1,
    /// <summary>Install has been requested and is queued.</summary>
    Scheduled = 2,
    /// <summary>Blocking processes are running; waiting for the user to close them.</summary>
    WaitingForClose = 3,
    Installing = 4,
    Installed = 5,
    Failed = 6,
}

/// <summary>
/// How insistent the agent is about a pending update. The default is <see cref="Quiet"/>: users found one toast per
/// update per notification interval intrusive, so an update is announced once and only things that actually need the
/// user (deadline, close prompt, failure) are allowed to interrupt again.
/// </summary>
public enum NotificationMode
{
    /// <summary>Announce an update once, then stay silent unless the user has to act.</summary>
    Quiet = 0,
    /// <summary>Repeat the reminder for a pending update every notification interval.</summary>
    Reminders = 1,
}

public enum NotificationKind
{
    UpdateAvailable = 0,
    DeadlineApproaching = 1,
    CloseApplications = 2,
    Installing = 3,
    Installed = 4,
    Failed = 5,
    Info = 6,
}

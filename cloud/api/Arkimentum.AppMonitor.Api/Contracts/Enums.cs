namespace Arkimentum.AppMonitor.Api.Contracts;

// =====================================================================================================================
// Ported verbatim from src/Arkimentum.AppMonitor.Core (Models/Enums.cs, Cloud/CloudContracts.cs). The API cannot
// reference Core because Core targets net10.0-windows; the numeric values and the names must therefore stay identical.
// Arkimentum.AppMonitor.Api.Tests round-trips every DTO against the Core types to guarantee that.
// =====================================================================================================================

/// <summary>Where an application is installed / where its update must run.</summary>
public enum InstallContext
{
    Auto = 0,
    System = 1,
    User = 2,
}

/// <summary>Where update information and installers come from.</summary>
public enum UpdateSource
{
    Winget = 0,
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
    Available = 0,
    Deferred = 1,
    Scheduled = 2,
    WaitingForClose = 3,
    Installing = 4,
    Installed = 5,
    Failed = 6,
}

public enum DeviceCommandKind
{
    ScanNow,
    RepairPrerequisites,
    UpdateAgent,
    ReportNow,
}

public enum ReportedEventKind
{
    UpdateDetected,
    InstallSucceeded,
    InstallFailed,
    Deferred,
    ForcedClose,
    PrerequisiteRepaired,
    AgentUpdated,
}

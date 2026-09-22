using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// How the wire enums read on screen. Kept in one place so an event says the same thing on the Overview page, the
/// Activity page and inside a device.
/// </summary>
public static class Display
{
    public static string Event(ReportedEventKind kind) => kind switch
    {
        ReportedEventKind.UpdateDetected => "Update detected",
        ReportedEventKind.InstallSucceeded => "Installed",
        ReportedEventKind.InstallFailed => "Install failed",
        ReportedEventKind.Deferred => "Deferred",
        ReportedEventKind.ForcedClose => "Forced close",
        ReportedEventKind.PrerequisiteRepaired => "Prerequisite repaired",
        ReportedEventKind.AgentUpdated => "Agent updated",
        _ => kind.ToString(),
    };

    /// <summary>The tag colour an event carries: attention for failures, olive for successes, quiet otherwise.</summary>
    public static string EventTone(ReportedEventKind kind) => kind switch
    {
        ReportedEventKind.InstallFailed => "danger",
        ReportedEventKind.InstallSucceeded or ReportedEventKind.AgentUpdated or ReportedEventKind.PrerequisiteRepaired => "ok",
        ReportedEventKind.ForcedClose => "warn",
        _ => "quiet",
    };

    public static string UpdateState(UpdateState state) => state switch
    {
        Api.Contracts.UpdateState.Available => "Available",
        Api.Contracts.UpdateState.Deferred => "Deferred",
        Api.Contracts.UpdateState.Scheduled => "Scheduled",
        Api.Contracts.UpdateState.WaitingForClose => "Waiting for close",
        Api.Contracts.UpdateState.Installing => "Installing",
        Api.Contracts.UpdateState.Installed => "Installed",
        Api.Contracts.UpdateState.Failed => "Failed",
        _ => state.ToString(),
    };

    public static string UpdateTone(UpdateState state) => state switch
    {
        Api.Contracts.UpdateState.Failed => "danger",
        Api.Contracts.UpdateState.Installed => "ok",
        Api.Contracts.UpdateState.WaitingForClose or Api.Contracts.UpdateState.Deferred => "warn",
        _ => "quiet",
    };

    public static string Context(InstallContext context) => context switch
    {
        InstallContext.System => "machine-wide",
        InstallContext.User => "per user",
        _ => "auto",
    };

    public static string Command(DeviceCommandKind kind) => kind switch
    {
        DeviceCommandKind.ScanNow => "Scan now",
        DeviceCommandKind.ReportNow => "Report now",
        DeviceCommandKind.RepairPrerequisites => "Repair prerequisites",
        DeviceCommandKind.UpdateAgent => "Update agent",
        _ => kind.ToString(),
    };

    /// <summary>"None" rather than an empty cell, so a table never looks broken.</summary>
    public static string OrNone(string? value) => string.IsNullOrWhiteSpace(value) ? "None" : value!;
}

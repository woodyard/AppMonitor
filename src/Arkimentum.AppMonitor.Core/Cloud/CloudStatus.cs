namespace Arkimentum.AppMonitor.Cloud;

/// <summary>
/// Contents of <c>&lt;StateDirectory&gt;\cloud-status.json</c>: written by the service's cloud sync, read by the admin
/// console's Overview page. Never contains secrets.
/// </summary>
public sealed class CloudStatus
{
    public string? ServerUrl { get; set; }
    public string? OrganizationName { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTimeOffset? EnrolledUtc { get; set; }
    public string? LastConfigVersion { get; set; }
    public DateTimeOffset? LastConfigUtc { get; set; }
    public DateTimeOffset? LastReportUtc { get; set; }
    public string? LastError { get; set; }

    public const string FileName = "cloud-status.json";
}

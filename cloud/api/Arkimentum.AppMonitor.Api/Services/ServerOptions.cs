using Microsoft.Extensions.Configuration;

namespace Arkimentum.AppMonitor.Api.Services;

/// <summary>
/// Everything the API reads from Function App settings. Nothing here is a secret: the SQL connection uses the managed
/// identity, blob access uses the managed identity, and the Entra values are public identifiers.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Application (client) id of the API app registration, without the api:// prefix.</summary>
    public string ApiClientId { get; init; } = string.Empty;

    /// <summary>Application (client) id of the admin console public client.</summary>
    public string AdminClientId { get; init; } = string.Empty;

    /// <summary>Authority the admin console signs in against; "organizations" for the multi-tenant app.</summary>
    public string TenantIdMode { get; init; } = "organizations";

    /// <summary>Accepted audience values. Entra issues either api://{appId} or the bare {appId} depending on the accessTokenAcceptedVersion.</summary>
    public IReadOnlyList<string> ValidAudiences { get; init; } = [];

    /// <summary>Scope the admin console must present.</summary>
    public string RequiredScope { get; init; } = "AppMonitor.Admin";

    /// <summary>Arkimentum's own tenant. AppMonitor.GlobalAdmin is only honoured for tokens issued by this tenant.</summary>
    public string OperatorTenantId { get; init; } = string.Empty;

    /// <summary>Public base URL of this API; handed to devices as the third registry value.</summary>
    public string PublicServerUrl { get; init; } = string.Empty;

    /// <summary>Value of DeviceConfigResponse.PollIntervalSeconds.</summary>
    public int PollIntervalSeconds { get; init; } = 900;

    /// <summary>https://{account}.blob.core.windows.net - empty disables the raw report archive.</summary>
    public string? BlobServiceUri { get; init; }

    /// <summary>Container for the raw device report archive.</summary>
    public string ReportContainer { get; init; } = "reports";

    /// <summary>Enrollments allowed per IP address per <see cref="EnrollWindowMinutes"/>.</summary>
    public int EnrollLimitPerIp { get; init; } = 20;

    /// <summary>Enrollments allowed per organization per <see cref="EnrollWindowMinutes"/>.</summary>
    public int EnrollLimitPerOrganization { get; init; } = 200;

    public int EnrollWindowMinutes { get; init; } = 10;

    /// <summary>Events kept per device before the oldest are pruned on write.</summary>
    public int MaxEventsPerDevice { get; init; } = 500;

    public string Authority => $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(TenantIdMode) ? "organizations" : TenantIdMode)}";

    public string ScopeUri => $"api://{ApiClientId}/{RequiredScope}";

    public static ServerOptions FromConfiguration(IConfiguration configuration)
    {
        var apiClientId = configuration["AzureAd:ClientId"] ?? configuration["AzureAd__ClientId"] ?? string.Empty;
        var audience = configuration["AzureAd:Audience"] ?? configuration["AzureAd__Audience"];

        var audiences = new List<string>();
        if (!string.IsNullOrWhiteSpace(audience)) audiences.Add(audience);
        if (!string.IsNullOrWhiteSpace(apiClientId))
        {
            audiences.Add(apiClientId);
            audiences.Add($"api://{apiClientId}");
        }

        return new ServerOptions
        {
            ApiClientId = apiClientId,
            AdminClientId = configuration["AdminClientId"] ?? string.Empty,
            TenantIdMode = configuration["AzureAd:TenantIdMode"] ?? configuration["AzureAd__TenantIdMode"] ?? "organizations",
            ValidAudiences = audiences.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            OperatorTenantId = configuration["OperatorTenantId"] ?? string.Empty,
            PublicServerUrl = (configuration["PublicServerUrl"] ?? string.Empty).TrimEnd('/'),
            PollIntervalSeconds = ReadInt(configuration, "PollIntervalSeconds", 900),
            BlobServiceUri = configuration["BlobServiceUri"],
            ReportContainer = configuration["ReportContainer"] ?? "reports",
            EnrollLimitPerIp = ReadInt(configuration, "EnrollLimitPerIp", 20),
            EnrollLimitPerOrganization = ReadInt(configuration, "EnrollLimitPerOrganization", 200),
            EnrollWindowMinutes = ReadInt(configuration, "EnrollWindowMinutes", 10),
            MaxEventsPerDevice = ReadInt(configuration, "MaxEventsPerDevice", 500),
        };
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;
}

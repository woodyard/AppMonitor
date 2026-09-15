using System.Text;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Api.Services;

public interface IReportArchive
{
    /// <summary>Stores the raw report body at reports/{orgId}/{deviceId}/{yyyy}/{MM}/{timestamp}.json. Never throws.</summary>
    Task ArchiveAsync(Guid organizationId, Guid deviceId, DateTimeOffset reportedUtc, string rawJson, CancellationToken ct);
}

/// <summary>Used when BlobServiceUri is not configured (local development and tests).</summary>
public sealed class NullReportArchive : IReportArchive
{
    public Task ArchiveAsync(Guid organizationId, Guid deviceId, DateTimeOffset reportedUtc, string rawJson, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Writes every accepted device report to Blob Storage with the Function App's managed identity (no connection string).
/// Archiving is best-effort: a storage outage must not make devices retry their reports forever.
/// </summary>
public sealed class BlobReportArchive : IReportArchive
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobReportArchive> _logger;
    private int _containerChecked;

    public BlobReportArchive(ServerOptions options, ILogger<BlobReportArchive> logger)
    {
        _logger = logger;
        var service = new BlobServiceClient(new Uri(options.BlobServiceUri!), new DefaultAzureCredential());
        _container = service.GetBlobContainerClient(options.ReportContainer);
    }

    public async Task ArchiveAsync(Guid organizationId, Guid deviceId, DateTimeOffset reportedUtc, string rawJson, CancellationToken ct)
    {
        try
        {
            if (Interlocked.Exchange(ref _containerChecked, 1) == 0)
                await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

            var utc = reportedUtc.ToUniversalTime();
            var name = $"{organizationId:D}/{deviceId:D}/{utc:yyyy}/{utc:MM}/{utc:yyyyMMddTHHmmssfffZ}.json";
            var blob = _container.GetBlobClient(name);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawJson));
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" },
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not archive the report for device {DeviceId}", deviceId);
        }
    }
}

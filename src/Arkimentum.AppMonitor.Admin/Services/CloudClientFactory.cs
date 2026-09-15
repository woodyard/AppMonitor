using System.Net.Http;
using Arkimentum.AppMonitor.Cloud;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// Creates the <see cref="CloudClient"/> the organization pages talk through. It is an interface only so a test
/// harness can hand back a client wired to an in-process <see cref="HttpMessageHandler"/> instead of the network.
/// </summary>
public interface ICloudClientFactory
{
    CloudClient Create(string serverUrl, string? proxyUrl);
}

/// <inheritdoc />
public sealed class CloudClientFactory : ICloudClientFactory
{
    private readonly ILogger<CloudClient> _log;

    public CloudClientFactory(ILogger<CloudClient> log) => _log = log;

    public CloudClient Create(string serverUrl, string? proxyUrl) => new(_log, serverUrl, proxyUrl);
}

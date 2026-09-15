using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkimentum.AppMonitor.Cloud;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// Reads <c>&lt;StateDirectory&gt;\cloud-status.json</c> — what the service last managed to do with the cloud. The
/// shape is <see cref="CloudStatus"/> in Core, shared with the service that writes the file; the console only ever
/// reads it. Every failure is "not connected" rather than an exception, because a missing file simply means this
/// machine is not enrolled.
/// </summary>
public sealed class CloudStatusReader
{
    public const string FileName = CloudStatus.FileName;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<CloudStatusReader> _log;

    public CloudStatusReader(ILogger<CloudStatusReader> log) => _log = log;

    public static string PathFor(string stateDirectory) => Path.Combine(stateDirectory, FileName);

    /// <summary>The parsed file, or null when it does not exist or cannot be read.</summary>
    public CloudStatus? Read(string stateDirectory)
    {
        var path = PathFor(stateDirectory);
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var status = JsonSerializer.Deserialize<CloudStatus>(stream, Options);
            if (status is not null) _log.LogInformation("Read the cloud status of this machine from {Path}.", path);
            return status;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading {Path} failed; the Overview page shows the machine as not connected.", path);
            return null;
        }
    }
}

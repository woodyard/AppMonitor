using Arkimentum.AppMonitor.Cloud;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service.Cloud;

/// <summary>Reads and atomically writes cloud-status.json in the state directory (shape: Core CloudStatus).</summary>
public sealed class CloudStatusFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _logger;
    private readonly object _lock = new();

    public CloudStatusFile(ILogger logger, string stateDirectory)
    {
        _logger = logger;
        FilePath = Path.Combine(stateDirectory, "cloud-status.json");
    }

    public string FilePath { get; }

    public CloudStatus? Load()
    {
        try
        {
            lock (_lock)
                return File.Exists(FilePath) ? JsonSerializer.Deserialize<CloudStatus>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud status file {Path} is unreadable", FilePath);
            return null;
        }
    }

    public void Save(CloudStatus status)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(status, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the cloud status file {Path}", FilePath);
        }
    }
}

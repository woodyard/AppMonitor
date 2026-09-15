using System.Text.Json;
using Arkimentum.AppMonitor.Configuration;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Cloud;

/// <summary>The last organization configuration fetched from the cloud, kept on disk so it survives restarts and offline periods.</summary>
public sealed class CachedCloudConfig
{
    public required Guid OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
    public required string ConfigVersion { get; set; }
    public DateTimeOffset FetchedUtc { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public required SettingsDocument Settings { get; set; }
}

/// <summary>
/// Stores the organization configuration under the state directory (<c>cloud-config.json</c>). The configuration reader
/// layers it between the policy key and the local preferences; see <see cref="RegistryConfigurationReader"/>.
/// </summary>
public sealed class CloudConfigCache
{
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly object _lock = new();
    private CachedCloudConfig? _memory;
    private DateTime _memoryStamp;

    public CloudConfigCache(ILogger<CloudConfigCache> logger, string stateDirectory)
    {
        _logger = logger;
        _path = Path.Combine(stateDirectory, "cloud-config.json");
    }

    public string FilePath => _path;

    /// <summary>Returns the cached configuration or null. Cheap: re-reads the file only when its timestamp changed.</summary>
    public CachedCloudConfig? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) { _memory = null; return null; }
                var stamp = File.GetLastWriteTimeUtc(_path);
                if (_memory is not null && stamp == _memoryStamp) return _memory;
                _memory = JsonSerializer.Deserialize<CachedCloudConfig>(File.ReadAllText(_path), CloudJson.Options);
                _memoryStamp = stamp;
                return _memory;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cached organization configuration at {Path} is unreadable and will be ignored", _path);
                return null;
            }
        }
    }

    public void Save(DeviceConfigResponse response)
    {
        var cached = new CachedCloudConfig
        {
            OrganizationId = response.OrganizationId,
            OrganizationName = response.OrganizationName,
            ConfigVersion = response.ConfigVersion,
            FetchedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = response.UpdatedUtc,
            UpdatedBy = response.UpdatedBy,
            Settings = response.Settings,
        };
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cached, CloudJson.Options));
            File.Move(tmp, _path, overwrite: true);
            _memory = cached;
            _memoryStamp = File.GetLastWriteTimeUtc(_path);
        }
        _logger.LogInformation("Organization configuration {Version} from {Org} cached ({Globals} global value(s), {Apps} app(s))",
            response.ConfigVersion, response.OrganizationName, response.Settings.Global.Count, response.Settings.Apps.Count);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _memory = null;
            try { if (File.Exists(_path)) File.Delete(_path); } catch (Exception ex) { _logger.LogWarning(ex, "Could not delete {Path}", _path); }
        }
    }

    /// <summary>Settings document for the configuration reader's cloud layer, or null when nothing is cached.</summary>
    public SettingsDocument? CurrentSettings() => Load()?.Settings;
}

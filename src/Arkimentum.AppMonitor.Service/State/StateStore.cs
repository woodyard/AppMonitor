using System.Text.Json;
using System.Text.Json.Serialization;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service.State;

public sealed class ServiceState
{
    public Dictionary<string, PendingUpdate> Updates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastScanUtc { get; set; }
    public DateTimeOffset? NextScanUtc { get; set; }
    public string? LastScanSummary { get; set; }
}

/// <summary>Persists <see cref="ServiceState"/> as JSON (atomic replace) so deadlines and deferrals survive restarts.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<StateStore> _logger;
    private readonly object _lock = new();

    public StateStore(ILogger<StateStore> logger) => _logger = logger;

    public static string PathFor(AgentSettings settings) => Path.Combine(settings.StateDirectory, "state.json");

    public ServiceState Load(AgentSettings settings)
    {
        var path = PathFor(settings);
        try
        {
            if (!File.Exists(path)) return new ServiceState();
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<ServiceState>(json, Json) ?? new ServiceState();
            state.Updates = new Dictionary<string, PendingUpdate>(state.Updates, StringComparer.OrdinalIgnoreCase);
            // An install that was in flight when the service stopped cannot be trusted.
            foreach (var u in state.Updates.Values.Where(u => u.State == UpdateState.Installing)) u.State = UpdateState.Available;
            _logger.LogInformation("Loaded state from {Path}: {Count} tracked update(s)", path, state.Updates.Count);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load state from {Path}; starting empty", path);
            return new ServiceState();
        }
    }

    public void Save(AgentSettings settings, ServiceState state)
    {
        var path = PathFor(settings);
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not save state to {Path}", path);
            }
        }
    }
}

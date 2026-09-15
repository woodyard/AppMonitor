using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service;

/// <summary>Caches the registry configuration and reloads it on demand (before every scan and every policy tick).</summary>
public sealed class SettingsProvider
{
    private readonly RegistryConfigurationReader _reader;
    private readonly ILogger<SettingsProvider> _logger;
    private readonly object _lock = new();
    private AgentSettings _current;
    private string _lastFingerprint = string.Empty;

    public SettingsProvider(RegistryConfigurationReader reader, ILogger<SettingsProvider> logger)
    {
        _reader = reader;
        _logger = logger;
        _current = new AgentSettings();
    }

    public AgentSettings Current { get { lock (_lock) return _current; } }

    /// <summary>Optional post-processing applied after every read (used by the unprivileged testing mode).</summary>
    public Func<AgentSettings, AgentSettings>? Overrides { get; init; }

    public event Action<AgentSettings>? Changed;

    public AgentSettings Reload()
    {
        AgentSettings s;
        try
        {
            s = _reader.Read();
            if (Overrides is not null) s = Overrides(s);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read configuration from the registry; keeping the previous settings");
            return Current;
        }

        var fp = Fingerprint(s);
        bool changed;
        lock (_lock)
        {
            changed = fp != _lastFingerprint;
            _current = s;
            _lastFingerprint = fp;
        }
        if (changed)
        {
            _logger.LogInformation(
                "Configuration loaded: scan every {Scan} min, notify every {Notify} min, winget={Winget}, web={Web}, {Count} app(s): {Apps}",
                s.ScanIntervalMinutes, s.NotificationIntervalMinutes, s.WingetEnabled, s.WebSourcesEnabled, s.Apps.Count,
                string.Join(", ", s.Apps.Select(a => $"{a.AppId}[{a.Source},{a.Context}{(a.Mandatory ? ",mandatory" : "")}{(a.Enabled ? "" : ",disabled")}]")));
            Changed?.Invoke(s);
        }
        return s;
    }

    private static readonly System.Text.Json.JsonSerializerOptions FingerprintJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string Fingerprint(AgentSettings s) =>
        System.Text.Json.JsonSerializer.Serialize(s with { ValueSources = new Dictionary<string, string>(), LoadedAtUtc = default }, FingerprintJson);
}

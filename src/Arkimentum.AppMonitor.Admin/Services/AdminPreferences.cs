using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// The few things the console remembers for the administrator using it, as opposed to for the machine.
///
/// <para>
/// Only one thing so far: the cloud server URL. It is pre-filled from the machine's own configuration
/// (<c>CloudServerUrl</c> under HKLM, policy or preference, read through
/// <see cref="RegistryConfigurationReader"/>) when the agent on this machine is already enrolled; otherwise it
/// falls back to what this administrator last typed, kept in
/// <c>HKCU\SOFTWARE\Arkimentum\AppMonitor\Admin\CloudServerUrl</c>. Nothing here is machine configuration — the
/// console never writes HKLM outside an explicit Apply.
/// </para>
/// </summary>
public sealed class AdminPreferences
{
    /// <summary>HKCU key holding this administrator's console preferences.</summary>
    public const string AdminKeyPath = @"SOFTWARE\Arkimentum\AppMonitor\Admin";

    public const string CloudServerUrlValue = "CloudServerUrl";

    private readonly ILogger<AdminPreferences> _log;
    private readonly ILoggerFactory _loggers;
    private readonly SettingsStoreService _store;
    private readonly CatalogService _catalog;

    public AdminPreferences(ILogger<AdminPreferences> log, ILoggerFactory loggers, SettingsStoreService store, CatalogService catalog)
    {
        _log = log;
        _loggers = loggers;
        _store = store;
        _catalog = catalog;
    }

    /// <summary>The effective agent configuration of this machine, or the defaults when it cannot be read.</summary>
    public AgentSettings EffectiveSettings => _store.ReadEffective(_loggers, _catalog);

    /// <summary>What the Connect page should start with: the machine's own server, else the remembered one.</summary>
    public string InitialServerUrl()
    {
        var machine = EffectiveSettings.CloudServerUrl;
        if (!string.IsNullOrWhiteSpace(machine))
        {
            _log.LogInformation("Cloud server URL pre-filled from this machine's configuration: {Url}", machine);
            return machine.Trim();
        }

        var remembered = ReadRemembered();
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            _log.LogInformation("Cloud server URL pre-filled from {Key}: {Url}", @"HKCU\" + AdminKeyPath, remembered);
            return remembered!;
        }
        return string.Empty;
    }

    /// <summary>True when the URL comes from the machine configuration rather than from this administrator.</summary>
    public bool ServerUrlComesFromMachine => !string.IsNullOrWhiteSpace(EffectiveSettings.CloudServerUrl);

    public string? ReadRemembered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdminKeyPath, writable: false);
            return key?.GetValue(CloudServerUrlValue) as string;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading the remembered cloud server URL failed.");
            return null;
        }
    }

    /// <summary>Remembers the URL for the next start. Never written when the machine already supplies one.</summary>
    public void Remember(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || ServerUrlComesFromMachine) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AdminKeyPath, writable: true);
            key?.SetValue(CloudServerUrlValue, serverUrl.Trim(), RegistryValueKind.String);
            _log.LogInformation("Remembered the cloud server URL {Url} in {Key}.", serverUrl.Trim(), @"HKCU\" + AdminKeyPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Remembering the cloud server URL failed.");
        }
    }
}

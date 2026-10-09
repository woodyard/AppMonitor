using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service;

/// <summary>Whether this process may edit SYSTEM's winget settings file at all.</summary>
/// <param name="Allowed">
/// True only for the real service: the process runs as LocalSystem and not in <c>--user-config</c> testing mode. Anywhere
/// else <c>winget settings export</c> would name the developer's own winget settings file.
/// </param>
public sealed record WingetDownloaderOptions(bool Allowed);

/// <summary>
/// Keeps <c>network.downloader</c> in SYSTEM's own winget settings file on the <c>WingetDownloader</c> setting
/// (<see cref="AgentSettings.WingetDownloader"/>; policy > organization > preference > default <c>wininet</c>). The
/// service runs winget as LocalSystem, so that file decides how its installers are downloaded; winget has no command-line
/// switch for it. Applied at start-up and after every configuration change (<see cref="Attach"/>, in the background),
/// and awaited before every machine-wide winget install (<see cref="EnsureAppliedAsync"/>).
/// <para>
/// The file's path is asked of winget once (<c>winget settings export</c>) and kept for the life of the process; a
/// failure is logged as a warning once and asked again at most every <see cref="RetryInterval"/>. With the path known,
/// a check is one small file read; the file is written only when the value changes. The tray never touches the user's
/// own winget settings.
/// </para>
/// </summary>
public sealed class WingetDownloaderController
{
    /// <summary>How long after a failed lookup of the settings file winget is asked again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<WingetDownloaderController> _logger;
    private readonly WingetDownloaderOptions _options;
    private readonly Func<AgentSettings, CancellationToken, Task<(string? Path, string? Error)>> _findSettingsFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // ---- guarded by _gate
    private string? _path;
    private DateTimeOffset? _lookupFailedUtc;
    private bool _warned;
    private bool _reported;
    private string? _lastApplied;

    /// <summary>The downloader SYSTEM's winget settings file was last seen to hold (null = none, or not known).</summary>
    private volatile string? _inEffect;

    /// <param name="findSettingsFile">Finds the settings file; null = <c>winget settings export</c> with the located winget. Tests pass a fake.</param>
    /// <param name="clock">The time source; null = the system clock.</param>
    public WingetDownloaderController(ILogger<WingetDownloaderController> logger, WingetDownloaderOptions options,
        Func<AgentSettings, CancellationToken, Task<(string? Path, string? Error)>>? findSettingsFile = null, Func<DateTimeOffset>? clock = null)
    {
        _logger = logger;
        _options = options;
        _findSettingsFile = findSettingsFile ?? FindWithWingetAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True when SYSTEM's winget settings file was last seen to say <c>wininet</c>: winget then downloads without Delivery
    /// Optimization, so the install progress must not look for a DO job (<see cref="Install.InstallProgressTracker"/>).
    /// </summary>
    public bool WinInetInEffect => string.Equals(_inEffect, WingetUserSettings.WinInet, StringComparison.Ordinal);

    /// <summary>Follows every configuration change the provider announces (the first load at start-up included).</summary>
    public void Attach(SettingsProvider settings) =>
        settings.Changed += s => _ = Task.Run(() => EnsureAppliedAsync(s, CancellationToken.None));

    /// <summary>
    /// Makes SYSTEM's winget settings file say what <paramref name="settings"/> asks for. Does nothing outside the real
    /// service or with winget switched off. Never throws (cancellation aside).
    /// </summary>
    public async Task EnsureAppliedAsync(AgentSettings settings, CancellationToken ct)
    {
        if (!_options.Allowed || !settings.WingetEnabled) return;
        var wanted = string.IsNullOrWhiteSpace(settings.WingetDownloader) ? AgentSettings.DefaultWingetDownloader : settings.WingetDownloader.Trim().ToLowerInvariant();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_path is null)
            {
                if (_lookupFailedUtc is { } failed && _clock() - failed < RetryInterval) return;
                (string? Path, string? Error) found;
                try { found = await _findSettingsFile(settings, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { found = (null, ex.Message); }
                if (found.Path is null)
                {
                    _lookupFailedUtc = _clock();
                    _logger.Log(NextProblemLevel(), "winget's settings file for SYSTEM could not be found ({Error}); the winget download method stays as it is",
                        found.Error ?? "no reason given");
                    return;
                }
                _path = found.Path;
                _lookupFailedUtc = null;
                _logger.LogDebug("SYSTEM's winget settings file: {Path}", _path);
            }

            var result = WingetUserSettings.ApplyToFile(_path, wanted);
            if (!result.Ok)
            {
                _inEffect = null;
                _logger.Log(NextProblemLevel(), "The winget download method could not be set to {Downloader} in {Path}: {Error}", wanted, result.Path, result.Error!);
                return;
            }

            _inEffect = result.Downloader;
            var first = !_reported || !string.Equals(_lastApplied, wanted, StringComparison.Ordinal);
            _reported = true;
            _lastApplied = wanted;
            _warned = false;
            if (result.Written)
            {
                if (wanted == WingetUserSettings.Default)
                    _logger.LogInformation("winget downloader left to winget's own default in {Path} (the setting was removed)", result.Path);
                else
                    _logger.LogInformation("winget downloader set to {Downloader} in {Path}", wanted, result.Path);
            }
            else if (first)
            {
                _logger.LogInformation("winget downloader is {Downloader} in {Path} (already)",
                    wanted == WingetUserSettings.Default ? "winget's own default" : wanted, result.Path);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Log(NextProblemLevel(), "Setting the winget download method failed: {Error}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Warning the first time, Debug while the trouble lasts (until a success); called under the gate.</summary>
    private LogLevel NextProblemLevel()
    {
        if (_warned) return LogLevel.Debug;
        _warned = true;
        return LogLevel.Warning;
    }

    private async Task<(string? Path, string? Error)> FindWithWingetAsync(AgentSettings settings, CancellationToken ct)
    {
        var winget = WingetLocator.Find(_logger, isSystem: true, string.IsNullOrWhiteSpace(settings.WingetPath) ? null : settings.WingetPath);
        if (winget is null) return (null, "winget.exe was not found");
        return await WingetUserSettings.FindUserSettingsFileAsync(_logger, winget, ct).ConfigureAwait(false);
    }
}

using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Logging;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// LogLevel applies while the process runs, from every configuration layer: the file logger and the logger factory's
/// global filter read one shared switch on every call, and the service moves the switch on every configuration change.
/// </summary>
public sealed class LogLevelSwitchTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "Arkimentum.AppMonitor.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _rootPath = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _hive;

    public LogLevelSwitchTests() => _hive = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true)!;

    public void Dispose()
    {
        _hive.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootPath, false); } catch { }
        try { Directory.Delete(_logDirectory, recursive: true); } catch { }
    }

    private sealed record Entry(string Category, LogLevel Level, string Message);

    /// <summary>Records everything that gets past the logger factory's filters.</summary>
    private sealed class CaptureProvider : ILoggerProvider
    {
        public List<Entry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
        public void Dispose() { }

        private sealed class Capture(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add(new Entry(category, logLevel, formatter(state, exception)));
            }
        }
    }

    private static ILoggerFactory Factory(LogLevelSwitch levelSwitch, CaptureProvider capture) => LoggerFactory.Create(b =>
    {
        b.AddLevelSwitch(levelSwitch);
        b.AddFilter("Microsoft", LogLevel.Warning);
        b.AddProvider(capture);
    });

    [Fact]
    public void File_logger_follows_the_switch_without_being_recreated()
    {
        var levelSwitch = new LogLevelSwitch(LogLevel.Warning);
        var provider = new FileLoggerProvider(new FileLoggerOptions { Directory = _logDirectory, FilePrefix = "LevelTest", LevelSwitch = levelSwitch });
        Assert.Same(levelSwitch, provider.LevelSwitch);
        var logger = provider.CreateLogger("Tests.Category");

        logger.LogInformation("information-at-warning");
        logger.LogWarning("warning-at-warning");
        levelSwitch.MinimumLevel = LogLevel.Debug;
        logger.LogDebug("debug-at-debug");
        levelSwitch.MinimumLevel = LogLevel.Warning;
        logger.LogDebug("debug-at-warning-again");
        provider.Dispose();   // drains the writer queue

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_logDirectory, "LevelTest_*.log")));
        Assert.DoesNotContain("information-at-warning", text);
        Assert.Contains("[WRN] Category: warning-at-warning", text);
        Assert.Contains("[DBG] Category: debug-at-debug", text);
        Assert.DoesNotContain("debug-at-warning-again", text);
    }

    [Fact]
    public void File_logger_without_a_switch_starts_from_MinimumLevel()
    {
        var provider = new FileLoggerProvider(new FileLoggerOptions { Directory = _logDirectory, FilePrefix = "LevelTest", MinimumLevel = LogLevel.Error });
        try
        {
            Assert.Equal(LogLevel.Error, provider.LevelSwitch.MinimumLevel);
            var logger = provider.CreateLogger("X");
            Assert.False(logger.IsEnabled(LogLevel.Warning));
            provider.LevelSwitch.MinimumLevel = LogLevel.Trace;
            Assert.True(logger.IsEnabled(LogLevel.Trace));
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public void Factory_filter_is_evaluated_per_call_and_specific_rules_still_win()
    {
        var levelSwitch = new LogLevelSwitch(LogLevel.Information);
        var capture = new CaptureProvider();
        using var factory = Factory(levelSwitch, capture);
        var logger = factory.CreateLogger("Arkimentum.AppMonitor.Service.Worker");
        var framework = factory.CreateLogger("Microsoft.Hosting.Lifetime");

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        logger.LogDebug("before");
        levelSwitch.MinimumLevel = LogLevel.Debug;
        Assert.True(logger.IsEnabled(LogLevel.Debug));   // same logger instance: nothing cached
        logger.LogDebug("after");
        framework.LogInformation("framework-information");   // the "Microsoft" rule keeps Warning
        levelSwitch.MinimumLevel = LogLevel.Warning;
        logger.LogInformation("suppressed-again");
        framework.LogWarning("framework-warning");

        Assert.Equal(["after", "framework-warning"], capture.Entries.Select(e => e.Message));
    }

    [Fact]
    public void Apply_announces_the_change_at_the_more_verbose_level_and_respects_pinning()
    {
        var levelSwitch = new LogLevelSwitch(LogLevel.Information);
        var capture = new CaptureProvider();
        using var factory = Factory(levelSwitch, capture);
        var logger = factory.CreateLogger("Test");

        Assert.True(levelSwitch.Apply(LogLevel.Debug, logger, "organization"));
        Assert.False(levelSwitch.Apply(LogLevel.Debug, logger, "organization"));
        Assert.True(levelSwitch.Apply(LogLevel.Warning, logger, "policy"));   // announced before the level rises
        Assert.Equal(LogLevel.Warning, levelSwitch.MinimumLevel);
        Assert.Equal(
            ["Log level changed from Information to Debug (source: organization)", "Log level changed from Debug to Warning (source: policy)"],
            capture.Entries.Select(e => e.Message));

        var pinned = new LogLevelSwitch(LogLevel.Debug) { Pinned = true };
        Assert.False(pinned.Apply(LogLevel.Error, logger, "service configuration"));
        Assert.Equal(LogLevel.Debug, pinned.MinimumLevel);
    }

    [Fact]
    public void Settings_changes_move_the_level_from_every_layer()
    {
        SettingsDocument? organization = null;
        var reader = new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, null, _hive, () => organization);
        var settings = new SettingsProvider(reader, NullLogger<SettingsProvider>.Instance);
        var levelSwitch = new LogLevelSwitch(LogLevel.Information);   // what the registry-only bootstrap read found
        var capture = new CaptureProvider();
        using var factory = Factory(levelSwitch, capture);
        new LogLevelController(levelSwitch, factory.CreateLogger<LogLevelController>()).Attach(settings);

        settings.Reload();                                   // start-up, nothing configured: stays at the default
        Assert.Equal(LogLevel.Information, levelSwitch.MinimumLevel);
        Assert.Empty(capture.Entries);

        organization = new SettingsDocument();
        organization.Global["LogLevel"] = SettingValue.From("Debug");
        settings.Reload();                                   // the organization publishes Debug
        Assert.Equal(LogLevel.Debug, levelSwitch.MinimumLevel);

        using (var pref = _hive.CreateSubKey(AgentSettings.RegistryRoot)!) pref.SetValue("LogLevel", "Error");
        settings.Reload();                                   // the organization still beats the local preference
        Assert.Equal(LogLevel.Debug, levelSwitch.MinimumLevel);

        using (var pol = _hive.CreateSubKey(AgentSettings.PolicyRegistryRoot)!) pol.SetValue("LogLevel", "Warning");
        settings.Reload();                                   // policy beats the organization
        Assert.Equal(LogLevel.Warning, levelSwitch.MinimumLevel);

        _hive.DeleteSubKeyTree(AgentSettings.PolicyRegistryRoot);
        organization = null;
        settings.Reload();                                   // only the local preference is left
        Assert.Equal(LogLevel.Error, levelSwitch.MinimumLevel);

        // Warning -> Error is announced at Information, which neither level writes.
        Assert.Equal(
            ["Log level changed from Information to Debug (source: organization)", "Log level changed from Debug to Warning (source: policy)"],
            capture.Entries.Select(e => e.Message));
    }

    [Theory]
    [InlineData("Policy", "policy")]
    [InlineData("Cloud", "organization")]
    [InlineData("Preference", "local preference")]
    [InlineData("Default", "default")]
    [InlineData(null, "default")]
    public void Sources_are_named_for_people(string? source, string expected) =>
        Assert.Equal(expected, LogLevelController.DescribeSource(source));
}

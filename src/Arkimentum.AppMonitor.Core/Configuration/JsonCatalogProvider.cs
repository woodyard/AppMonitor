using System.Text.Json;
using System.Text.Json.Serialization;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Configuration;

/// <summary>
/// Loads the app catalog from a JSON array (camelCase property names, enums as strings, unknown members ignored).
/// The catalog only supplies <em>templates</em>: the registry reader merges registry values over them.
/// <para>Never throws: a missing or malformed catalog logs a warning and yields an empty list.</para>
/// </summary>
public sealed class JsonCatalogProvider : ICatalogProvider
{
    /// <summary>File name used when no catalog path is configured; looked up next to the executing assembly.</summary>
    public const string DefaultFileName = "catalog.json";

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private string? _cachedPath;
    private DateTime _cachedWriteUtc;
    private IReadOnlyList<AppPolicy>? _cached;

    public JsonCatalogProvider(ILogger<JsonCatalogProvider> logger) => _logger = logger;

    /// <inheritdoc />
    public IReadOnlyList<AppPolicy> GetCatalog(string catalogPath)
    {
        var path = ResolvePath(catalogPath);
        if (path is null) return [];

        try
        {
            var writeUtc = File.GetLastWriteTimeUtc(path);
            lock (_gate)
            {
                if (_cached is not null &&
                    string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _cachedWriteUtc == writeUtc)
                {
                    return _cached;
                }
            }

            var json = File.ReadAllText(path);
            var entries = Parse(json, path, _logger);

            lock (_gate)
            {
                _cachedPath = path;
                _cachedWriteUtc = writeUtc;
                _cached = entries;
            }

            _logger.LogInformation("Loaded {Count} catalog entr{Plural} from {Path}.", entries.Count, entries.Count == 1 ? "y" : "ies", path);
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the app catalog from {Path}; continuing without catalog defaults.", path);
            return [];
        }
    }

    /// <summary>Clears the cached catalog (used by tests and after a configuration reload).</summary>
    public void InvalidateCache()
    {
        lock (_gate) { _cached = null; _cachedPath = null; _cachedWriteUtc = default; }
    }

    /// <summary>Resolves the configured path, or <c>catalog.json</c> next to the executing assembly. Null when nothing exists.</summary>
    public static string? ResolvePath(string? catalogPath)
    {
        if (!string.IsNullOrWhiteSpace(catalogPath))
        {
            var configured = Environment.ExpandEnvironmentVariables(catalogPath.Trim().Trim('"'));
            try
            {
                if (Directory.Exists(configured)) configured = Path.Combine(configured, DefaultFileName);
                return File.Exists(configured) ? Path.GetFullPath(configured) : null;
            }
            catch { return null; }
        }

        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
            return File.Exists(beside) ? beside : null;
        }
        catch { return null; }
    }

    /// <summary>Parses a catalog JSON document. Entries without an appId are skipped with a warning. Never throws.</summary>
    public static IReadOnlyList<AppPolicy> Parse(string json, string sourceName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        List<CatalogEntry?>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<CatalogEntry?>>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("The app catalog {Path} is not valid JSON ({Message}); continuing without catalog defaults.", sourceName, ex.Message);
            return [];
        }

        if (entries is null) return [];

        var result = new List<AppPolicy>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.AppId))
            {
                logger.LogWarning("Catalog {Path}: skipping an entry without an appId (displayName '{DisplayName}').", sourceName, entry.DisplayName);
                continue;
            }
            if (!seen.Add(entry.AppId.Trim()))
            {
                logger.LogWarning("Catalog {Path}: duplicate appId '{AppId}'; the first entry wins.", sourceName, entry.AppId);
                continue;
            }
            result.Add(entry.ToPolicy());
        }
        return result;
    }

    /// <summary>
    /// Wire format of one catalog entry. Deliberately separate from <see cref="AppPolicy"/> so that the JSON shape is a
    /// stable contract (and so <see cref="AppPolicy"/>'s <c>required</c>/init members do not constrain deserialisation).
    /// </summary>
    public sealed class CatalogEntry
    {
        public string? AppId { get; set; }
        public string? DisplayName { get; set; }
        public bool? Enabled { get; set; }
        public UpdateSource? Source { get; set; }
        public InstallContext? Context { get; set; }

        public string? WingetId { get; set; }
        public string? WingetSourceName { get; set; }
        public string? WingetExtraArgs { get; set; }

        public string? VersionUrl { get; set; }
        public string? VersionRegex { get; set; }
        public string? DownloadUrl { get; set; }
        public string? InstallerArgs { get; set; }
        public InstallerType? InstallerType { get; set; }
        public string? Sha256Url { get; set; }
        public string? Sha256 { get; set; }
        public string? UserDownloadUrl { get; set; }
        public string? UserInstallerArgs { get; set; }

        public string? DetectDisplayNameRegex { get; set; }
        public string? DetectPublisherRegex { get; set; }
        public string? DetectFilePath { get; set; }

        public bool? Mandatory { get; set; }
        public int? DeadlineHours { get; set; }
        public int? MaxDeferrals { get; set; }
        public List<int>? DeferralOptionsMinutes { get; set; }
        public List<string>? ProcessNames { get; set; }
        public bool? AutoInstall { get; set; }
        public int? CloseGracePeriodMinutes { get; set; }
        public bool? ForceCloseAtDeadline { get; set; }
        public string? MinimumVersion { get; set; }
        public int? NotificationIntervalMinutes { get; set; }

        /// <summary>Free-form note for administrators reading the catalog file; ignored by the agent.</summary>
        public string? Notes { get; set; }

        public AppPolicy ToPolicy()
        {
            var template = new AppPolicy { AppId = AppId!.Trim() };
            return template with
            {
                DisplayName = DisplayName?.Trim() ?? string.Empty,
                Enabled = Enabled ?? template.Enabled,
                Source = Source ?? template.Source,
                Context = Context ?? template.Context,
                WingetId = Trim(WingetId),
                WingetSourceName = string.IsNullOrWhiteSpace(WingetSourceName) ? template.WingetSourceName : WingetSourceName.Trim(),
                WingetExtraArgs = Trim(WingetExtraArgs),
                VersionUrl = Trim(VersionUrl),
                VersionRegex = VersionRegex,
                DownloadUrl = Trim(DownloadUrl),
                InstallerArgs = InstallerArgs,
                InstallerType = InstallerType ?? template.InstallerType,
                Sha256Url = Trim(Sha256Url),
                Sha256 = Trim(Sha256),
                UserDownloadUrl = Trim(UserDownloadUrl),
                UserInstallerArgs = UserInstallerArgs,
                DetectDisplayNameRegex = DetectDisplayNameRegex,
                DetectPublisherRegex = DetectPublisherRegex,
                DetectFilePath = Trim(DetectFilePath),
                Mandatory = Mandatory ?? template.Mandatory,
                DeadlineHours = DeadlineHours ?? template.DeadlineHours,
                MaxDeferrals = MaxDeferrals ?? template.MaxDeferrals,
                DeferralOptionsMinutes = DeferralOptionsMinutes is { Count: > 0 } d ? d : template.DeferralOptionsMinutes,
                ProcessNames = ProcessNames is { Count: > 0 } p
                    ? p.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList()
                    : template.ProcessNames,
                AutoInstall = AutoInstall ?? template.AutoInstall,
                CloseGracePeriodMinutes = CloseGracePeriodMinutes ?? template.CloseGracePeriodMinutes,
                ForceCloseAtDeadline = ForceCloseAtDeadline ?? template.ForceCloseAtDeadline,
                MinimumVersion = Trim(MinimumVersion),
                NotificationIntervalMinutes = NotificationIntervalMinutes,
            };
        }

        private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

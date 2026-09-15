using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Inventory;

/// <summary>An application found on the local machine, ready to be added to the monitor.</summary>
public sealed record DiscoveredApp
{
    public required string DisplayName { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    /// <summary>winget package id when winget knows the app (null = not manageable through winget).</summary>
    public string? WingetId { get; init; }
    /// <summary>Newer version winget already knows about, if any.</summary>
    public string? AvailableVersion { get; init; }
    /// <summary>System (machine-wide), User (per-user) or Auto when unknown.</summary>
    public InstallContext Context { get; init; } = InstallContext.Auto;
    /// <summary>Matching catalog entry, when the catalog knows this product.</summary>
    public string? CatalogAppId { get; init; }
    /// <summary>AppId already configured for this product, when it is monitored already.</summary>
    public string? ConfiguredAppId { get; init; }
    /// <summary>True when winget printed a shortened (…) id; such rows cannot be added automatically.</summary>
    public bool WingetIdTruncated { get; init; }
    /// <summary>Where the entry came from: "winget", "registry" or "winget+registry".</summary>
    public string Origin { get; init; } = "registry";

    public bool IsConfigured => ConfiguredAppId is not null;
    public bool CanQuickAdd => !IsConfigured && !WingetIdTruncated && (CatalogAppId is not null || !string.IsNullOrWhiteSpace(WingetId));

    /// <summary>The AppId a quick add would create: the catalog id when known, else the winget id.</summary>
    public string? SuggestedAppId => CatalogAppId ?? (string.IsNullOrWhiteSpace(WingetId) || WingetIdTruncated ? null : WingetId);
}

/// <summary>
/// Lists the applications installed on this machine so an administrator can pick the ones to monitor. Combines the
/// Uninstall-key inventory (all installs the registry knows about, with context) with <c>winget list</c> (adds the
/// winget package id and any newer version winget already knows) and matches both against the catalog and the
/// currently configured applications.
/// </summary>
public sealed class InstalledAppDiscovery
{
    private readonly ILogger _logger;
    private readonly InstalledAppScanner _scanner;

    public InstalledAppDiscovery(ILogger<InstalledAppDiscovery> logger, InstalledAppScanner scanner)
    {
        _logger = logger;
        _scanner = scanner;
    }

    /// <param name="configured">Currently configured applications (effective policies) to mark as already monitored.</param>
    /// <param name="catalog">Catalog entries used to recognise known products.</param>
    /// <param name="wingetPath">Optional explicit winget.exe path.</param>
    /// <param name="includeWinget">Run winget list (slower, a few seconds) to obtain package ids and available versions.</param>
    public async Task<IReadOnlyList<DiscoveredApp>> DiscoverAsync(IReadOnlyList<AppPolicy> configured, IReadOnlyList<AppPolicy> catalog,
        string? wingetPath, bool includeWinget, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Reading installed applications from the registry…");
        var inventory = _scanner.Scan(includeMachine: true, includeUsers: true);
        _logger.LogInformation("Discovery: {Count} installed application(s) in the registry inventory", inventory.Count);

        var wingetRows = new List<(WingetRow Row, InstallContext Context)>();
        if (includeWinget)
        {
            var winget = WingetLocator.Find(_logger, isSystem: false, wingetPath) ?? WingetLocator.Find(_logger, isSystem: true, wingetPath);
            if (winget is null)
            {
                _logger.LogWarning("Discovery: winget.exe not found; package ids will be missing");
            }
            else
            {
                foreach (var (scope, context) in new[] { ("machine", InstallContext.System), ("user", InstallContext.User) })
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Asking winget for {scope}-scope packages…");
                    var run = await ProcessRunner.RunAsync(_logger, winget,
                        $"list --scope {scope} --accept-source-agreements --disable-interactivity", TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);
                    if (run.ExitCode != 0 && !WingetOutputParser.IsNotInstalledOutput(run.CombinedOutput))
                    {
                        _logger.LogWarning("Discovery: winget list --scope {Scope} exited with {Code}: {Tail}", scope, run.ExitCode, run.LastLines(2));
                        continue;
                    }
                    var rows = WingetOutputParser.ParseListOutput(run.StandardOutput);
                    _logger.LogInformation("Discovery: winget reports {Count} {Scope}-scope package(s)", rows.Count, scope);
                    wingetRows.AddRange(rows.Where(r => !string.IsNullOrWhiteSpace(r.Id)).Select(r => (r, context)));
                }
            }
        }

        progress?.Report("Matching against the catalog and the current configuration…");
        var result = new List<DiscoveredApp>();
        var usedRows = new HashSet<WingetRow>();

        // Registry entries first (they carry publisher + reliable context); attach the winget row with the same name.
        foreach (var app in inventory.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var match = wingetRows.FirstOrDefault(w => !usedRows.Contains(w.Row) && NamesMatch(w.Row.Name, app.DisplayName)
                                                       && (w.Context == app.Context || w.Context == InstallContext.Auto));
            if (match.Row is not null) usedRows.Add(match.Row);
            result.Add(Build(app.DisplayName, app.DisplayVersion, app.Publisher, match.Row, app.Context, match.Row is null ? "registry" : "winget+registry", configured, catalog));
        }

        // winget-only rows (MSIX/Store packages and other installs without an Uninstall key).
        foreach (var (row, context) in wingetRows.Where(w => !usedRows.Contains(w.Row)))
        {
            if (row.Source.Length == 0 && string.IsNullOrWhiteSpace(row.Available))
            {
                // Rows without a source are ARP entries winget could not map to a package; the registry pass already has them.
                if (result.Any(r => NamesMatch(r.DisplayName, row.Name))) continue;
            }
            result.Add(Build(row.Name, row.Version, null, row, context, "winget", configured, catalog));
        }

        var list = result
            .GroupBy(r => (Name: r.DisplayName.ToLowerInvariant(), r.Context, r.WingetId))
            .Select(g => g.First())
            // Most relevant first: known catalog products, then apps with a pending update, then anything else winget can
            // manage, then the rest; already-monitored apps last. Alphabetical within each group.
            .OrderBy(r => r.IsConfigured)
            .ThenByDescending(r => r.CatalogAppId is not null)
            .ThenByDescending(r => r.AvailableVersion is not null)
            .ThenByDescending(r => r.CanQuickAdd)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _logger.LogInformation("Discovery: {Total} application(s), {QuickAdd} can be added directly, {Configured} already monitored",
            list.Count, list.Count(r => r.CanQuickAdd), list.Count(r => r.IsConfigured));
        return list;
    }

    internal static DiscoveredApp Build(string name, string? version, string? publisher, WingetRow? row, InstallContext context, string origin,
        IReadOnlyList<AppPolicy> configured, IReadOnlyList<AppPolicy> catalog)
    {
        var wingetId = row?.Id;
        var truncated = row?.IsTruncated == true || (wingetId?.Contains(WingetOutputParser.Ellipsis) ?? false);
        // winget prints pseudo ids ("MSIX\<package full name>", "ARP\Machine\X64\{GUID}") for installs it cannot map to
        // any package in a source. Those cannot be upgraded through winget, so they are not package ids for our purposes.
        if (IsPseudoId(wingetId)) wingetId = null;
        var probe = new InstalledApp { DisplayName = name, DisplayVersion = version, Publisher = publisher, Context = context };

        var catalogEntry = catalog.FirstOrDefault(c => MatchesPolicy(c, probe, wingetId));
        var configuredEntry = configured.FirstOrDefault(c => MatchesPolicy(c, probe, wingetId));

        // No winget row, but a policy recognised the install by its detection rules: take the package id from there.
        // This is the normal case for per-user installs (VS Code user setup, Firefox per user, ...) when the inventory
        // runs as SYSTEM - the registry scan sees every user hive, but "winget list --scope user" only lists the
        // packages of the account that runs it, so those rows never get a winget id of their own.
        if (wingetId is null && !truncated)
        {
            wingetId = WingetIdFromPolicy(configuredEntry) ?? WingetIdFromPolicy(catalogEntry);
            if (wingetId is not null) origin += "+catalog";
        }

        return new DiscoveredApp
        {
            DisplayName = name,
            Version = version,
            Publisher = publisher,
            WingetId = truncated ? null : wingetId,
            WingetIdTruncated = truncated,
            AvailableVersion = string.IsNullOrWhiteSpace(row?.Available) ? null : row!.Available,
            Context = context,
            CatalogAppId = catalogEntry?.AppId,
            ConfiguredAppId = configuredEntry?.AppId,
            Origin = origin,
        };
    }

    /// <summary>A policy matches an installed app when one of its winget ids equals the row id or its detection rules match the name.</summary>
    private static bool MatchesPolicy(AppPolicy policy, InstalledApp app, string? wingetId)
    {
        if (!string.IsNullOrWhiteSpace(wingetId) && !string.IsNullOrWhiteSpace(policy.WingetId) &&
            WingetProvider.SplitIds(policy.WingetId).Any(id => id.Equals(wingetId, StringComparison.OrdinalIgnoreCase)))
            return true;
        return InstalledAppScanner.Match(policy, [app]).Count > 0;
    }

    /// <summary>The first winget id of a policy (ids may list ";"-separated alternatives), or null when it has none.</summary>
    internal static string? WingetIdFromPolicy(AppPolicy? policy)
    {
        if (policy is null || string.IsNullOrWhiteSpace(policy.WingetId)) return null;
        var first = WingetProvider.SplitIds(policy.WingetId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }

    /// <summary>True for winget's placeholder ids of packages that no source knows (MSIX\… and ARP\… entries).</summary>
    public static bool IsPseudoId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && (id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex Noise = new(@"\s*\((x64|x86|64-bit|32-bit|user|machine|current user|all users)[^)]*\)|\s+\d+(\.\d+)+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Compares display names loosely: ignores case, version suffixes and architecture hints ("7-Zip 26.02 (x64 edition)" ~ "7-Zip").</summary>
    public static bool NamesMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var na = Normalize(a);
        var nb = Normalize(b);
        return na.Length > 0 && na.Equals(nb, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string s) => Noise.Replace(s, string.Empty).Trim().TrimEnd('…');
}

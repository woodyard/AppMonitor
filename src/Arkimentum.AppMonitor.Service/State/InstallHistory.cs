using System.Globalization;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Service.State;

/// <summary>
/// The rules of the persisted install history: how an entry is made from an install outcome, how the list is kept
/// (newest first, capped) and which entries one pipe client may see. Pure and static so they are testable without a
/// service, a pipe or a state file.
/// </summary>
public static class InstallHistory
{
    /// <summary>How many entries the service keeps in its state file.</summary>
    public const int MaxEntries = 50;

    /// <summary>How many entries one state message carries to a client.</summary>
    public const int MaxSentToClient = 10;

    /// <summary>A failure message is trimmed to this many characters; the tray shows it as a tooltip only.</summary>
    public const int MaxMessageLength = 300;

    /// <summary>
    /// A new list with <paramref name="entry"/> added, newest first and capped at <see cref="MaxEntries"/>. The input is
    /// never edited: the state message is built from the current list on pipe threads without the policy lock, so the
    /// caller swaps the returned list in instead.
    /// </summary>
    public static List<InstallHistoryEntry> Append(IEnumerable<InstallHistoryEntry>? history, InstallHistoryEntry entry) =>
        Normalize(new[] { entry }.Concat(history ?? []));

    /// <summary>Newest first (stable for equal times, so a new entry stays ahead of an older one), capped at <see cref="MaxEntries"/>.</summary>
    public static List<InstallHistoryEntry> Normalize(IEnumerable<InstallHistoryEntry?>? history) =>
        (history ?? [])
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderByDescending(e => e.CompletedUtc)
            .Take(MaxEntries)
            .ToList();

    /// <summary>
    /// The entries the client of <paramref name="userSid"/> may see, newest first, at most <paramref name="max"/>: the
    /// same rule as the tracked updates - machine-wide installs for everyone, per-user installs only for their own user.
    /// </summary>
    public static List<InstallHistoryEntry> VisibleTo(IEnumerable<InstallHistoryEntry>? history, string? userSid, int max = MaxSentToClient) =>
        (history ?? [])
            .Where(e => IsVisibleTo(e, userSid))
            .OrderByDescending(e => e.CompletedUtc)
            .Take(max)
            .ToList();

    /// <summary>
    /// Gives entries recorded without an icon path (before icons existed, or backfilled from the service log) the one a
    /// scan found for the same application in the same context and, for a per-user install, the same user. Returns the
    /// list to swap in, or null when nothing changed; like <see cref="Append"/>, the input and its entries are never edited.
    /// </summary>
    public static List<InstallHistoryEntry>? FillIconPaths(IReadOnlyList<InstallHistoryEntry>? history,
        IEnumerable<(string AppId, InstallContext Context, string? UserSid, string IconPath)> found)
    {
        if (history is null || history.Count == 0 || history.All(e => e.IconPath is not null)) return null;
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in found) icons.TryAdd(IconKey(f.AppId, f.Context, f.UserSid), f.IconPath);
        if (icons.Count == 0) return null;

        var changed = false;
        var result = history.Select(e =>
        {
            if (e.IconPath is not null || !icons.TryGetValue(IconKey(e.AppId, e.Context, e.UserSid), out var icon)) return e;
            changed = true;
            return new InstallHistoryEntry
            {
                AppId = e.AppId, DisplayName = e.DisplayName, FromVersion = e.FromVersion, ToVersion = e.ToVersion,
                Succeeded = e.Succeeded, CompletedUtc = e.CompletedUtc, Context = e.Context, UserSid = e.UserSid,
                Message = e.Message, IconPath = icon,
            };
        }).ToList();
        return changed ? result : null;
    }

    private static string IconKey(string appId, InstallContext context, string? userSid) =>
        context == InstallContext.User ? $"{appId}|User|{userSid}" : $"{appId}|System";

    public static bool IsVisibleTo(InstallHistoryEntry entry, string? userSid) =>
        entry.Context != InstallContext.User || string.Equals(entry.UserSid, userSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A successful install of <paramref name="update"/>, read before the update is marked installed (that overwrites its
    /// installed version). The version is the one the provider read back when it did, else the one the install aimed for.
    /// </summary>
    public static InstallHistoryEntry Succeeded(PendingUpdate update, InstallResult result, DateTimeOffset at) =>
        Create(update, true, string.IsNullOrWhiteSpace(result.InstalledVersion) ? update.AvailableVersion : result.InstalledVersion, null, at);

    /// <summary>A failed install of <paramref name="update"/>; the version is the one it aimed for.</summary>
    public static InstallHistoryEntry Failed(PendingUpdate update, string? message, DateTimeOffset at) =>
        Create(update, false, update.AvailableVersion, Shorten(message), at);

    // ---------------------------------------------------------------- one-time backfill from the service log

    private static readonly Regex LogLine = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[\w{3}\] UpdateCoordinator: (?<msg>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StartLine = new(
        @"^Installing (?<rest>.+) -> (?<to>\S+) \((?<source>\w+), (?<ctx>System|User|Auto)(?: for (?<sid>\S+))?\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Rebuilds install history from the service's own log, for devices that installed updates before the history
    /// existed (1.1.27). The coordinator logs every install in three fixed forms, and installs run one at a time, so
    /// each start line pairs with the next result line:
    /// <c>Installing {App} {From} -> {To} ({Source}, {Context}[ for {Sid}])</c>, then
    /// <c>Installed {App} {Version}...</c> or <c>Install of {App} failed (exit {Code}): {Message}</c>.
    /// A start without a result (the service stopped mid-install) is dropped. <paramref name="appIdFor"/> maps the logged
    /// display name back to an AppId; the display name itself is used when it returns null.
    /// </summary>
    public static List<InstallHistoryEntry> ParseServiceLog(IEnumerable<string> lines, Func<string, string?>? appIdFor = null)
    {
        var entries = new List<InstallHistoryEntry>();
        (string App, string? From, string To, InstallContext Context, string? Sid)? open = null;

        foreach (var line in lines)
        {
            var m = LogLine.Match(line);
            if (!m.Success) continue;
            var msg = m.Groups["msg"].Value;

            var start = StartLine.Match(msg);
            if (start.Success)
            {
                // "{App} {From}": the installed version is the last token and may be empty ("Installing X  -> 2.0").
                var rest = start.Groups["rest"].Value;
                var cut = rest.LastIndexOf(' ');
                var app = cut < 0 ? rest : rest[..cut];
                var from = cut < 0 ? null : rest[(cut + 1)..];
                if (!Enum.TryParse<InstallContext>(start.Groups["ctx"].Value, out var context)) context = InstallContext.System;
                open = (app, string.IsNullOrWhiteSpace(from) ? null : from, start.Groups["to"].Value, context,
                    start.Groups["sid"].Success ? start.Groups["sid"].Value : null);
                continue;
            }

            if (open is not { } o) continue;
            bool succeeded;
            string? version;
            string? message = null;
            if (msg.StartsWith($"Installed {o.App} ", StringComparison.Ordinal))
            {
                succeeded = true;
                var tail = msg[$"Installed {o.App} ".Length..];
                var end = tail.IndexOfAny([' ', ':']);
                version = end < 0 ? tail : tail[..end];
            }
            else if (msg.StartsWith($"Install of {o.App} failed", StringComparison.Ordinal))
            {
                succeeded = false;
                version = o.To;
                var colon = msg.IndexOf("): ", StringComparison.Ordinal);
                message = Shorten(colon < 0 ? null : msg[(colon + 3)..]);
            }
            else continue;

            if (!DateTimeOffset.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at)) { open = null; continue; }

            entries.Add(new InstallHistoryEntry
            {
                AppId = appIdFor?.Invoke(o.App) ?? o.App,
                DisplayName = o.App,
                FromVersion = o.From,
                ToVersion = string.IsNullOrWhiteSpace(version) ? o.To : version,
                Succeeded = succeeded,
                CompletedUtc = at.ToUniversalTime(),
                Context = o.Context,
                UserSid = o.Context == InstallContext.User ? o.Sid : null,
                Message = message,
            });
            open = null;
        }
        return entries;
    }

    /// <summary>
    /// Merges backfilled entries into the current history without duplicating an install that is already there (same
    /// app, same outcome, completed within the same second), then applies the usual order and cap.
    /// </summary>
    public static List<InstallHistoryEntry> Merge(IEnumerable<InstallHistoryEntry>? history, IEnumerable<InstallHistoryEntry> backfill)
    {
        var current = (history ?? []).ToList();
        static string Key(InstallHistoryEntry e) =>
            $"{e.DisplayName}|{e.Succeeded}|{e.CompletedUtc.ToUniversalTime():yyyyMMddHHmmss}";
        var seen = current.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Normalize(current.Concat(backfill.Where(e => seen.Add(Key(e)))));
    }

    /// <summary>Single line, at most <see cref="MaxMessageLength"/> characters (with an ellipsis when cut).</summary>
    public static string? Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var flat = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= MaxMessageLength ? flat : flat[..(MaxMessageLength - 1)].TrimEnd() + "…";
    }

    private static InstallHistoryEntry Create(PendingUpdate u, bool succeeded, string? toVersion, string? message, DateTimeOffset at) => new()
    {
        AppId = u.AppId,
        DisplayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.AppId : u.DisplayName,
        FromVersion = u.InstalledVersion,
        ToVersion = toVersion,
        Succeeded = succeeded,
        CompletedUtc = at,
        Context = u.Context,
        UserSid = u.Context == InstallContext.User ? u.UserSid : null,
        Message = message,
        IconPath = u.IconPath,
    };
}

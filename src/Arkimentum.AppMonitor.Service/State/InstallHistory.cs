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
    };
}

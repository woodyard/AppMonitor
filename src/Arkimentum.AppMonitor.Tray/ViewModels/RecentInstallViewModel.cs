using System.Collections.Generic;
using System.Linq;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>
/// One line of the "Recent updates" list: a status glyph, the application, the version and when it finished.
/// Immutable; the main view model replaces the whole list when the service reports a different history.
/// </summary>
public sealed class RecentInstallViewModel
{
    public RecentInstallViewModel(InstallHistoryEntry entry)
    {
        Succeeded = entry.Succeeded;
        Glyph = entry.Succeeded ? Strings.RecentInstallSucceededGlyph : Strings.RecentInstallFailedGlyph;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.AppId : entry.DisplayName;
        // For a failure this is the version the install aimed for.
        VersionText = entry.ToVersion ?? string.Empty;
        TimeText = TimeFormat.Absolute(entry.CompletedUtc);
        ToolTip = entry.Succeeded
            ? string.IsNullOrWhiteSpace(entry.FromVersion) || string.IsNullOrWhiteSpace(entry.ToVersion)
                ? null
                : Strings.RecentInstallUpdatedFrom(entry.FromVersion!, entry.ToVersion!)
            : Strings.StateFailed(entry.Message);
    }

    public bool Succeeded { get; }

    public string Glyph { get; }

    public string DisplayName { get; }

    public string VersionText { get; }

    public string TimeText { get; }

    /// <summary>The failure reason for a failed install, "Updated from … to …" for a successful one; null when there is nothing to add.</summary>
    public string? ToolTip { get; }

    /// <summary>
    /// What the list is built from, as one comparable string: the entries plus today's date, because a time shown as
    /// "14:02" today has to read "22 Sep 14:02" tomorrow. The list is only rebuilt when this changes.
    /// </summary>
    public static string Signature(IReadOnlyList<InstallHistoryEntry> entries) =>
        DateTime.Today.ToString("yyyyMMdd") + "|" + string.Join("|", entries.Select(e =>
            $"{e.AppId}/{e.Context}/{e.UserSid}/{e.CompletedUtc.UtcTicks}/{e.Succeeded}/{e.ToVersion}/{e.DisplayName}"));
}

using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.Services;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>
/// One line of the "Recent updates" list: the application's icon (a failure badge on it for a failed install), the
/// application, the version and when it finished. Immutable apart from <see cref="Icon"/>, which arrives once it has been
/// resolved; the main view model replaces the whole list when the service reports a different history.
/// </summary>
public sealed class RecentInstallViewModel : ObservableObject
{
    /// <summary>The row's application icon, in logical pixels.</summary>
    public const int IconSize = 16;

    private ImageSource? _icon;

    /// <param name="entry">The finished install.</param>
    /// <param name="current">The tracked update of the same application, if any: it lends its icon path and process names.</param>
    /// <param name="icons">Resolves the icon.</param>
    public RecentInstallViewModel(InstallHistoryEntry entry, PendingUpdate? current, AppIconProvider icons)
    {
        Succeeded = entry.Succeeded;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.AppId : entry.DisplayName;
        Monogram = AppIconLookup.Monogram(DisplayName);
        // For a failure this is the version the install aimed for.
        VersionText = entry.ToVersion ?? string.Empty;
        TimeText = TimeFormat.Absolute(entry.CompletedUtc);
        ToolTip = entry.Succeeded
            ? string.IsNullOrWhiteSpace(entry.FromVersion) || string.IsNullOrWhiteSpace(entry.ToVersion)
                ? null
                : Strings.RecentInstallUpdatedFrom(entry.FromVersion!, entry.ToVersion!)
            : Strings.StateFailed(entry.Message);

        icons.Deliver(AppIconRequest.For(entry, current), IconSize, icon => Icon = icon);
    }

    public bool Succeeded { get; }

    public string DisplayName { get; }

    public string VersionText { get; }

    public string TimeText { get; }

    /// <summary>The failure reason for a failed install, "Updated from … to …" for a successful one; null when there is nothing to add.</summary>
    public string? ToolTip { get; }

    /// <summary>The application's own icon once it has been found; until then, or when there is none, the view shows <see cref="Monogram"/>.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public string Monogram { get; }

    /// <summary>
    /// What the list is built from, as one comparable string: the entries plus today's date, because a time shown as
    /// "14:02" today has to read "22 Sep 14:02" tomorrow. The list is only rebuilt when this changes.
    /// </summary>
    public static string Signature(IReadOnlyList<InstallHistoryEntry> entries) =>
        DateTime.Today.ToString("yyyyMMdd") + "|" + string.Join("|", entries.Select(e =>
            $"{e.AppId}/{e.Context}/{e.UserSid}/{e.CompletedUtc.UtcTicks}/{e.Succeeded}/{e.ToVersion}/{e.DisplayName}"));
}

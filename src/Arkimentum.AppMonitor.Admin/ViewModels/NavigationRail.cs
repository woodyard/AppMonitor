using Arkimentum.AppMonitor.Admin.Resources;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>Which document, if any, a page edits — and therefore which footer it gets.</summary>
public enum NavigationScope
{
    /// <summary>A page that edits nothing: Overview, Export &amp; import, Connect, Devices, Inventory, Enrollment.</summary>
    None,

    /// <summary>Edits this machine's configuration: the Apply / Discard footer.</summary>
    LocalEditor,

    /// <summary>Edits the organization's configuration: the Publish / Discard footer.</summary>
    OrganizationEditor,
}

/// <summary>One entry of the left navigation rail — or, with no page, the label of a group of entries.</summary>
public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(string title, string glyph, object page, NavigationScope scope, bool organization,
        string? automationName = null)
    {
        Title = title;
        Glyph = glyph;
        Page = page;
        Scope = scope;
        IsOrganization = organization;
        AutomationName = automationName ?? title;
    }

    private NavigationItemViewModel(string title, string? tip)
    {
        Title = title;
        Tip = tip;
        AutomationName = title;
        Glyph = string.Empty;
        Page = new object();
        IsHeader = true;
    }

    /// <summary>
    /// A non-selectable group label in the rail ("Organization", "This machine (deprecated)"), optionally with one
    /// line of explanation underneath it.
    /// </summary>
    public static NavigationItemViewModel Header(string title, string? tip = null) => new(title, tip);

    public string Title { get; }

    /// <summary>
    /// What a screen reader announces. The two organization editor entries are labelled "Settings" and
    /// "Applications" in the rail — the group header above them says which — but they must still be
    /// distinguishable from the local pages of the same name when read on their own.
    /// </summary>
    public string AutomationName { get; }

    public string Glyph { get; }

    public object Page { get; }

    public NavigationScope Scope { get; }

    /// <summary>True for the pages that work on the cloud rather than on this machine; drives the scope badge.</summary>
    public bool IsOrganization { get; }

    public bool IsHeader { get; }

    /// <summary>One line under a group header; only the deprecated local group has one. Null otherwise.</summary>
    public string? Tip { get; }

    public bool HasTip => !string.IsNullOrEmpty(Tip);

    /// <summary>True for the pages that edit the shared local configuration document and therefore share its footer.</summary>
    public bool SharesEditor => Scope == NavigationScope.LocalEditor;

    public bool SharesOrganizationEditor => Scope == NavigationScope.OrganizationEditor;
}

/// <summary>
/// Builds the left rail out of the two groups of pages.
///
/// <para>
/// Every setting is managed centrally, so the organization pages are the console. The per-machine pages are
/// deprecated: they only appear when <c>--local</c> was given, they come <em>after</em> the organization group, and
/// their header says so. With a single group there is no header at all — one label over the whole rail is noise.
/// </para>
///
/// <para>This is deliberately free of WPF: it is the one piece of the shell that can be unit-tested.</para>
/// </summary>
public static class NavigationRail
{
    /// <summary>
    /// The rail, in order. <paramref name="local"/> is null or empty in the normal, organization-only console.
    /// </summary>
    public static IReadOnlyList<NavigationItemViewModel> Compose(
        IReadOnlyList<NavigationItemViewModel> organization,
        IReadOnlyList<NavigationItemViewModel>? local)
    {
        ArgumentNullException.ThrowIfNull(organization);

        if (local is not { Count: > 0 }) return [.. organization];

        return
        [
            NavigationItemViewModel.Header(Strings.NavGroupOrganization),
            .. organization,
            NavigationItemViewModel.Header(Strings.NavGroupLocalDeprecated, Strings.NavGroupLocalTip),
            .. local,
        ];
    }
}

using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    private NavigationItemViewModel(string title)
    {
        Title = title;
        AutomationName = title;
        Glyph = string.Empty;
        Page = new object();
        IsHeader = true;
    }

    /// <summary>A non-selectable group label in the rail ("This machine", "Organization").</summary>
    public static NavigationItemViewModel Header(string title) => new(title);

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

    /// <summary>True for the pages that edit the shared local configuration document and therefore share its footer.</summary>
    public bool SharesEditor => Scope == NavigationScope.LocalEditor;

    public bool SharesOrganizationEditor => Scope == NavigationScope.OrganizationEditor;
}

/// <summary>
/// The shell: the navigation rail, the page host, the testing-mode banner, the scope badge that says which
/// configuration the current page edits, and the two footers — Apply / Discard for this machine's document,
/// Publish / Discard for the organization's.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel> _log;
    private readonly IDialogService _dialogs;
    private readonly AdminIpcService _ipc;
    private readonly IServiceProvider _services;
    private readonly Dispatcher _dispatcher;
    private readonly CloudSession _session;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _discardCommand;
    private readonly RelayCommand _scanCommand;
    private readonly RelayCommand _signOutCommand;

    private NavigationItemViewModel _selectedItem;
    private readonly CloudConnectViewModel _connect;
    private readonly NavigationItemViewModel _devicesItem;
    private string? _savedNotice;
    private bool _reverting;

    public MainViewModel(
        ILogger<MainViewModel> log,
        Dispatcher dispatcher,
        CommandLineOptions options,
        ConfigurationEditor editor,
        IDialogService dialogs,
        AdminIpcService ipc,
        IServiceProvider services,
        OverviewViewModel overview,
        SettingsPageViewModel settings,
        ApplicationsViewModel applications,
        ExportImportViewModel exportImport,
        CloudSession session,
        CloudConnectViewModel connect,
        OrganizationDevicesViewModel devices,
        OrganizationActivityViewModel activity,
        OrganizationInventoryViewModel inventory,
        OrganizationConfigViewModel organizationConfig,
        OrganizationSettingsViewModel organizationSettings,
        OrganizationApplicationsViewModel organizationApplications,
        OrganizationEnrollmentViewModel enrollment)
    {
        _log = log;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _ipc = ipc;
        _services = services;
        _session = session;
        Editor = editor;
        Organization = organizationConfig;
        TestingMode = options.UserConfig;

        NavigationItems =
        [
            NavigationItemViewModel.Header(Strings.NavGroupLocal),
            new NavigationItemViewModel(Strings.NavOverview, Strings.GlyphOverview, overview, NavigationScope.None, false),
            new NavigationItemViewModel(Strings.NavSettings, Strings.GlyphSettings, settings, NavigationScope.LocalEditor, false),
            new NavigationItemViewModel(Strings.NavApplications, Strings.GlyphApplications, applications, NavigationScope.LocalEditor, false),
            new NavigationItemViewModel(Strings.NavExportImport, Strings.GlyphExportImport, exportImport, NavigationScope.None, false),
            NavigationItemViewModel.Header(Strings.NavGroupOrganization),
            new NavigationItemViewModel(Strings.NavConnect, Strings.GlyphConnect, connect, NavigationScope.None, true),
            new NavigationItemViewModel(Strings.NavDevices, Strings.GlyphDevices, devices, NavigationScope.None, true),
            new NavigationItemViewModel(Strings.NavActivity, Strings.GlyphActivity, activity, NavigationScope.None, true),
            new NavigationItemViewModel(Strings.NavInventory, Strings.GlyphInventory, inventory, NavigationScope.None, true),
            new NavigationItemViewModel(Strings.NavOrganizationSettings, Strings.GlyphOrgSettings, organizationSettings,
                NavigationScope.OrganizationEditor, true, Strings.OrganizationSettingsTitle),
            new NavigationItemViewModel(Strings.NavOrganizationApplications, Strings.GlyphApplications, organizationApplications,
                NavigationScope.OrganizationEditor, true, Strings.OrganizationApplicationsTitle),
            new NavigationItemViewModel(Strings.NavEnrollment, Strings.GlyphEnrollment, enrollment, NavigationScope.None, true),
        ];
        _selectedItem = NavigationItems.First(i => !i.IsHeader);

        _applyCommand = new RelayCommand(Apply, () => Editor.CanApply);
        _discardCommand = new RelayCommand(Discard, () => Editor.IsDirty);
        _connect = connect;
        _devicesItem = NavigationItems.First(i => ReferenceEquals(i.Page, devices));
        _connect.OrganizationChosen += () => SelectedItem = _devicesItem;

        _scanCommand = new RelayCommand(RequestScan, () => _ipc.IsConnected);
        _signOutCommand = new RelayCommand(SignOut, () => _session.IsSignedIn);
        AboutCommand = new RelayCommand(ShowAbout);

        Editor.Changed += OnEditorChanged;
        _ipc.ConnectionChanged += _ => _dispatcher.BeginInvoke(() => _scanCommand.RaiseCanExecuteChanged());
        _session.Changed += OnSessionChanged;
    }

    public ConfigurationEditor Editor { get; }

    /// <summary>The organization document, its Publish / Discard footer and its version history.</summary>
    public OrganizationConfigViewModel Organization { get; }

    public string Title => Strings.WindowTitle;

    public string WordmarkBrand => Strings.WordmarkBrand;

    public string WordmarkProduct => Strings.WordmarkProduct;

    /// <summary>The header line follows the scope, so the two halves of the console never look like one.</summary>
    public string Subtitle => _selectedItem.IsOrganization ? Strings.HeaderSubtitleOrganization : Strings.HeaderSubtitle;

    public bool TestingMode { get; }

    public string TestingModeBanner => Strings.TestingModeBanner;

    public string TestingModeDetail => Strings.TestingModeDetail;

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (value is null || ReferenceEquals(_selectedItem, value)) return;
            // Group labels are in the same list as the pages; a click or an arrow key must never land on one.
            if (value.IsHeader) { Revert(); return; }
            if (!_reverting && !ConfirmLeaving(value)) { Revert(); return; }
            if (_selectedItem.SharesEditor && !value.SharesEditor && Editor.IsDirty) Editor.Discard();

            _selectedItem = value;
            SavedNotice = null;
            Activate(value);
            OnPropertyChanged(nameof(SelectedItem), nameof(CurrentPage), nameof(ShowEditorFooter),
                nameof(ShowOrganizationFooter), nameof(ScopeBadge), nameof(ScopeTooltip), nameof(IsOrganizationScope),
                nameof(Subtitle));
        }
    }

    /// <summary>Puts the rail's selection back once the ListBox has finished its own update.</summary>
    private void Revert() => _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        _reverting = true;
        OnPropertyChanged(nameof(SelectedItem));
        _reverting = false;
    });

    /// <summary>False when the page being left has unsaved changes the administrator wants to keep.</summary>
    private bool ConfirmLeaving(NavigationItemViewModel next)
    {
        if (_selectedItem.SharesEditor && !next.SharesEditor && Editor.IsDirty)
            return ConfirmDiscard(Strings.ConfirmDiscardBody);
        if (_selectedItem.SharesOrganizationEditor && !next.SharesOrganizationEditor && Organization.Editor.IsDirty)
            return Organization.ConfirmLeave();
        return true;
    }

    /// <summary>Organization pages fetch what they need the first time they are actually looked at.</summary>
    private void Activate(NavigationItemViewModel item)
    {
        switch (item.Page)
        {
            case OrganizationDevicesViewModel devices: devices.EnsureLoaded(); break;
            case OrganizationActivityViewModel activity: activity.EnsureLoaded(); break;
            case OrganizationInventoryViewModel inventory: inventory.EnsureLoaded(); break;
            case OrganizationSettingsViewModel: case OrganizationApplicationsViewModel: Organization.EnsureLoaded(); break;
            case OrganizationEnrollmentViewModel enrollment: enrollment.EnsureLoaded(); break;
        }
    }

    public object CurrentPage => _selectedItem.Page;

    /// <summary>The Apply / Discard footer belongs to the pages that edit this machine's document.</summary>
    public bool ShowEditorFooter => _selectedItem.SharesEditor;

    /// <summary>The Publish / Discard footer belongs to the pages that edit the organization's document.</summary>
    public bool ShowOrganizationFooter => _selectedItem.SharesOrganizationEditor;

    // ---------------------------------------------------------------- scope badge

    /// <summary>True while an organization page is open; the badge turns from neutral to sky.</summary>
    public bool IsOrganizationScope => _selectedItem.IsOrganization;

    /// <summary>"Local machine" or "Organization: Contoso" — so it is never a guess which one is being edited.</summary>
    public string ScopeBadge => !_selectedItem.IsOrganization ? Strings.ScopeLocal
        : _session.Organization is { } organization ? Strings.ScopeOrganization(organization.Name)
        : Strings.ScopeOrganizationNone;

    public string ScopeTooltip => _selectedItem.IsOrganization ? Strings.ScopeOrganizationTip : Strings.ScopeLocalTip;

    // ---------------------------------------------------------------- navigation footer

    public bool IsSignedIn => _session.IsSignedIn;

    public string SignedInAccount => _session.SignedInAccount ?? string.Empty;

    public ICommand SignOutCommand => _signOutCommand;

    public ICommand ApplyCommand => _applyCommand;

    public ICommand DiscardCommand => _discardCommand;

    public ICommand ScanNowCommand => _scanCommand;

    public ICommand AboutCommand { get; }

    public string ApplyText => Strings.ButtonApply;

    public string DiscardText => Strings.ButtonDiscard;

    public string ScanNowText => Strings.SavedScanLink;

    public string? SavedNotice
    {
        get => _savedNotice;
        private set
        {
            if (SetProperty(ref _savedNotice, value)) OnPropertyChanged(nameof(HasSavedNotice));
        }
    }

    public bool HasSavedNotice => !string.IsNullOrEmpty(_savedNotice);

    // ---------------------------------------------------------------- commands

    private void Apply()
    {
        try
        {
            Editor.Apply();
            SavedNotice = Strings.SavedNotice;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applying the configuration failed.");
            _dialogs.ShowMessage(Strings.ProductName, Strings.WriteFailed(ex.Message), DialogTone.Critical);
        }
    }

    private void Discard()
    {
        if (!ConfirmDiscard(Strings.ConfirmDiscardBody)) return;
        Editor.Discard();
        SavedNotice = null;
    }

    private async void RequestScan()
    {
        var sent = await _ipc.RequestScanAsync().ConfigureAwait(true);
        SavedNotice = sent ? Strings.ScanRequested : Strings.ScanRequestFailed;
    }

    private async void SignOut()
    {
        if (Organization.Editor.IsDirty && !Organization.ConfirmLeave()) return;
        try
        {
            await _session.SignOutAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Signing out failed.");
        }
    }

    private void ShowAbout() => _dialogs.ShowDialog(_services.GetRequiredService<AboutViewModel>());

    /// <summary>Called by the window before it closes; false keeps it open.</summary>
    public bool ConfirmClose() =>
        (!Editor.IsDirty || ConfirmDiscard(Strings.ConfirmDiscardCloseBody)) &&
        (!Organization.Editor.IsDirty || Organization.ConfirmLeave());

    private bool ConfirmDiscard(string body) =>
        _dialogs.Confirm(Strings.ConfirmDiscardTitle, body, Strings.Discard, Strings.KeepEditing);

    private void OnEditorChanged()
    {
        _applyCommand.RaiseCanExecuteChanged();
        _discardCommand.RaiseCanExecuteChanged();
        if (Editor.IsDirty) SavedNotice = null;
    }

    /// <summary>
    /// Called once the window is on screen: restores the previous cloud sign-in from the token cache and, when that
    /// leaves an organization selected and the administrator has not navigated anywhere yet, opens its Devices page.
    /// An administrator who signed in yesterday should not have to press Sign in again today.
    /// </summary>
    public async Task RestoreSessionAsync()
    {
        var restored = await _connect.RestoreAsync().ConfigureAwait(true);
        if (!restored || _session.Organization is null) return;
        if (ReferenceEquals(_selectedItem, NavigationItems.First(i => !i.IsHeader))) SelectedItem = _devicesItem;
    }

    private void OnSessionChanged()
    {
        _signOutCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsSignedIn), nameof(SignedInAccount), nameof(ScopeBadge));
        // An organization page that is already open starts loading as soon as one is picked.
        Activate(_selectedItem);
    }
}

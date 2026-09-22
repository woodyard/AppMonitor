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

/// <summary>
/// The shell: the navigation rail, the page host, the testing-mode banner, the scope badge that says which
/// configuration the current page edits, and the two footers — Publish / Discard for the organization's document,
/// Apply / Discard for the deprecated per-machine one.
///
/// <para>
/// The console is the organization console. The per-machine pages only exist under <c>--local</c>, and nothing
/// that belongs to them — not their view models, not the local configuration document, not the connection to the
/// service — is constructed unless that switch was given: an organization-only run must leave this machine alone.
/// </para>
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
    private readonly RelayCommand _openWebConsoleCommand;

    private NavigationItemViewModel _selectedItem;
    private readonly CloudConnectViewModel _connect;
    private readonly NavigationItemViewModel _devicesItem;
    private string? _savedNotice;
    private bool _reverting;

    public MainViewModel(
        ILogger<MainViewModel> log,
        Dispatcher dispatcher,
        CommandLineOptions options,
        IDialogService dialogs,
        AdminIpcService ipc,
        IServiceProvider services,
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
        Organization = organizationConfig;
        TestingMode = options.UserConfig;
        ShowLocalPages = options.Local;

        NavigationItemViewModel[] organizationItems =
        [
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

        // Resolved only under --local: touching the container for them is what reads HKLM and builds the local
        // configuration document, and an organization-only console must do neither.
        NavigationItemViewModel[]? localItems = null;
        if (ShowLocalPages)
        {
            Editor = _services.GetRequiredService<ConfigurationEditor>();
            localItems =
            [
                new NavigationItemViewModel(Strings.NavOverview, Strings.GlyphOverview,
                    _services.GetRequiredService<OverviewViewModel>(), NavigationScope.None, false),
                new NavigationItemViewModel(Strings.NavSettings, Strings.GlyphSettings,
                    _services.GetRequiredService<SettingsPageViewModel>(), NavigationScope.LocalEditor, false, Strings.LocalSettingsAutomationName),
                new NavigationItemViewModel(Strings.NavApplications, Strings.GlyphApplications,
                    _services.GetRequiredService<ApplicationsViewModel>(), NavigationScope.LocalEditor, false, Strings.LocalApplicationsAutomationName),
                new NavigationItemViewModel(Strings.NavExportImport, Strings.GlyphExportImport,
                    _services.GetRequiredService<ExportImportViewModel>(), NavigationScope.None, false),
            ];
            _log.LogWarning("--local is deprecated: the per-machine pages are shown after the organization pages. " +
                            "Manage settings centrally instead.");
        }

        NavigationItems = [.. NavigationRail.Compose(organizationItems, localItems)];
        _selectedItem = NavigationItems.First(i => !i.IsHeader);

        _applyCommand = new RelayCommand(Apply, () => Editor?.CanApply == true);
        _discardCommand = new RelayCommand(Discard, () => Editor?.IsDirty == true);
        _connect = connect;
        _devicesItem = NavigationItems.First(i => ReferenceEquals(i.Page, devices));
        _connect.OrganizationChosen += () => SelectedItem = _devicesItem;

        _scanCommand = new RelayCommand(RequestScan, () => _ipc.IsConnected);
        _signOutCommand = new RelayCommand(SignOut, () => _session.IsSignedIn);
        _openWebConsoleCommand = new RelayCommand(OpenWebConsole, () => HasWebAdminConsole);
        AboutCommand = new RelayCommand(ShowAbout);

        if (Editor is not null) Editor.Changed += OnEditorChanged;
        _ipc.ConnectionChanged += _ => _dispatcher.BeginInvoke(() => _scanCommand.RaiseCanExecuteChanged());
        _session.Changed += OnSessionChanged;
    }

    /// <summary>
    /// The deprecated per-machine document, or null when the console runs without <c>--local</c>. The Apply /
    /// Discard footer is hidden in that case, so the bindings against it never resolve.
    /// </summary>
    public ConfigurationEditor? Editor { get; }

    /// <summary>True when <c>--local</c> put the deprecated "This machine" group in the rail.</summary>
    public bool ShowLocalPages { get; }

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
            if (_selectedItem.SharesEditor && !value.SharesEditor && Editor?.IsDirty == true) Editor.Discard();

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
        if (_selectedItem.SharesEditor && !next.SharesEditor && Editor?.IsDirty == true)
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

    // ---------------------------------------------------------------- the browser console

    /// <summary>
    /// The browser-based admin console this deployment hosts, as the server advertises it in
    /// <c>/public/auth-config</c>. Empty until the console has connected — and for a deployment that hosts none.
    /// </summary>
    public string WebAdminUrl => _session.AuthConfig?.WebAdminUrl ?? string.Empty;

    public bool HasWebAdminConsole => ExternalLink.IsBrowsable(WebAdminUrl);

    public string OpenWebConsoleText => Strings.ButtonOpenWebConsole;

    public ICommand OpenWebConsoleCommand => _openWebConsoleCommand;

    private void OpenWebConsole()
    {
        if (!ExternalLink.TryOpen(WebAdminUrl, _log))
            _dialogs.ShowMessage(Strings.ProductName, Strings.WebConsoleOpenFailed(WebAdminUrl), DialogTone.Warning);
    }

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
            Editor!.Apply();
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
        Editor!.Discard();
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
        (Editor?.IsDirty != true || ConfirmDiscard(Strings.ConfirmDiscardCloseBody)) &&
        (!Organization.Editor.IsDirty || Organization.ConfirmLeave());

    private bool ConfirmDiscard(string body) =>
        _dialogs.Confirm(Strings.ConfirmDiscardTitle, body, Strings.Discard, Strings.KeepEditing);

    private void OnEditorChanged()
    {
        _applyCommand.RaiseCanExecuteChanged();
        _discardCommand.RaiseCanExecuteChanged();
        if (Editor?.IsDirty == true) SavedNotice = null;
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
        _openWebConsoleCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsSignedIn), nameof(SignedInAccount), nameof(ScopeBadge),
            nameof(WebAdminUrl), nameof(HasWebAdminConsole));
        // An organization page that is already open starts loading as soon as one is picked.
        Activate(_selectedItem);
    }
}

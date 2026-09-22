using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One organization the signed-in administrator may manage.</summary>
public sealed class OrganizationRowViewModel : ObservableObject
{
    public OrganizationRowViewModel(OrganizationSummary summary)
    {
        Summary = summary;
        Name = summary.Name;
        OrganizationId = summary.OrganizationId.ToString();
        Devices = Strings.OrganizationDeviceSummary(summary.DeviceCount, summary.DevicesWithPendingUpdates, summary.DevicesNotSeenIn7Days);
        ConfigVersion = string.IsNullOrWhiteSpace(summary.ConfigVersion) ? Strings.None : summary.ConfigVersion!;
        HasStaleDevices = summary.DevicesNotSeenIn7Days > 0;
    }

    public OrganizationSummary Summary { get; }
    public string Name { get; }
    public string OrganizationId { get; }
    public string Devices { get; }
    public string ConfigVersion { get; }
    public bool HasStaleDevices { get; }
}

/// <summary>
/// The Connect page: point the console at an AppMonitor deployment, sign in with Entra ID, pick the organization.
///
/// <para>
/// The server URL is pre-filled from this machine's own <c>CloudServerUrl</c> when the agent here is already
/// enrolled, and otherwise from what this administrator last used. Connecting reads the server's public sign-in
/// configuration; signing in tries the MSAL cache first and only opens the browser when that comes up empty, so a
/// returning administrator usually gets straight to the organization list.
/// </para>
/// </summary>
public sealed class CloudConnectViewModel : ObservableObject
{
    private readonly ILogger<CloudConnectViewModel> _log;
    private readonly CloudSession _session;
    private readonly AdminPreferences _preferences;
    private readonly IDialogService _dialogs;

    private readonly RelayCommand _connectCommand;
    private readonly RelayCommand _signInCommand;
    private readonly RelayCommand _signOutCommand;
    private readonly RelayCommand _reloadCommand;
    private readonly RelayCommand _useCommand;
    private readonly RelayCommand _createCommand;
    private readonly RelayCommand _openWebConsoleCommand;
    private readonly OrganizationEnrollmentViewModel _enrollment;

    private string _serverUrl;
    private bool _isBusy;
    private string? _status;
    private string? _error;
    private OrganizationRowViewModel? _selected;

    public CloudConnectViewModel(ILogger<CloudConnectViewModel> log, CloudSession session, AdminPreferences preferences,
        IDialogService dialogs, OrganizationEnrollmentViewModel enrollment)
    {
        _log = log;
        _session = session;
        _preferences = preferences;
        _dialogs = dialogs;
        _enrollment = enrollment;
        _serverUrl = preferences.InitialServerUrl();

        _connectCommand = new RelayCommand(Connect, () => !_isBusy && _serverUrl.Trim().Length > 0);
        _signInCommand = new RelayCommand(SignIn, () => !_isBusy && _serverUrl.Trim().Length > 0);
        _signOutCommand = new RelayCommand(SignOut, () => !_isBusy && _session.IsSignedIn);
        _reloadCommand = new RelayCommand(ReloadOrganizations, () => !_isBusy && _session.IsSignedIn);
        _useCommand = new RelayCommand(UseSelected, () => !_isBusy && _selected is not null);
        _createCommand = new RelayCommand(CreateOrganization, () => !_isBusy && IsGlobalAdmin);
        _openWebConsoleCommand = new RelayCommand(OpenWebConsole, () => HasWebAdminConsole);

        _session.Changed += OnSessionChanged;
    }

    public string Title => Strings.ConnectTitle;

    public string Subtitle => Strings.ConnectSubtitle;

    public string ServerUrl
    {
        get => _serverUrl;
        set
        {
            if (SetProperty(ref _serverUrl, value ?? string.Empty)) RaiseCanExecuteChanged();
        }
    }

    public string ServerUrlSource => _preferences.ServerUrlComesFromMachine
        ? Strings.ServerUrlFromMachine
        : string.IsNullOrWhiteSpace(_preferences.ReadRemembered()) ? Strings.ServerUrlHint : Strings.ServerUrlRemembered;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCanExecuteChanged();
        }
    }

    public string StatusText => _status ?? (IsSignedIn ? Strings.ConnectedTo(_session.ServerUrl ?? string.Empty) : Strings.ConnectNotConnected);

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    // ---------------------------------------------------------------- signed-in state

    public bool IsSignedIn => _session.IsSignedIn;

    public bool HasAuthConfig => _session.AuthConfig is not null;

    public string SignedInAs => _session.SignedInDisplayName is { Length: > 0 } name
        ? $"{name} ({_session.SignedInAccount})"
        : _session.SignedInAccount ?? Strings.None;

    public string TenantText => _session.Me?.TenantId ?? Strings.None;

    public bool IsGlobalAdmin => _session.Me?.IsGlobalAdmin == true;

    public string AuthorityText => _session.AuthConfig?.Authority ?? Strings.None;

    public string ScopeText => _session.AuthConfig?.Scope ?? Strings.None;

    public string ClientIdText => _session.AuthConfig?.ClientId ?? Strings.None;

    // ---------------------------------------------------------------- the browser console

    /// <summary>
    /// Where this deployment's browser-based admin console lives, if it hosts one: the server says so in
    /// <c>webAdminUrl</c>. That console is the one that manages settings centrally from any device, so the page
    /// offers it as soon as the server has named it — before signing in here, too.
    /// </summary>
    public string WebAdminUrl => _session.AuthConfig?.WebAdminUrl ?? string.Empty;

    public bool HasWebAdminConsole => ExternalLink.IsBrowsable(WebAdminUrl);

    public ICommand OpenWebConsoleCommand => _openWebConsoleCommand;

    private void OpenWebConsole()
    {
        if (!ExternalLink.TryOpen(WebAdminUrl, _log))
            _dialogs.ShowMessage(Strings.ProductName, Strings.WebConsoleOpenFailed(WebAdminUrl), DialogTone.Warning);
    }

    public ObservableCollection<OrganizationRowViewModel> Organizations { get; } = [];

    public bool HasOrganizations => Organizations.Count > 0;

    public OrganizationRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            RaiseCanExecuteChanged();
        }
    }

    public ICommand ConnectCommand => _connectCommand;

    public ICommand SignInCommand => _signInCommand;

    public ICommand SignOutCommand => _signOutCommand;

    public ICommand ReloadOrganizationsCommand => _reloadCommand;

    public ICommand UseOrganizationCommand => _useCommand;

    /// <summary>Only global administrators may create an organization, so the button only appears for them.</summary>
    public ICommand CreateOrganizationCommand => _createCommand;

    // ---------------------------------------------------------------- startup

    /// <summary>
    /// Runs once when the console opens: connects to the remembered (or machine-configured) server and signs in
    /// silently from the MSAL token cache, so an administrator who signed in before lands in organization mode
    /// without touching the Sign in button. Never opens a browser: when the cache has nothing usable the page simply
    /// waits for Sign in, exactly as before. Returns true when a session was restored.
    /// </summary>
    public async Task<bool> RestoreAsync()
    {
        if (_serverUrl.Trim().Length == 0 || _session.IsSignedIn || IsBusy) return false;

        IsBusy = true;
        Error = null;
        SetStatus(Strings.ConnectRestoring);
        try
        {
            await _session.ConnectAsync(ServerUrl, CancellationToken.None).ConfigureAwait(true);
            await _session.SignInAsync(allowInteractive: false, CancellationToken.None).ConfigureAwait(true);
            SetStatus(Strings.ConnectedTo(_session.ServerUrl ?? string.Empty));
            _log.LogInformation("Restored the previous sign-in silently as {Account}.", _session.SignedInAccount);
            return true;
        }
        catch (AuthenticationRequiredException)
        {
            _log.LogInformation("No usable cached sign-in for {Url}; waiting for the administrator to sign in.", _session.ServerUrl);
            SetStatus(Strings.CloudSignInRequired);
            return false;
        }
        catch (Exception ex)
        {
            // Offline, a wrong URL, the server down: say so and leave the page fully usable.
            _log.LogWarning(ex, "Restoring the previous sign-in failed.");
            Fail(ex, "Restoring the previous sign-in failed.");
            return false;
        }
        finally
        {
            IsBusy = false;
            RefreshAll();
        }
    }

    // ---------------------------------------------------------------- commands

    private async void Connect()
    {
        IsBusy = true;
        Error = null;
        SetStatus(Strings.ConnectWorking);
        try
        {
            var config = await _session.ConnectAsync(ServerUrl, CancellationToken.None).ConfigureAwait(true);
            SetStatus(Strings.ConnectedTo(_session.ServerUrl ?? string.Empty));
            _log.LogInformation("Connected to {Url}; sign-in uses client {ClientId}.", _session.ServerUrl, config.ClientId);
        }
        catch (Exception ex)
        {
            Fail(ex, "Reading the server's sign-in configuration failed.");
        }
        finally
        {
            IsBusy = false;
            RefreshAll();
        }
    }

    /// <summary>
    /// Connect (when needed), then sign in. The silent attempt runs first; only if the cache has nothing usable
    /// does MSAL open the default browser.
    /// </summary>
    private async void SignIn()
    {
        IsBusy = true;
        Error = null;
        SetStatus(Strings.ConnectWorking);
        try
        {
            if (_session.AuthConfig is null || !string.Equals(_session.ServerUrl, CloudSession.Normalise(ServerUrl), StringComparison.OrdinalIgnoreCase))
                await _session.ConnectAsync(ServerUrl, CancellationToken.None).ConfigureAwait(true);

            try
            {
                await _session.SignInAsync(allowInteractive: false, CancellationToken.None).ConfigureAwait(true);
            }
            catch (AuthenticationRequiredException)
            {
                _log.LogInformation("No usable cached token; opening the system browser for an interactive sign-in.");
                await _session.SignInAsync(allowInteractive: true, CancellationToken.None).ConfigureAwait(true);
            }
            SetStatus(Strings.ConnectedTo(_session.ServerUrl ?? string.Empty));
        }
        catch (Exception ex)
        {
            Fail(ex, "Signing in failed.");
        }
        finally
        {
            IsBusy = false;
            RefreshAll();
        }
    }

    private async void SignOut()
    {
        IsBusy = true;
        Error = null;
        try
        {
            await _session.SignOutAsync(CancellationToken.None).ConfigureAwait(true);
            SetStatus(Strings.ConnectNotConnected);
        }
        catch (Exception ex)
        {
            Fail(ex, "Signing out failed.");
        }
        finally
        {
            IsBusy = false;
            RefreshAll();
        }
    }

    private async void ReloadOrganizations()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var organizations = await _session.GetOrganizationsAsync(CancellationToken.None).ConfigureAwait(true);
            if (_session.Me is { } me) me.Organizations = organizations;
            FillOrganizations(organizations);
        }
        catch (Exception ex)
        {
            Fail(ex, "Reloading the organization list failed.");
        }
        finally
        {
            IsBusy = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Raised when the administrator presses "Manage this organization", after the session has the organization.
    /// The main window answers by opening the organization's pages: with a single organization the session already
    /// had it selected at sign-in, so without this the button appeared to do nothing.
    /// </summary>
    public event Action? OrganizationChosen;

    private void UseSelected()
    {
        if (_selected is null) return;
        _session.SelectOrganization(_selected.Summary);
        _log.LogInformation("Managing organization {Name} ({Id}).", _selected.Summary.Name, _selected.Summary.OrganizationId);
        OrganizationChosen?.Invoke();
    }

    /// <summary>
    /// Creates an organization and selects it. The response carries the enrollment key — the only time the server
    /// ever returns it — so it goes straight to the Enrollment page, which is built to show it once and copy it.
    /// </summary>
    private async void CreateOrganization()
    {
        var input = new TextInputViewModel(Strings.CreateOrganizationTitle, Strings.CreateOrganizationPrompt,
            string.Empty, Strings.ButtonCreate, text => text.Trim().Length == 0 ? Strings.CreateOrganizationEmpty : null);
        if (!_dialogs.ShowDialog(input)) return;

        IsBusy = true;
        Error = null;
        try
        {
            var created = await _session.CreateOrganizationAsync(input.Text.Trim(), null, CancellationToken.None).ConfigureAwait(true);
            _log.LogInformation("Created organization {Name} ({Id}); the one-time enrollment key was returned.",
                created.Organization.Name, created.Organization.OrganizationId);

            if (_session.Me is { } me) me.Organizations = [.. me.Organizations.Where(o => o.OrganizationId != created.Organization.OrganizationId), created.Organization];
            _session.SelectOrganization(created.Organization);
            FillOrganizations(_session.Me?.Organizations ?? [created.Organization]);
            _enrollment.AcceptCreated(created.Enrollment);
            SetStatus(Strings.OrganizationCreated(created.Organization.Name));
        }
        catch (Exception ex)
        {
            Fail(ex, "Creating the organization failed.");
        }
        finally
        {
            IsBusy = false;
            RefreshAll();
        }
    }

    // ---------------------------------------------------------------- plumbing

    private void Fail(Exception exception, string logMessage)
    {
        Error = CloudSession.Describe(exception);
        SetStatus(null);
        _log.LogWarning(exception, "{Message}", logMessage);
    }

    private void SetStatus(string? status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnSessionChanged()
    {
        FillOrganizations(_session.Me?.Organizations ?? []);
        RefreshAll();
    }

    private void FillOrganizations(IReadOnlyList<OrganizationSummary> organizations)
    {
        Organizations.Clear();
        foreach (var organization in organizations.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase))
            Organizations.Add(new OrganizationRowViewModel(organization));

        _selected = _session.OrganizationId is { } id
            ? Organizations.FirstOrDefault(o => o.Summary.OrganizationId == id)
            : Organizations.FirstOrDefault();
        OnPropertyChanged(nameof(Selected), nameof(HasOrganizations));
    }

    private void RefreshAll()
    {
        OnPropertyChanged(
            nameof(IsSignedIn), nameof(HasAuthConfig), nameof(SignedInAs), nameof(TenantText), nameof(IsGlobalAdmin),
            nameof(AuthorityText), nameof(ScopeText), nameof(ClientIdText), nameof(StatusText), nameof(HasOrganizations),
            nameof(ServerUrlSource), nameof(WebAdminUrl), nameof(HasWebAdminConsole));
        RaiseCanExecuteChanged();
    }

    private void RaiseCanExecuteChanged()
    {
        _connectCommand.RaiseCanExecuteChanged();
        _signInCommand.RaiseCanExecuteChanged();
        _signOutCommand.RaiseCanExecuteChanged();
        _reloadCommand.RaiseCanExecuteChanged();
        _useCommand.RaiseCanExecuteChanged();
        _createCommand.RaiseCanExecuteChanged();
        _openWebConsoleCommand.RaiseCanExecuteChanged();
    }
}

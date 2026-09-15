using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One generated provisioning artefact: a heading, why you would use it, and the text to copy.</summary>
public sealed class SnippetViewModel
{
    public SnippetViewModel(string heading, string caption, string text)
    {
        Heading = heading;
        Caption = caption;
        Text = text;
    }

    public string Heading { get; }
    public string Caption { get; }
    public string Text { get; }
}

/// <summary>
/// The Enrollment page: the three values a device needs to join the organization, and the four shapes an
/// administrator actually deploys them in.
///
/// <para>
/// The key is masked until "Show", because this page is the one most likely to be on a screen during a
/// screen share. The server only ever returns the key itself right after it is created or rotated, so a page
/// opened later shows the key status but not the key — the snippets then carry a placeholder, which is the honest
/// thing to put there.
/// </para>
/// </summary>
public sealed class OrganizationEnrollmentViewModel : ObservableObject
{
    private readonly ILogger<OrganizationEnrollmentViewModel> _log;
    private readonly CloudSession _session;
    private readonly IDialogService _dialogs;

    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _rotateCommand;
    private readonly RelayCommand _toggleKeyCommand;
    private readonly RelayCommand<string> _copyCommand;

    private EnrollmentInfoResponse? _info;
    private bool _keyVisible;
    private bool _isBusy;
    private string? _error;
    private string? _notice;
    private Guid? _loadedOrganization;

    public OrganizationEnrollmentViewModel(ILogger<OrganizationEnrollmentViewModel> log, CloudSession session, IDialogService dialogs)
    {
        _log = log;
        _session = session;
        _dialogs = dialogs;

        _refreshCommand = new RelayCommand(Reload, () => !_isBusy && _session.OrganizationId is not null);
        _rotateCommand = new RelayCommand(Rotate, () => !_isBusy && _session.OrganizationId is not null);
        _toggleKeyCommand = new RelayCommand(() => KeyVisible = !KeyVisible, () => HasKey);
        _copyCommand = new RelayCommand<string>(Copy, text => !string.IsNullOrEmpty(text));

        _session.Changed += OnSessionChanged;
    }

    public string Title => Strings.EnrollmentTitle;

    public string Subtitle => Strings.EnrollmentSubtitle;

    public bool IsSignedIn => _session.OrganizationId is not null;

    public bool HasInfo => _info is not null;

    public string OrganizationName => _session.OrganizationName;

    public string OrganizationIdText => _info?.OrganizationId.ToString() ?? _session.OrganizationId?.ToString() ?? Strings.None;

    public string ServerUrlText => _info?.ServerUrl ?? _session.ServerUrl ?? Strings.None;

    public string KeyRotatedText => _info?.KeyRotatedUtc is null ? Strings.None
        : $"{TimeFormat.Absolute(_info.KeyRotatedUtc)} ({TimeFormat.Relative(_info.KeyRotatedUtc)})";

    public bool HasKey => !string.IsNullOrWhiteSpace(_info?.EnrollmentKey);

    public bool KeyVisible
    {
        get => _keyVisible;
        set
        {
            if (!SetProperty(ref _keyVisible, value)) return;
            OnPropertyChanged(nameof(KeyText), nameof(ToggleKeyText), nameof(ToggleKeyGlyph));
        }
    }

    public string KeyText => !HasKey ? Strings.None : _keyVisible ? _info!.EnrollmentKey! : Strings.KeyHidden;

    public string ToggleKeyText => _keyVisible ? Strings.ButtonHideKey : Strings.ButtonShowKey;

    public string ToggleKeyGlyph => _keyVisible ? Strings.GlyphHide : Strings.GlyphShow;

    /// <summary>The key itself, for the Copy button; never rendered.</summary>
    public string? RawKey => _info?.EnrollmentKey;

    public bool ShowKeyNotReturnedHint => HasInfo && !HasKey;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCanExecuteChanged();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    // ---------------------------------------------------------------- snippets

    public string RegistryPath => Strings.SnippetWritesTo(ProvisioningSnippets.RegistryPath);

    private Guid SnippetOrganizationId => _info?.OrganizationId ?? _session.OrganizationId ?? Guid.Empty;

    private string SnippetServerUrl => _info?.ServerUrl ?? _session.ServerUrl ?? string.Empty;

    /// <summary>The four artefacts, regenerated whenever the key or the organization changes.</summary>
    public IReadOnlyList<SnippetViewModel> Snippets =>
    [
        new(Strings.SnippetPowerShell, Strings.SnippetPowerShellCaption,
            ProvisioningSnippets.PowerShell(SnippetServerUrl, SnippetOrganizationId, RawKey)),
        new(Strings.SnippetReg, Strings.SnippetRegCaption,
            ProvisioningSnippets.RegFile(SnippetServerUrl, SnippetOrganizationId, RawKey)),
        new(Strings.SnippetInstaller, Strings.SnippetInstallerCaption,
            ProvisioningSnippets.InstallerCommandLine(SnippetServerUrl, SnippetOrganizationId, RawKey)),
        new(Strings.SnippetDetection, Strings.SnippetDetectionCaption,
            ProvisioningSnippets.IntuneDetection(SnippetOrganizationId)),
    ];

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand RotateKeyCommand => _rotateCommand;

    public ICommand ToggleKeyCommand => _toggleKeyCommand;

    public ICommand CopyCommand => _copyCommand;

    // ---------------------------------------------------------------- loading

    public void EnsureLoaded()
    {
        if (_session.OrganizationId is not { } id) return;
        if (_loadedOrganization == id && _info is not null) return;
        Reload();
    }

    private async void Reload()
    {
        if (_session.OrganizationId is not { } id) return;
        IsBusy = true;
        Error = null;
        try
        {
            var info = await _session.GetEnrollmentAsync(CancellationToken.None).ConfigureAwait(true);
            Accept(info, id);
            _log.LogInformation("Read the enrollment information of organization {Id}; key returned: {Returned}.",
                id, !string.IsNullOrWhiteSpace(info.EnrollmentKey));
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Reading the organization's enrollment information failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void Rotate()
    {
        if (_session.OrganizationId is not { } id) return;
        if (!_dialogs.Confirm(Strings.ConfirmRotateTitle, Strings.ConfirmRotateBody, Strings.ButtonRotate, Strings.Cancel)) return;

        IsBusy = true;
        Error = null;
        Notice = null;
        try
        {
            var info = await _session.RotateEnrollmentKeyAsync(CancellationToken.None).ConfigureAwait(true);
            Accept(info, id);
            KeyVisible = true;
            Notice = Strings.KeyRotatedNotice;
            _log.LogInformation("Rotated the enrollment key of organization {Id}.", id);
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Rotating the organization's enrollment key failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Accept(EnrollmentInfoResponse info, Guid organizationId)
    {
        _info = info;
        _loadedOrganization = organizationId;
        RefreshAll();
    }

    /// <summary>
    /// Takes the enrollment information that came back from creating an organization. That response is the only
    /// place the key is ever returned, so the page shows it straight away rather than making the administrator
    /// rotate a key they have just been given.
    /// </summary>
    public void AcceptCreated(EnrollmentInfoResponse info)
    {
        Accept(info, info.OrganizationId);
        KeyVisible = true;
        Notice = Strings.KeyIssuedNotice;
        _log.LogInformation("Received the one-time enrollment key of the new organization {Id}.", info.OrganizationId);
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _dialogs.CopyToClipboard(text);
        Notice = Strings.CopiedNotice;
    }

    // ---------------------------------------------------------------- plumbing

    private void OnSessionChanged()
    {
        if (_session.OrganizationId == _loadedOrganization) return;
        _info = null;
        _loadedOrganization = null;
        KeyVisible = false;
        Notice = null;
        Error = null;
        RefreshAll();
    }

    private void RefreshAll()
    {
        OnPropertyChanged(
            nameof(HasInfo), nameof(IsSignedIn), nameof(OrganizationName), nameof(OrganizationIdText), nameof(ServerUrlText),
            nameof(KeyRotatedText), nameof(HasKey), nameof(KeyText), nameof(RawKey), nameof(ShowKeyNotReturnedHint),
            nameof(Snippets));
        RaiseCanExecuteChanged();
    }

    private void RaiseCanExecuteChanged()
    {
        _refreshCommand.RaiseCanExecuteChanged();
        _rotateCommand.RaiseCanExecuteChanged();
        _toggleKeyCommand.RaiseCanExecuteChanged();
        _copyCommand.RaiseCanExecuteChanged();
    }
}

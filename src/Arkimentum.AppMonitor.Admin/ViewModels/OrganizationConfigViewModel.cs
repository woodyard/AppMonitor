using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// One earlier version of the organization configuration, as the history list shows it.
///
/// <para>
/// The entry itself carries who, when, why and the version — not the document. Restoring fetches the revision from
/// <c>GET .../config/history/{historyId}</c> and loads it into the editor as a pending change; publishing it then
/// appends a new revision on top of the current one. The current version has nothing to restore.
/// </para>
/// </summary>
public sealed class ConfigHistoryRowViewModel
{
    public ConfigHistoryRowViewModel(ConfigHistoryEntry entry, bool isCurrent)
    {
        Entry = entry;
        ConfigVersion = entry.ConfigVersion;
        When = TimeFormat.Absolute(entry.UpdatedUtc);
        Relative = TimeFormat.Relative(entry.UpdatedUtc);
        By = Strings.OrganizationUpdatedBy(entry.UpdatedBy, Relative);
        Comment = string.IsNullOrWhiteSpace(entry.Comment) ? Strings.None : entry.Comment!;
        IsCurrent = isCurrent;
    }

    public ConfigHistoryEntry Entry { get; }
    public long HistoryId => Entry.Id;
    public string ConfigVersion { get; }
    public string When { get; }
    public string Relative { get; }
    public string By { get; }
    public string Comment { get; }
    public bool IsCurrent { get; }

    public bool CanRestore => !IsCurrent;
}

/// <summary>
/// The organization configuration document: one <see cref="ConfigurationEditor"/> over
/// <see cref="OrganizationConfigurationStore"/>, the Publish / Discard footer the two organization editor pages
/// share, and the version history.
///
/// <para>
/// Publishing is optimistic: the PUT carries the <c>configVersion</c> the editor started from, and a 409 means
/// somebody else published in the meantime. The administrator then chooses — reload their version, or re-GET and
/// publish these edits on top of it. Nothing is ever written silently over somebody else's work.
/// </para>
/// </summary>
public sealed class OrganizationConfigViewModel : ObservableObject
{
    private readonly ILogger<OrganizationConfigViewModel> _log;
    private readonly CloudSession _session;
    private readonly OrganizationConfigurationStore _store;
    private readonly IDialogService _dialogs;

    private readonly RelayCommand _publishCommand;
    private readonly RelayCommand _discardCommand;
    private readonly RelayCommand _reloadCommand;
    private readonly RelayCommand<ConfigHistoryRowViewModel> _restoreCommand;

    private bool _isBusy;
    private string? _restoredFrom;
    private string? _notice;
    private string? _error;
    private Guid? _loadedOrganization;

    public OrganizationConfigViewModel(
        ILogger<OrganizationConfigViewModel> log,
        ILogger<ConfigurationEditor> editorLog,
        CloudSession session,
        OrganizationConfigurationStore store,
        CatalogService catalog,
        IDialogService dialogs)
    {
        _log = log;
        _session = session;
        _store = store;
        _dialogs = dialogs;

        // No detection tester: "Test detection" checks this machine, which says nothing about a fleet.
        Editor = new ConfigurationEditor(editorLog, store, catalog, tester: null);

        _publishCommand = new RelayCommand(Publish, () => !_isBusy && HasDocument && Editor.CanApply);
        _discardCommand = new RelayCommand(Discard, () => !_isBusy && Editor.IsDirty);
        _reloadCommand = new RelayCommand(Reload, () => !_isBusy && _session.OrganizationId is not null);
        _restoreCommand = new RelayCommand<ConfigHistoryRowViewModel>(Restore, row => row is { CanRestore: true } && !_isBusy && HasDocument);

        Editor.Changed += OnEditorChanged;
        _session.Changed += OnSessionChanged;
    }

    public ConfigurationEditor Editor { get; }

    public ObservableCollection<ConfigHistoryRowViewModel> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    /// <summary>How many versions the organization has in total, not how many are listed.</summary>
    public int HistoryTotal { get; private set; }

    public string HistoryCountText => Strings.VersionHistoryCount(History.Count, HistoryTotal);

    public bool HasDocument => _store.HasDocument;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCanExecuteChanged();
        }
    }

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string VersionText => _store.ConfigVersion is { } v ? Strings.OrganizationConfigVersion(v) : Strings.OrganizationNotLoaded;

    public string UpdatedText => _store.UpdatedUtc is null
        ? Strings.None
        : Strings.OrganizationUpdatedBy(_store.UpdatedBy, TimeFormat.Absolute(_store.UpdatedUtc));

    public ICommand PublishCommand => _publishCommand;

    public ICommand DiscardCommand => _discardCommand;

    public ICommand ReloadCommand => _reloadCommand;

    public ICommand RestoreCommand => _restoreCommand;

    // ---------------------------------------------------------------- load

    /// <summary>Loads the document (and its history) when the selected organization has changed.</summary>
    public void EnsureLoaded() => _ = EnsureLoadedAsync();

    /// <summary>
    /// The same as <see cref="EnsureLoaded"/>, but returns the load in flight. A page that is about to put something
    /// into the editor (the Inventory page adding applications) must wait for it: a reload rebuilds every row from the
    /// server document, so an addition made before the first load completed was silently discarded when the
    /// Applications page was opened next. Completed when nothing needs loading.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        if (_session.OrganizationId is not { } id) return Task.CompletedTask;
        if (_loadTask is { IsCompleted: false } running) return running;
        if (_loadedOrganization == id && HasDocument) return Task.CompletedTask;
        return ReloadAsync();
    }

    private Task? _loadTask;

    private void Reload() => _ = ReloadAsync();

    /// <summary>One load at a time; a second request while one is running joins it instead of starting another.</summary>
    private Task ReloadAsync()
    {
        if (_loadTask is { IsCompleted: false } running) return running;
        return _loadTask = ReloadCoreAsync();
    }

    private async Task ReloadCoreAsync()
    {
        if (_session.OrganizationId is not { } id) return;
        IsBusy = true;
        Error = null;
        Notice = null;
        try
        {
            var config = await _session.GetConfigAsync(CancellationToken.None).ConfigureAwait(true);
            _store.Accept(config.Settings, config.ConfigVersion, config.UpdatedUtc, config.UpdatedBy);
            Editor.Reload();
            _loadedOrganization = id;
            _log.LogInformation("Loaded organization configuration {Version} ({Global} global value(s), {Apps} application(s)).",
                config.ConfigVersion, config.Settings.Global.Count, config.Settings.Apps.Count);
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Loading the organization configuration failed.");
        }
        finally
        {
            IsBusy = false;
            RefreshHeader();
        }
    }

    /// <summary>The newest page of history entries; the list is informational, so one page is enough.</summary>
    private const int HistoryPageSize = 20;

    private async Task LoadHistoryAsync()
    {
        History.Clear();
        try
        {
            var page = await _session.GetConfigHistoryAsync(1, HistoryPageSize, CancellationToken.None).ConfigureAwait(true);
            foreach (var entry in page.Items.OrderByDescending(e => e.UpdatedUtc))
                History.Add(new ConfigHistoryRowViewModel(entry, entry.ConfigVersion == _store.ConfigVersion));
            HistoryTotal = page.Total;
        }
        catch (Exception ex)
        {
            // A missing history is not a reason to refuse to edit; the editor is already usable.
            _log.LogWarning(ex, "Loading the organization configuration history failed.");
        }
        OnPropertyChanged(nameof(HasHistory), nameof(HistoryTotal), nameof(HistoryCountText));
    }

    // ---------------------------------------------------------------- publish

    private async void Publish()
    {
        var comment = new TextInputViewModel(Strings.PublishCommentTitle, Strings.PublishCommentPrompt,
            string.Empty, Strings.ButtonPublish, _ => null);
        if (!_dialogs.ShowDialog(comment)) return;
        await PublishAsync(comment.Text.Trim(), _store.ConfigVersion).ConfigureAwait(true);
    }

    private async Task PublishAsync(string? comment, string? baseVersion)
    {
        IsBusy = true;
        Error = null;
        Notice = null;
        var document = Editor.ToDocument();
        try
        {
            _log.LogInformation("Publishing the organization configuration ({Global} global value(s), {Apps} application(s)) over base version {Base}.",
                document.Global.Count, document.Apps.Count, baseVersion ?? "(none)");
            var response = await _session.PutConfigAsync(document, baseVersion, string.IsNullOrWhiteSpace(comment) ? null : comment,
                CancellationToken.None).ConfigureAwait(true);
            _store.Accept(response.Settings, response.ConfigVersion, response.UpdatedUtc, response.UpdatedBy);
            Editor.Reload();
            RestoredFrom = null;
            Notice = Strings.PublishedNotice;
            _log.LogInformation("Published the organization configuration as version {Version}.", response.ConfigVersion);
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (CloudException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            _log.LogInformation("The organization configuration was published by somebody else; asking what to do.");
            IsBusy = false;
            await ResolveConflictAsync(comment, document).ConfigureAwait(true);
            return;
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Publishing the organization configuration failed.");
        }
        finally
        {
            IsBusy = false;
            RefreshHeader();
        }
    }

    /// <summary>409: take theirs (Reload) or publish ours on top of theirs (Overwrite = re-GET, then PUT).</summary>
    private async Task ResolveConflictAsync(string? comment, Configuration.SettingsDocument pending)
    {
        var overwrite = _dialogs.Confirm(Strings.ConflictTitle, Strings.ConflictBody,
            Strings.ConflictOverwrite, Strings.ConflictReload);
        if (!overwrite)
        {
            Reload();
            return;
        }

        IsBusy = true;
        try
        {
            var current = await _session.GetConfigAsync(CancellationToken.None).ConfigureAwait(true);
            _log.LogInformation("Overwriting organization configuration version {Version} with the pending edits.", current.ConfigVersion);
            var response = await _session.PutConfigAsync(pending, current.ConfigVersion,
                string.IsNullOrWhiteSpace(comment) ? null : comment, CancellationToken.None).ConfigureAwait(true);
            _store.Accept(response.Settings, response.ConfigVersion, response.UpdatedUtc, response.UpdatedBy);
            Editor.Reload();
            Notice = Strings.OverwroteNotice;
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Overwriting the organization configuration failed.");
        }
        finally
        {
            IsBusy = false;
            RefreshHeader();
        }
    }

    // ---------------------------------------------------------------- discard / restore

    private void Discard()
    {
        if (!ConfirmDiscard()) return;
        Editor.Discard();
        RestoredFrom = null;
        Notice = null;
        Error = null;
    }

    /// <summary>
    /// Loads one historical revision into the editor as a pending change. Nothing is written here: the
    /// administrator reviews the restored document and publishes it, and that publish carries the <em>current</em>
    /// version as its base — so a restore appends a new revision instead of rewinding the history.
    /// </summary>
    private async void Restore(ConfigHistoryRowViewModel? row)
    {
        if (row is not { CanRestore: true } || _session.OrganizationId is null) return;
        if (Editor.IsDirty && !ConfirmDiscard()) return;

        IsBusy = true;
        Error = null;
        Notice = null;
        try
        {
            var revision = await _session.GetConfigRevisionAsync(row.HistoryId, CancellationToken.None).ConfigureAwait(true);
            Editor.LoadPending(revision.Settings);
            RestoredFrom = revision.ConfigVersion;
            Notice = Strings.RestoredNotice(revision.ConfigVersion);
            _log.LogInformation(
                "Loaded organization configuration revision {HistoryId} ({Version}, {Global} global value(s), {Apps} application(s)) into the editor; publishing it will append a new revision over {Current}.",
                row.HistoryId, revision.ConfigVersion, revision.Settings.Global.Count, revision.Settings.Apps.Count,
                _store.ConfigVersion ?? "(none)");
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Loading configuration revision {HistoryId} failed.", row.HistoryId);
        }
        finally
        {
            IsBusy = false;
            RefreshHeader();
        }
    }

    /// <summary>The version the pending document was restored from, or null when it was not restored.</summary>
    public string? RestoredFrom
    {
        get => _restoredFrom;
        private set
        {
            if (SetProperty(ref _restoredFrom, value)) OnPropertyChanged(nameof(IsRestored), nameof(RestoredBadge));
        }
    }

    public bool IsRestored => !string.IsNullOrEmpty(_restoredFrom) && Editor.IsDirty;

    public string RestoredBadge => Strings.RestoredFromVersion(_restoredFrom ?? string.Empty);

    /// <summary>Called by the shell before it leaves an organization editor page with unsaved changes.</summary>
    public bool ConfirmLeave() => !Editor.IsDirty || ConfirmDiscard();

    private bool ConfirmDiscard() =>
        _dialogs.Confirm(Strings.ConfirmDiscardTitle, Strings.ConfirmDiscardOrganizationBody, Strings.Discard, Strings.KeepEditing);

    // ---------------------------------------------------------------- change tracking

    private void OnSessionChanged()
    {
        if (_session.OrganizationId == _loadedOrganization) return;
        _store.Clear();
        _loadedOrganization = null;
        History.Clear();
        Editor.Reload();
        Notice = null;
        Error = null;
        RefreshHeader();
        OnPropertyChanged(nameof(HasHistory), nameof(HistoryTotal), nameof(HistoryCountText));
    }

    private void OnEditorChanged()
    {
        // A restore is itself an unsaved change, so its notice survives; any further edit clears it.
        if (Editor.IsDirty && _restoredFrom is null) Notice = null;
        if (!Editor.IsDirty) RestoredFrom = null;
        OnPropertyChanged(nameof(IsRestored));
        RaiseCanExecuteChanged();
    }

    private void RefreshHeader()
    {
        OnPropertyChanged(nameof(HasDocument), nameof(VersionText), nameof(UpdatedText));
        RaiseCanExecuteChanged();
    }

    private void RaiseCanExecuteChanged()
    {
        _publishCommand.RaiseCanExecuteChanged();
        _discardCommand.RaiseCanExecuteChanged();
        _reloadCommand.RaiseCanExecuteChanged();
        _restoreCommand.RaiseCanExecuteChanged();
    }
}

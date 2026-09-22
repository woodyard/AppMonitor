using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Editing;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// The one pending organization configuration the Applications page, the Settings page and the Inventory page all
/// edit, plus the publish/discard/history machinery around it.
///
/// <para>
/// Publishing is optimistic: the PUT carries the version the editor started from as <c>If-Match</c>, and a 409 means
/// somebody else published in the meantime. The administrator then chooses - reload theirs, or re-GET and publish
/// these edits on top of it. Nothing is ever written silently over somebody else's work.
/// </para>
///
/// <para>Ported from <c>src/Arkimentum.AppMonitor.Admin/ViewModels/OrganizationConfigViewModel.cs</c>.</para>
/// </summary>
public sealed class ConfigWorkspace
{
    /// <summary>The history list is informational; one page of it is enough.</summary>
    private const int HistoryPageSize = 20;

    private readonly AdminApiClient _api;
    private readonly OrganizationState _organizations;
    private readonly CatalogService _catalog;

    private Guid? _loadedOrganization;
    private Task? _loading;
    private SettingsDocument? _conflictDocument;
    private string? _conflictComment;

    public ConfigWorkspace(AdminApiClient api, OrganizationState organizations, CatalogService catalog)
    {
        _api = api;
        _organizations = organizations;
        _catalog = catalog;
        Editor = new ConfigDocumentEditor(_catalog.Find);
        Editor.Changed += () => Changed?.Invoke();
        _organizations.Changed += OnOrganizationChanged;
    }

    /// <summary>Raised whenever anything the pages show has changed.</summary>
    public event Action? Changed;

    public ConfigDocumentEditor Editor { get; }

    public bool HasDocument { get; private set; }

    public bool IsBusy { get; private set; }

    public string? ConfigVersion { get; private set; }

    public DateTimeOffset? UpdatedUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    /// <summary>The version the pending document was restored from, or null when it was not restored.</summary>
    public string? RestoredFrom { get; private set; }

    public IReadOnlyList<ConfigHistoryEntry> History { get; private set; } = [];

    public int HistoryTotal { get; private set; }

    /// <summary>Set while a 409 is waiting to be answered; the page shows "reload theirs" / "overwrite".</summary>
    public bool HasConflict => _conflictDocument is not null;

    public bool IsDirty => Editor.IsDirty;

    public bool CanPublish => Editor.CanPublish && HasDocument && !IsBusy;

    public bool ShowAdvanced { get; set; }

    // ---------------------------------------------------------------- load

    /// <summary>
    /// Loads the document and its history when the selected organization changed. A page that is about to put
    /// something into the editor (Inventory adding applications) must await this: a load rebuilds every row from the
    /// server document, so an addition made before the first load completed would be silently discarded.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        if (_organizations.OrganizationId is not { } id) return Task.CompletedTask;
        if (_loading is { IsCompleted: false } running) return running;
        if (_loadedOrganization == id && HasDocument) return Task.CompletedTask;
        return ReloadAsync();
    }

    /// <summary>Re-reads the document from the server, throwing away any pending edits.</summary>
    public Task ReloadAsync()
    {
        if (_loading is { IsCompleted: false } running) return running;
        return _loading = ReloadCoreAsync();
    }

    private async Task ReloadCoreAsync()
    {
        if (_organizations.OrganizationId is not { } id) return;
        Begin();
        try
        {
            await _catalog.EnsureLoadedAsync().ConfigureAwait(false);
            var fetch = await _api.GetConfigAsync(id).ConfigureAwait(false);
            Accept(fetch);
            Editor.Load(fetch.Config.Settings);
            RestoredFrom = null;
            _loadedOrganization = id;
            await LoadHistoryAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Error = ApiException.Describe(ex);
        }
        finally
        {
            End();
        }
    }

    private async Task LoadHistoryAsync(Guid organizationId)
    {
        try
        {
            var page = await _api.GetConfigHistoryAsync(organizationId, 1, HistoryPageSize).ConfigureAwait(false);
            History = [.. page.Items.OrderByDescending(e => e.UpdatedUtc)];
            HistoryTotal = page.Total;
        }
        catch
        {
            // A missing history is no reason to refuse to edit; the editor is already usable.
            History = [];
            HistoryTotal = 0;
        }
    }

    // ---------------------------------------------------------------- publish

    /// <summary>
    /// Publishes the pending document over the version it was loaded from. A 409 leaves the edits in place and puts
    /// the workspace into <see cref="HasConflict"/>, which the footer answers with Reload or Overwrite.
    /// </summary>
    public async Task PublishAsync(string? comment)
    {
        if (_organizations.OrganizationId is not { } id) return;
        Begin();
        var document = Editor.ToDocument();
        try
        {
            var fetch = await _api.PutConfigAsync(id, document, ConfigVersion, comment).ConfigureAwait(false);
            Accept(fetch);
            Editor.Load(fetch.Config.Settings);
            RestoredFrom = null;
            Notice = "Published. Devices pick the new configuration up on their next sync.";
            await LoadHistoryAsync(id).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.IsConflict)
        {
            _conflictDocument = document;
            _conflictComment = comment;
            Error = "Somebody else published a newer version while you were editing.";
        }
        catch (Exception ex)
        {
            Error = ApiException.Describe(ex);
        }
        finally
        {
            End();
        }
    }

    /// <summary>409, "take theirs": throw the pending edits away and re-read the server's document.</summary>
    public async Task ResolveWithTheirsAsync()
    {
        ClearConflict();
        await ReloadAsync().ConfigureAwait(false);
        Notice = "Reloaded the newer version; your edits were discarded.";
        Changed?.Invoke();
    }

    /// <summary>409, "publish mine on top": re-GET for the current version, then PUT the pending document over it.</summary>
    public async Task ResolveWithMineAsync()
    {
        if (_organizations.OrganizationId is not { } id || _conflictDocument is not { } pending) return;
        var comment = _conflictComment;
        ClearConflict();
        Begin();
        try
        {
            var current = await _api.GetConfigAsync(id).ConfigureAwait(false);
            var fetch = await _api.PutConfigAsync(id, pending, current.BaseVersion, comment).ConfigureAwait(false);
            Accept(fetch);
            Editor.Load(fetch.Config.Settings);
            RestoredFrom = null;
            Notice = "Published over the newer version.";
            await LoadHistoryAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Error = ApiException.Describe(ex);
        }
        finally
        {
            End();
        }
    }

    public void Discard()
    {
        Editor.Discard();
        RestoredFrom = null;
        ClearConflict();
        Notice = null;
        Error = null;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- history

    /// <summary>
    /// Loads one historical revision into the editor as a pending change. Nothing is written here: the administrator
    /// reviews the restored document and publishes it, and that publish carries the <em>current</em> version as its
    /// base - so a restore appends a new revision instead of rewinding the history.
    /// </summary>
    public async Task RestoreAsync(long historyId)
    {
        if (_organizations.OrganizationId is not { } id) return;
        Begin();
        try
        {
            var revision = await _api.GetConfigRevisionAsync(id, historyId).ConfigureAwait(false);
            Editor.LoadPending(revision.Settings);
            RestoredFrom = revision.ConfigVersion;
            Notice = $"Loaded version {revision.ConfigVersion} into the editor. Publish it to make it the current version.";
        }
        catch (Exception ex)
        {
            Error = ApiException.Describe(ex);
        }
        finally
        {
            End();
        }
    }

    /// <summary>The settings of one revision, for the "view" pane of the history drawer.</summary>
    public async Task<OrganizationConfigResponse?> ViewAsync(long historyId)
    {
        if (_organizations.OrganizationId is not { } id) return null;
        try { return await _api.GetConfigRevisionAsync(id, historyId).ConfigureAwait(false); }
        catch (Exception ex) { Error = ApiException.Describe(ex); Changed?.Invoke(); return null; }
    }

    public bool IsCurrent(ConfigHistoryEntry entry) => entry.ConfigVersion == ConfigVersion;

    // ---------------------------------------------------------------- plumbing

    public void ClearNotice()
    {
        Notice = null;
        Changed?.Invoke();
    }

    public void ClearError()
    {
        Error = null;
        Changed?.Invoke();
    }

    private void ClearConflict()
    {
        _conflictDocument = null;
        _conflictComment = null;
    }

    private void Accept(ConfigFetch fetch)
    {
        // The ETag is the version to send back as If-Match; the body repeats it for clients that cannot read headers.
        ConfigVersion = fetch.BaseVersion;
        UpdatedUtc = fetch.Config.UpdatedUtc;
        UpdatedBy = fetch.Config.UpdatedBy;
        HasDocument = true;
    }

    private void Begin()
    {
        IsBusy = true;
        Error = null;
        Notice = null;
        Changed?.Invoke();
    }

    private void End()
    {
        IsBusy = false;
        Changed?.Invoke();
    }

    private void OnOrganizationChanged()
    {
        if (_organizations.OrganizationId == _loadedOrganization) return;
        _loadedOrganization = null;
        _loading = null;
        HasDocument = false;
        ConfigVersion = null;
        UpdatedUtc = null;
        UpdatedBy = null;
        History = [];
        HistoryTotal = 0;
        RestoredFrom = null;
        Notice = null;
        Error = null;
        ClearConflict();
        Editor.Load(new SettingsDocument());
    }
}

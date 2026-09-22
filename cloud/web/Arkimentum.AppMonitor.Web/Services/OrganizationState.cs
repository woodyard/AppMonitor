using Arkimentum.AppMonitor.Api.Contracts;
using Microsoft.JSInterop;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// Who is signed in and which organization they are looking at.
///
/// <para>
/// The choice is remembered in <c>localStorage</c>, because an administrator who manages several organizations
/// comes back to the same one every morning. One organization selects itself; a remembered id that the signed-in
/// account can no longer see falls back to the first one it can.
/// </para>
/// </summary>
public sealed class OrganizationState
{
    private const string StorageKey = "appmonitor.organizationId";

    private readonly AdminApiClient _api;
    private readonly IJSRuntime _js;
    private Task? _loading;

    public OrganizationState(AdminApiClient api, IJSRuntime js)
    {
        _api = api;
        _js = js;
    }

    /// <summary>Raised when the signed-in user or the selected organization changed.</summary>
    public event Action? Changed;

    public AdminMeResponse? Me { get; private set; }

    public IReadOnlyList<OrganizationSummary> Organizations => Me?.Organizations ?? [];

    public OrganizationSummary? Current { get; private set; }

    public Guid? OrganizationId => Current?.OrganizationId;

    public string OrganizationName => Current?.Name ?? "No organization";

    public bool IsGlobalAdmin => Me?.IsGlobalAdmin ?? false;

    /// <summary>The account has no organization at all - a global administrator has to create one first.</summary>
    public bool HasNoOrganizations => Me is not null && Organizations.Count == 0;

    /// <summary>Set when <c>/admin/me</c> failed; the shell shows it instead of a page.</summary>
    public string? Error { get; private set; }

    public bool IsLoaded => Me is not null;

    /// <summary>Calls <c>/admin/me</c> once per sign-in. Concurrent callers join the call in flight.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>Forgets everything and re-reads <c>/admin/me</c> (after creating an organization, for instance).</summary>
    public Task ReloadAsync()
    {
        _loading = null;
        Me = null;
        return EnsureLoadedAsync();
    }

    private async Task LoadAsync()
    {
        Error = null;
        try
        {
            Me = await _api.GetMeAsync().ConfigureAwait(false);
            var remembered = await ReadRememberedAsync().ConfigureAwait(false);
            Current = Organizations.FirstOrDefault(o => o.OrganizationId == remembered) ?? Organizations.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Error = ApiException.Describe(ex);
        }
        Changed?.Invoke();
    }

    /// <summary>Selects an organization and remembers it. Ignores an id the account cannot see.</summary>
    public async Task SelectAsync(Guid organizationId)
    {
        var organization = Organizations.FirstOrDefault(o => o.OrganizationId == organizationId);
        if (organization is null || organization.OrganizationId == Current?.OrganizationId) return;
        Current = organization;
        await WriteRememberedAsync(organizationId).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Replaces the summary of the current organization after something changed its counters.</summary>
    public void Refresh(OrganizationSummary summary)
    {
        if (Me is null) return;
        var index = Me.Organizations.FindIndex(o => o.OrganizationId == summary.OrganizationId);
        if (index >= 0) Me.Organizations[index] = summary;
        else Me.Organizations.Add(summary);
        if (Current?.OrganizationId == summary.OrganizationId) Current = summary;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- localStorage
    // Storage can be blocked outright (private windows, "block all cookies"), and a read that throws must not take
    // the console down with it - the choice is a convenience, not state the console depends on.

    private async Task<Guid?> ReadRememberedAsync()
    {
        try
        {
            var value = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey).ConfigureAwait(false);
            return Guid.TryParse(value, out var id) ? id : null;
        }
        catch { return null; }
    }

    private async Task WriteRememberedAsync(Guid organizationId)
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, organizationId.ToString()).ConfigureAwait(false); }
        catch { /* nothing to do: the choice simply is not remembered */ }
    }
}

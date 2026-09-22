using Arkimentum.AppMonitor.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Arkimentum.AppMonitor.Web.Shared;

/// <summary>
/// What every organization-scoped page does the same way: wait for <c>/admin/me</c>, load itself for the selected
/// organization, reload when the organization switcher changes, and turn any failure into one error banner instead
/// of a broken page.
/// </summary>
public abstract class OrganizationPage : ComponentBase, IDisposable
{
    private Guid? _loadedFor;

    [Inject] protected OrganizationState Organizations { get; set; } = default!;

    protected Guid? OrganizationId => Organizations.OrganizationId;

    protected bool HasOrganization => Organizations.OrganizationId is not null;

    protected bool IsBusy { get; private set; }

    protected string? Error { get; set; }

    protected string? Notice { get; set; }

    /// <summary>Loads everything the page shows for <see cref="OrganizationId"/>. Failures become the banner.</summary>
    protected abstract Task LoadAsync();

    protected override async Task OnInitializedAsync()
    {
        Organizations.Changed += OnOrganizationChanged;
        await Organizations.EnsureLoadedAsync();
        _loadedFor = Organizations.OrganizationId;
        if (HasOrganization) await RunAsync(LoadAsync);
    }

    /// <summary>Runs an action with the busy flag set and any failure captured into <see cref="Error"/>.</summary>
    protected async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
        StateHasChanged();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Error = ApiException.Describe(ex);
        }
        finally
        {
            IsBusy = false;
            StateHasChanged();
        }
    }

    /// <summary>Re-runs <see cref="LoadAsync"/> - what the page's own "Refresh" button calls.</summary>
    protected Task ReloadAsync() => RunAsync(LoadAsync);

    protected void Dismiss()
    {
        Error = null;
        Notice = null;
    }

    private void OnOrganizationChanged() => _ = InvokeAsync(OnOrganizationChangedAsync);

    private async Task OnOrganizationChangedAsync()
    {
        if (_loadedFor == Organizations.OrganizationId)
        {
            StateHasChanged();
            return;
        }
        _loadedFor = Organizations.OrganizationId;
        Notice = null;
        if (HasOrganization) await RunAsync(LoadAsync);
        else StateHasChanged();
    }

    public virtual void Dispose() => Organizations.Changed -= OnOrganizationChanged;
}

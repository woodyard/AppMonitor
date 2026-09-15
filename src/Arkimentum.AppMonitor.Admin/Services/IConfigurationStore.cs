using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Configuration;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// What <see cref="ViewModels.ConfigurationEditor"/> needs from whatever holds the document it edits.
///
/// <para>
/// There are two implementations, and the editor cannot tell them apart: <see cref="RegistryConfigurationStore"/>
/// for this machine's registry layers, and <see cref="OrganizationConfigurationStore"/> for the organization
/// document fetched from the cloud API. Everything else about the editor — the generated rows, the dirty tracking,
/// the validation — is identical for both, which is the whole point.
/// </para>
/// </summary>
public interface IConfigurationStore
{
    /// <summary>The badge an overridden row shows: "Preference" locally, "Organization" for the cloud document.</summary>
    string OverriddenBadge { get; }

    /// <summary>The saved document the editor takes as its baseline.</summary>
    SettingsDocument ReadSaved();

    /// <summary>
    /// The read-only layer above it. Empty for the organization document: there is no policy lock concept in the
    /// cloud, and an empty document means no row is ever locked.
    /// </summary>
    SettingsDocument ReadPolicy();

    /// <summary>Persists the document so the next <c>ReadSaved</c> returns it.</summary>
    void Write(SettingsDocument document);
}

/// <summary>The local machine: the preference layer this console writes, under the read-only policy layer.</summary>
public sealed class RegistryConfigurationStore : IConfigurationStore
{
    private readonly SettingsStoreService _store;

    public RegistryConfigurationStore(SettingsStoreService store) => _store = store;

    public string OverriddenBadge => Strings.BadgePreference;

    public SettingsDocument ReadSaved() => _store.Read(SettingsLayer.Preference);

    public SettingsDocument ReadPolicy() => _store.Read(SettingsLayer.Policy);

    public void Write(SettingsDocument document) =>
        _store.Write(document, SettingsLayer.Preference, SettingsWriteMode.Replace);
}

/// <summary>
/// The organization document. It is held in memory: the editor's baseline is whatever the last GET or PUT of
/// <c>/api/v1/admin/organizations/{id}/config</c> returned, and publishing is the owning view model's job, not the
/// editor's — a PUT can be rejected with 409 and needs a conversation, which <see cref="IConfigurationStore.Write"/>
/// has no room for.
/// </summary>
public sealed class OrganizationConfigurationStore : IConfigurationStore
{
    private static readonly SettingsDocument Empty = new();

    private SettingsDocument _saved = new();

    public string OverriddenBadge => Strings.BadgeOrganization;

    /// <summary>ETag of <see cref="_saved"/>; sent back as baseConfigVersion so a concurrent publish is caught.</summary>
    public string? ConfigVersion { get; private set; }

    public DateTimeOffset? UpdatedUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public bool HasDocument { get; private set; }

    public SettingsDocument ReadSaved() => _saved;

    public SettingsDocument ReadPolicy() => Empty;

    public void Write(SettingsDocument document) => _saved = document;

    /// <summary>Takes a document the API returned as the new baseline.</summary>
    public void Accept(SettingsDocument document, string? configVersion, DateTimeOffset? updatedUtc, string? updatedBy)
    {
        _saved = document;
        ConfigVersion = configVersion;
        UpdatedUtc = updatedUtc;
        UpdatedBy = updatedBy;
        HasDocument = true;
    }

    /// <summary>Back to "nothing loaded" — used on sign-out and when the organization changes.</summary>
    public void Clear()
    {
        _saved = new SettingsDocument();
        ConfigVersion = null;
        UpdatedUtc = null;
        UpdatedBy = null;
        HasDocument = false;
    }
}

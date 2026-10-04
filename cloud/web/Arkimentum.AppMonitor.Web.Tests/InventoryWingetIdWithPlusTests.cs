using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Editing;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// Winget ids are not always valid AppIds: "Microsoft.VCRedist.2015+.x64" carries a "+", which a registry key name
/// must not. Those rows were unselectable in the inventory ("no usable identity"). The AppId is now a slug of the
/// winget id, and the winget id itself travels verbatim.
/// </summary>
public sealed class InventoryWingetIdWithPlusTests
{
    private static readonly OrganizationInventoryItem VcX64 = new()
    {
        DisplayName = "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247",
        Publisher = "Microsoft Corporation",
        WingetId = "Microsoft.VCRedist.2015+.x64",
    };

    private static readonly OrganizationInventoryItem VcX86 = new()
    {
        DisplayName = "Microsoft Visual C++ v14 Redistributable (x86) - 14.51.36247",
        Publisher = "Microsoft Corporation",
        WingetId = "Microsoft.VCRedist.2015+.x86",
    };

    [Fact]
    public void A_winget_id_with_a_plus_sign_still_suggests_an_app_id()
    {
        Assert.Equal("microsoft-vcredist-2015-x64", ConfigDocumentEditor.SuggestAppId(VcX64));
        Assert.Equal("microsoft-vcredist-2015-x86", ConfigDocumentEditor.SuggestAppId(VcX86));
        Assert.Equal("notepad-notepad", ConfigDocumentEditor.SuggestAppId(new OrganizationInventoryItem { DisplayName = "Notepad++", WingetId = "Notepad++.Notepad++" }));
    }

    [Fact]
    public void A_valid_winget_id_is_still_used_as_is()
    {
        Assert.Equal("Git.Git", ConfigDocumentEditor.SuggestAppId(new OrganizationInventoryItem { DisplayName = "Git", WingetId = "Git.Git" }));
    }

    [Fact]
    public void Both_architectures_are_added_with_their_winget_ids_verbatim()
    {
        var editor = new ConfigDocumentEditor(_ => null);
        editor.Load(new SettingsDocument());

        var x64 = editor.AddFromInventory(VcX64);
        var x86 = editor.AddFromInventory(VcX86);

        Assert.NotNull(x64);
        Assert.NotNull(x86);
        Assert.Equal(2, editor.Apps.Count);
        var document = editor.ToDocument();
        Assert.Equal("Microsoft.VCRedist.2015+.x64", document.Apps["microsoft-vcredist-2015-x64"]["WingetId"].AsString());
        Assert.Equal("Microsoft.VCRedist.2015+.x86", document.Apps["microsoft-vcredist-2015-x86"]["WingetId"].AsString());
        Assert.Equal("winget", document.Apps["microsoft-vcredist-2015-x64"]["Source"].AsString());
        Assert.Empty(document.Validate());
        Assert.True(editor.CanPublish);
    }

    [Fact]
    public void Adding_the_same_row_twice_is_a_no_op()
    {
        var editor = new ConfigDocumentEditor(_ => null);
        editor.Load(new SettingsDocument());

        Assert.NotNull(editor.AddFromInventory(VcX64));
        Assert.Null(editor.AddFromInventory(VcX64));
        Assert.Single(editor.Apps);
    }
}

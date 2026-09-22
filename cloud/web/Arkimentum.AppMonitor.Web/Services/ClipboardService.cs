using Microsoft.JSInterop;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// "Copy" for the enrollment snippets and the one-time keys.
///
/// <para>
/// The browser clipboard API only exists in a secure context and can be refused outright, so the JavaScript helper
/// in <c>index.html</c> falls back to a hidden textarea and returns whether it worked. A refusal is reported to the
/// user rather than swallowed - silently not copying an enrollment key is worse than saying so.
/// </para>
/// </summary>
public sealed class ClipboardService
{
    private readonly IJSRuntime _js;

    public ClipboardService(IJSRuntime js) => _js = js;

    public async Task<bool> CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try { return await _js.InvokeAsync<bool>("appMonitor.copyToClipboard", text).ConfigureAwait(false); }
        catch { return false; }
    }
}

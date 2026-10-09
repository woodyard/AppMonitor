using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkimentum.AppMonitor.Install;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>What <see cref="WingetUserSettings.Merge"/> decided.</summary>
public enum WingetSettingsMergeOutcome
{
    /// <summary>The file already says what it should; nothing is written.</summary>
    Unchanged,
    /// <summary><see cref="WingetSettingsMerge.Text"/> is the new content to write.</summary>
    Changed,
    /// <summary>The existing file cannot be read as a JSON object; it must not be overwritten.</summary>
    Malformed,
}

/// <param name="Outcome">Whether anything has to be written.</param>
/// <param name="Text">The new file content when <see cref="WingetSettingsMergeOutcome.Changed"/>, else null.</param>
/// <param name="Downloader">The <c>network.downloader</c> value the file holds afterwards; null when it holds none (winget's own default) or is malformed.</param>
/// <param name="Error">Why the file is malformed.</param>
public sealed record WingetSettingsMerge(WingetSettingsMergeOutcome Outcome, string? Text, string? Downloader, string? Error = null);

/// <param name="Path">The settings file.</param>
/// <param name="Written">True when the file was (created or) rewritten.</param>
/// <param name="Downloader">The <c>network.downloader</c> value the file holds now; null for none (winget's own default) or when it could not be read.</param>
/// <param name="Error">Why nothing could be applied; null on success.</param>
public sealed record WingetSettingsApplyResult(string Path, bool Written, string? Downloader, string? Error = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Reads and edits winget's own user settings file (<c>settings.json</c>, https://aka.ms/winget-settings) - for the
/// service that is SYSTEM's file, whose location is only known to winget itself and is asked for with
/// <c>winget settings export</c>. The one value edited is <c>network.downloader</c> (<c>default</c>, <c>wininet</c>,
/// <c>do</c>): winget has no command-line switch for its downloader. Delivery Optimization (winget's default) was measured
/// at 20-60 times slower than a plain HTTP download of the same installers on a device without peers (Node.js MSI 35 MB:
/// 63 s through DO, 1.7 s with curl).
/// <para>
/// The file is JSON with <c>//</c> comments and trailing commas. Every other key is kept; comments are not (the default
/// file holds only <c>$schema</c> and commented-out examples). The file is written only when the value changes, and
/// atomically (a temporary file in the same folder, then a replace). A file that cannot be parsed is never overwritten.
/// </para>
/// </summary>
public static class WingetUserSettings
{
    public const string SchemaUrl = "https://aka.ms/winget-settings.schema.json";
    public const string Default = "default";
    public const string WinInet = "wininet";
    public const string DeliveryOptimization = "do";

    /// <summary>How long <c>winget settings export</c> may take.</summary>
    public static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The <c>userSettingsFile</c> path from the output of <c>winget settings export</c>
    /// (<c>{"$schema":"...","adminSettings":{...},"userSettingsFile":"C:\\...\\settings.json"}</c>), or null when the output
    /// holds no such JSON object or no absolute path. Text around the object (a banner, a trailing line) is ignored.
    /// </summary>
    public static string? ParseUserSettingsFile(string? exportOutput)
    {
        if (string.IsNullOrWhiteSpace(exportOutput)) return null;
        var start = exportOutput.IndexOf('{');
        var end = exportOutput.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(exportOutput.AsMemory(start, end - start + 1), ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "userSettingsFile", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) return null;
                var path = property.Value.GetString()?.Trim();
                return !string.IsNullOrEmpty(path) && System.IO.Path.IsPathFullyQualified(path) ? path : null;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <c>winget settings export</c> with <paramref name="wingetPath"/> as the current identity and returns the
    /// settings file it names. Null (with the reason in <paramref name="error"/>) when winget fails or says nothing usable.
    /// </summary>
    public static async Task<(string? Path, string? Error)> FindUserSettingsFileAsync(ILogger logger, string wingetPath, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync(logger, wingetPath, "settings export", ExportTimeout, ct: ct).ConfigureAwait(false);
        if (!run.Started) return (null, run.StartFailure);
        if (run.TimedOut) return (null, $"'winget settings export' did not finish within {ExportTimeout.TotalSeconds:0} s");
        var path = ParseUserSettingsFile(run.StandardOutput) ?? ParseUserSettingsFile(run.CombinedOutput);
        if (path is not null) return (path, null);
        var tail = run.LastLines(2);
        return (null, $"'winget settings export' exited with {run.ExitCode} and named no settings file{(tail.Length > 0 ? ": " + tail : "")}");
    }

    /// <summary>
    /// Sets <c>network.downloader</c> to <paramref name="downloader"/> in <paramref name="existing"/> (the file's text, or
    /// null when there is no file). <see cref="Default"/> removes the value (and a <c>network</c> object left empty), so
    /// winget's own choice applies. Every other key is kept in its order. Pure: nothing is read or written here.
    /// </summary>
    public static WingetSettingsMerge Merge(string? existing, string downloader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloader);
        var wanted = downloader.Trim().ToLowerInvariant();
        var remove = wanted == Default;

        JsonObject root;
        if (string.IsNullOrWhiteSpace(existing))
        {
            if (remove) return new WingetSettingsMerge(WingetSettingsMergeOutcome.Unchanged, null, null);
            root = new JsonObject { ["$schema"] = SchemaUrl };
        }
        else
        {
            try
            {
                if (JsonNode.Parse(existing, documentOptions: ReadOptions) is not JsonObject parsed)
                    return new WingetSettingsMerge(WingetSettingsMergeOutcome.Malformed, null, null, "the file does not hold a JSON object");
                root = parsed;
            }
            catch (JsonException ex)
            {
                return new WingetSettingsMerge(WingetSettingsMergeOutcome.Malformed, null, null, ex.Message);
            }
        }

        var networkNode = root["network"];
        if (networkNode is not null and not JsonObject)
            return new WingetSettingsMerge(WingetSettingsMergeOutcome.Malformed, null, null, "\"network\" is not an object");
        var network = networkNode as JsonObject;
        var current = CurrentDownloader(network);

        if (remove)
        {
            if (network is null || !network.ContainsKey("downloader")) return new WingetSettingsMerge(WingetSettingsMergeOutcome.Unchanged, null, null);
            network.Remove("downloader");
            if (network.Count == 0) root.Remove("network");
            return new WingetSettingsMerge(WingetSettingsMergeOutcome.Changed, Serialize(root), null);
        }

        if (string.Equals(current, wanted, StringComparison.Ordinal))
            return new WingetSettingsMerge(WingetSettingsMergeOutcome.Unchanged, null, wanted);
        if (network is null)
        {
            network = new JsonObject();
            root["network"] = network;
        }
        network["downloader"] = wanted;
        return new WingetSettingsMerge(WingetSettingsMergeOutcome.Changed, Serialize(root), wanted);
    }

    /// <summary>The <c>network.downloader</c> string, or null when there is none (or it is not a string).</summary>
    private static string? CurrentDownloader(JsonObject? network)
    {
        if (network?["downloader"] is not JsonValue value) return null;
        return value.TryGetValue<string>(out var text) ? text : null;
    }

    private static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions) + Environment.NewLine;

    /// <summary>
    /// Applies <paramref name="downloader"/> to the settings file at <paramref name="path"/>: read (a missing file is
    /// created, with its folder), <see cref="Merge"/>, and - only when the value changes - an atomic write. A file that
    /// cannot be parsed is left as it is and reported in <see cref="WingetSettingsApplyResult.Error"/>. Never throws for
    /// I/O problems.
    /// </summary>
    public static WingetSettingsApplyResult ApplyToFile(string path, string downloader)
    {
        string? existing;
        try
        {
            existing = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new WingetSettingsApplyResult(path, false, null, $"cannot read it: {ex.Message}");
        }

        var merge = Merge(existing, downloader);
        switch (merge.Outcome)
        {
            case WingetSettingsMergeOutcome.Malformed:
                return new WingetSettingsApplyResult(path, false, null, $"it is not valid JSON and is left as it is ({merge.Error})");
            case WingetSettingsMergeOutcome.Unchanged:
                return new WingetSettingsApplyResult(path, false, merge.Downloader);
        }

        var temp = path + ".arkimentum-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            var folder = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(temp, merge.Text!, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // MoveFileEx(MOVEFILE_REPLACE_EXISTING) within one folder: readers see the old file or the new one, never half of one.
            File.Move(temp, path, overwrite: true);
            return new WingetSettingsApplyResult(path, true, merge.Downloader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            return new WingetSettingsApplyResult(path, false, null, $"cannot write it: {ex.Message}");
        }
    }
}

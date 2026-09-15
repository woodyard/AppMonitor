using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Security;

namespace Arkimentum.AppMonitor.Api.Services;

/// <summary>
/// ConfigVersion is the ETag devices and the console cache on. Format: "{n}-{shorthash}" where n is a monotonically
/// increasing counter (so a human can see which is newer) and shorthash is the first 4 bytes of the SHA-256 of the
/// stored JSON (so an identical document keeps an identical tail and an accidental double-save is visible).
/// </summary>
public static class ConfigVersioning
{
    public const string Initial = "0-00000000";

    public static string Next(string? currentVersion, string compactJson)
    {
        var n = ParseSequence(currentVersion) + 1;
        return $"{n}-{Secrets.ShortHash(compactJson)}";
    }

    public static long ParseSequence(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var dash = version.IndexOf('-');
        var head = dash > 0 ? version[..dash] : version;
        return long.TryParse(head, out var n) && n >= 0 ? n : 0;
    }

    /// <summary>The configuration an organization gets before an administrator has ever saved one.</summary>
    public static SettingsDocument EmptyDocument() => new()
    {
        Description = "Organization configuration",
    };

    /// <summary>Normalises the If-Match / If-None-Match header value (strips quotes and any W/ prefix).</summary>
    public static string? Unquote(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag)) return null;
        var value = etag.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        value = value.Trim('"');
        return value.Length == 0 ? null : value;
    }
}

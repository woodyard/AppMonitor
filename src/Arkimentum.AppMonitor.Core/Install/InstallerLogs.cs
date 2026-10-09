using System.Text;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Install;

/// <summary>
/// The installer logs winget writes with <c>--log &lt;file&gt;</c> (see <see cref="Providers.ProviderOptions.InstallerLogDirectory"/>):
/// the file names, and the retention that keeps the folder small. winget passes the path on to installers that take a
/// log switch (an MSI's <c>/log</c>, Inno's <c>/LOG=</c>, burn's <c>/log</c>); installers without one (nullsoft) write
/// nothing, so a run may leave no file or an empty one behind.
/// </summary>
public static class InstallerLogs
{
    /// <summary>Installer logs older than this are deleted before the next install.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>At most this many installer logs (the newest) are kept.</summary>
    public const int MaxFiles = 50;

    /// <summary>
    /// An empty file is a leftover once it is this old: an installer that writes its log while it runs may hold an
    /// empty file open for a while, and installs are cut off after the install timeout long before this.
    /// </summary>
    public static readonly TimeSpan EmptyFileGrace = TimeSpan.FromHours(6);

    /// <summary>The longest application id and winget id that go into a file name, each.</summary>
    internal const int MaxNamePart = 40;

    /// <summary>
    /// <c>&lt;appId&gt;_&lt;wingetId&gt;_&lt;yyyyMMdd-HHmmss&gt;[_&lt;step&gt;].log</c>, with every character a file name cannot
    /// hold (and whitespace and the underscore separator itself) replaced by '-', each part cut to
    /// <see cref="MaxNamePart"/> characters. Pure, so the naming is testable.
    /// </summary>
    public static string FileName(string appId, string wingetId, DateTime timestamp, string? step = null)
    {
        var sb = new StringBuilder()
            .Append(Sanitize(appId, "app")).Append('_')
            .Append(Sanitize(wingetId, "winget")).Append('_')
            .Append(timestamp.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(step)) sb.Append('_').Append(Sanitize(step, "step"));
        return sb.Append(".log").ToString();
    }

    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) || c == '_' ? '-' : c).ToArray();
        var s = new string(chars).Trim('-', '.');
        if (s.Length > MaxNamePart) s = s[..MaxNamePart].TrimEnd('-', '.');
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>
    /// A new log file path in <paramref name="directory"/> (created when missing) for one winget run, unique among the
    /// files there and the paths in <paramref name="reserved"/> (which it is added to): a second run of the same step in the
    /// same second gets "-2", "-3" and so on before the extension. Null when the folder cannot be created; never throws.
    /// </summary>
    public static string? NewPath(string directory, string appId, string wingetId, string? step, DateTime timestamp,
        ISet<string>? reserved = null, ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var name = FileName(appId, wingetId, timestamp, step);
            var stem = Path.GetFileNameWithoutExtension(name);
            var path = Path.Combine(directory, name);
            lock (reserved ?? new object())
            {
                for (var n = 2; File.Exists(path) || (reserved?.Contains(path) ?? false); n++)
                    path = Path.Combine(directory, $"{stem}-{n}.log");
                reserved?.Add(path);
            }
            return path;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "The installer log folder {Directory} could not be used; winget runs without --log.", directory);
            return null;
        }
    }

    /// <summary>
    /// The retention of the installer log folder, run before each install: deletes the <c>*.log</c> files older than
    /// <paramref name="maxAge"/> (default <see cref="MaxAge"/>), the empty ones older than <see cref="EmptyFileGrace"/>, and
    /// then all but the <paramref name="maxFiles"/> (default <see cref="MaxFiles"/>) newest. Only files directly in the
    /// folder, by their last write time. Returns how many files were deleted; never throws (a file that cannot be deleted,
    /// e.g. one an installer still holds open, stays and is logged at Debug).
    /// </summary>
    public static int Prune(string directory, DateTime utcNow, ILogger? logger = null, TimeSpan? maxAge = null, int maxFiles = MaxFiles)
    {
        var deleted = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
            var age = maxAge ?? MaxAge;
            var files = new DirectoryInfo(directory).GetFiles("*.log", SearchOption.TopDirectoryOnly).ToList();
            var keep = new List<FileInfo>();
            foreach (var f in files)
            {
                var old = utcNow - f.LastWriteTimeUtc > age;
                var emptyLeftover = f.Length == 0 && utcNow - f.LastWriteTimeUtc > EmptyFileGrace;
                if (old || emptyLeftover) { if (TryDelete(f, logger)) deleted++; else keep.Add(f); }
                else keep.Add(f);
            }
            foreach (var f in keep.OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase).Skip(Math.Max(0, maxFiles)))
                if (TryDelete(f, logger)) deleted++;
            if (deleted > 0) logger?.LogDebug("Deleted {Count} old installer log(s) in {Directory}.", deleted, directory);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "The installer log folder {Directory} could not be pruned.", directory);
        }
        return deleted;
    }

    private static bool TryDelete(FileInfo file, ILogger? logger)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "The installer log {File} could not be deleted.", file.FullName);
            return false;
        }
    }

    /// <summary>Whether <paramref name="path"/> is a file with content (an installer wrote its log there).</summary>
    public static bool HasContent(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && new FileInfo(path).Exists && new FileInfo(path).Length > 0; }
        catch { return false; }
    }
}

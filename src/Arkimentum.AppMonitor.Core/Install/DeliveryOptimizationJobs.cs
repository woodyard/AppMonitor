using System.Management;

namespace Arkimentum.AppMonitor.Install;

/// <summary>
/// One download job of Delivery Optimization, as the CIM class <c>MSFT_DeliveryOptimizationFile</c> (namespace
/// <c>root/Microsoft/Windows/DeliveryOptimization</c>, what <c>Get-DeliveryOptimizationStatus</c> reads) lists it.
/// winget downloads installers through DO, which fills the file in winget's temp folder only at the very end, so this
/// is where the progress of the download can be seen.
/// </summary>
/// <param name="FileId">DO's id of the job.</param>
/// <param name="SourceUrl">The URL being downloaded (for a redirecting URL apparently the final one, not the one winget printed).</param>
/// <param name="FileSize">The size of the file, known from the start; null or 0 when DO does not say.</param>
/// <param name="TotalBytesDownloaded">How much of it is in so far.</param>
/// <param name="Status">DO's status code: 0 seen while downloading, 1 when complete; others (paused, caching) exist.</param>
/// <param name="CallerApplication">The predefined caller, "Windows Package Manager" for winget's downloads.</param>
public sealed record DeliveryOptimizationJob(
    string? FileId,
    string? SourceUrl,
    long? FileSize,
    long TotalBytesDownloaded,
    uint? Status,
    string? CallerApplication)
{
    /// <summary>The caller name DO records for winget's downloads.</summary>
    public const string WingetCaller = "Windows Package Manager";

    /// <summary>The status code DO reported for a finished download.</summary>
    public const uint StatusComplete = 1;

    /// <summary>All of the file is in, by DO's status or by the byte count.</summary>
    public bool IsComplete => Status == StatusComplete || (FileSize is { } size && size > 0 && TotalBytesDownloaded >= size);

    public bool IsWinget => string.Equals(CallerApplication?.Trim(), WingetCaller, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads Delivery Optimization's download jobs through WMI.</summary>
public static class DeliveryOptimizationJobs
{
    public const string Namespace = @"root\Microsoft\Windows\DeliveryOptimization";
    public const string ClassName = "MSFT_DeliveryOptimizationFile";

    /// <summary>How long one query may take; a measured one takes 15-40 ms.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every job DO lists right now (winget's and everybody else's: Windows Update, Store, Office). Throws when the
    /// namespace or the class is missing (DO disabled, an older Windows) or WMI fails; the caller decides what that means.
    /// </summary>
    public static IReadOnlyList<DeliveryOptimizationJob> Query(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = new ManagementScope(Namespace);
        var options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {ClassName}"), options);
        using var results = searcher.Get();
        var jobs = new List<DeliveryOptimizationJob>();
        foreach (var item in results)
        {
            using (item)
            {
                jobs.Add(new DeliveryOptimizationJob(
                    String(item, "FileId"),
                    String(item, "SourceURL"),
                    Number(item, "FileSize"),
                    Number(item, "TotalBytesDownloaded") ?? 0,
                    Number(item, "Status") is { } status and >= 0 and <= uint.MaxValue ? (uint)status : null,
                    String(item, "PredefinedCallerApplication")));
            }
        }
        return jobs;
    }

    private static object? Property(ManagementBaseObject item, string name)
    {
        try { return item[name]; }
        catch (ManagementException) { return null; } // a property this Windows build does not have
    }

    private static string? String(ManagementBaseObject item, string name) => Property(item, name)?.ToString();

    private static long? Number(ManagementBaseObject item, string name)
    {
        try
        {
            return Property(item, name) switch
            {
                null => null,
                ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
                IConvertible c => c.ToInt64(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}

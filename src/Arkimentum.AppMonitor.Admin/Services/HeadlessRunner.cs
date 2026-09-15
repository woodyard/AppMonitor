using System.IO;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.ViewModels;
using Arkimentum.AppMonitor.Configuration;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// The <c>--export</c> and <c>--import</c> switches: the whole job without a window. Failures go to the log and to
/// a single brand-styled message window (the process has no console to write to); success is silent.
/// </summary>
public sealed class HeadlessRunner
{
    private readonly ILogger<HeadlessRunner> _log;
    private readonly SettingsStoreService _store;
    private readonly IDialogService _dialogs;

    public HeadlessRunner(ILogger<HeadlessRunner> log, SettingsStoreService store, IDialogService dialogs)
    {
        _log = log;
        _store = store;
        _dialogs = dialogs;
    }

    /// <summary>Runs whichever headless job the command line asked for and returns the process exit code.</summary>
    public int Run(CommandLineOptions options) =>
        options.ExportPath is { } export ? Export(export, options)
        : options.ImportPath is { } import ? Import(import, options)
        : 0;

    private int Export(string path, CommandLineOptions options)
    {
        try
        {
            var format = FormatFor(path);
            if (format is null) return Fail(Strings.HeadlessUnknownFormat);

            var document = _store.Read(SettingsLayer.Preference);
            document.ExportedUtc = DateTimeOffset.UtcNow;
            document.ExportedBy = AdminAppInfo.UserName;
            document.ExportedFrom = Environment.MachineName;
            document.ProductVersion = AdminAppInfo.Version;

            var text = SettingsExporter.Export(document, format.Value, new SettingsExportOptions
            {
                TargetLayer = options.Policy ? SettingsLayer.Policy : SettingsLayer.Preference,
                ReplaceApps = !options.NoReplaceApps,
            });

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, text);

            _log.LogInformation("Exported the preference layer to {Path} ({Format}, target {Layer}, replaceApps={Replace}).",
                path, format, options.Policy ? SettingsLayer.Policy : SettingsLayer.Preference, !options.NoReplaceApps);
            return 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Headless export to {Path} failed.", path);
            return Fail(Strings.HeadlessExportFailed(ex.Message));
        }
    }

    private int Import(string path, CommandLineOptions options)
    {
        try
        {
            if (!File.Exists(path)) return Fail(Strings.HeadlessImportFailed(Strings.HeadlessMissingFile));

            var document = SettingsDocument.FromJson(File.ReadAllText(path));
            var problems = document.Validate();
            if (problems.Count > 0) return Fail(Strings.HeadlessImportFailed(string.Join(" ", problems)));

            var mode = options.Merge ? SettingsWriteMode.Merge : SettingsWriteMode.Replace;
            _store.Write(document, SettingsLayer.Preference, mode);
            _log.LogInformation("Imported {Path} into the preference layer ({Mode}): {Globals} global value(s), {Apps} application(s).",
                path, mode, document.Global.Count, document.Apps.Count);
            return 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Headless import of {Path} failed.", path);
            return Fail(Strings.HeadlessImportFailed(ex.Message));
        }
    }

    private int Fail(string message)
    {
        _log.LogError("{Message}", message);
        _dialogs.ShowMessage(Strings.HeadlessErrorTitle, message, DialogTone.Critical);
        return 1;
    }

    private static SettingsExportFormat? FormatFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => SettingsExportFormat.Json,
            ".reg" => SettingsExportFormat.RegFile,
            ".ps1" => SettingsExportFormat.PowerShell,
            _ => null,
        };
}

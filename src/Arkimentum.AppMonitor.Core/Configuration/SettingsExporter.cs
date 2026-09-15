using System.Globalization;
using System.Text;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Configuration;

public enum SettingsExportFormat
{
    /// <summary>JSON profile that the admin console can import again.</summary>
    Json,
    /// <summary>Registration Entries file for <c>reg import</c> / double-click.</summary>
    RegFile,
    /// <summary>Idempotent PowerShell script for Intune (platform script / Win32 app) or any deployment tool.</summary>
    PowerShell,
}

public sealed record SettingsExportOptions
{
    /// <summary>Write to the Policies key instead of the preference key.</summary>
    public SettingsLayer TargetLayer { get; init; } = SettingsLayer.Preference;
    /// <summary>Remove existing application entries on the target before writing (makes the export authoritative).</summary>
    public bool ReplaceApps { get; init; } = true;
    public string? Description { get; init; }
}

/// <summary>Turns a <see cref="SettingsDocument"/> into distributable artefacts. Pure string generation; no registry access.</summary>
public static class SettingsExporter
{
    public static string Export(SettingsDocument doc, SettingsExportFormat format, SettingsExportOptions? options = null) => format switch
    {
        SettingsExportFormat.Json => doc.ToJson(),
        SettingsExportFormat.RegFile => ToRegFile(doc, options),
        SettingsExportFormat.PowerShell => ToPowerShell(doc, options),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string FileExtension(SettingsExportFormat format) => format switch
    {
        SettingsExportFormat.Json => ".json",
        SettingsExportFormat.RegFile => ".reg",
        SettingsExportFormat.PowerShell => ".ps1",
        _ => ".txt",
    };

    // ------------------------------------------------------------------------------------------------------ .reg

    public static string ToRegFile(SettingsDocument doc, SettingsExportOptions? options = null)
    {
        options ??= new SettingsExportOptions();
        var root = @"HKEY_LOCAL_MACHINE\" + RegistrySettingsStore.PathFor(options.TargetLayer);
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        sb.AppendLine($"; {AgentSettings.ProductName} settings");
        if (!string.IsNullOrWhiteSpace(options.Description ?? doc.Description)) sb.AppendLine($"; {options.Description ?? doc.Description}");
        sb.AppendLine($"; Exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm} from {Environment.MachineName}. Import with: reg import <file>  (64-bit view)");
        sb.AppendLine();

        if (options.ReplaceApps)
        {
            sb.AppendLine("; Remove previously configured applications so this file is authoritative");
            sb.AppendLine($"[-{root}\\Apps]");
            sb.AppendLine($"[-{root}\\AppList]");
            sb.AppendLine();
        }

        sb.AppendLine($"[{root}]");
        foreach (var def in SettingsSchema.Global)
        {
            if (doc.Global.TryGetValue(def.Name, out var v)) sb.AppendLine(RegLine(def, v));
        }
        sb.AppendLine();

        foreach (var (appId, values) in doc.Apps)
        {
            sb.AppendLine($"[{root}\\Apps\\{appId}]");
            foreach (var def in SettingsSchema.App)
            {
                if (values.TryGetValue(def.Name, out var v)) sb.AppendLine(RegLine(def, v));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string RegLine(SettingDefinition def, SettingValue value)
    {
        var name = $"\"{def.Name}\"";
        switch (def.Kind)
        {
            case SettingKind.Bool:
                return $"{name}=dword:{(value.AsBool() is true ? 1 : 0):x8}";
            case SettingKind.Int:
                return $"{name}=dword:{(uint)(int)(value.AsInt() ?? 0):x8}";
            case SettingKind.StringList:
                return $"{name}=hex(7):{HexUtf16(value.AsStringList())}";
            case SettingKind.Path:
                return $"{name}=hex(2):{HexUtf16([value.AsString() ?? string.Empty])}";
            default:
                return $"{name}=\"{RegEscape(value.AsString() ?? string.Empty)}\"";
        }
    }

    /// <summary>UTF-16LE bytes, each string NUL-terminated, list terminated by an extra NUL (REG_MULTI_SZ / REG_EXPAND_SZ layout).</summary>
    private static string HexUtf16(IReadOnlyList<string> strings)
    {
        var bytes = new List<byte>();
        foreach (var s in strings)
        {
            bytes.AddRange(Encoding.Unicode.GetBytes(s));
            bytes.AddRange([0, 0]);
        }
        if (strings.Count != 1) bytes.AddRange([0, 0]); // multi-string terminator (expand-string has a single terminator)
        var hex = string.Join(",", bytes.Select(b => b.ToString("x2")));
        // .reg files wrap long hex lines with a trailing backslash; keep lines under ~80 chars
        var sb = new StringBuilder();
        var col = 0;
        foreach (var part in hex.Split(','))
        {
            if (col > 0) { sb.Append(','); col++; }
            if (col > 72) { sb.Append("\\\r\n  "); col = 2; }
            sb.Append(part);
            col += part.Length;
        }
        return sb.ToString();
    }

    private static string RegEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ------------------------------------------------------------------------------------------------------ PowerShell

    public static string ToPowerShell(SettingsDocument doc, SettingsExportOptions? options = null)
    {
        options ??= new SettingsExportOptions();
        var relative = RegistrySettingsStore.PathFor(options.TargetLayer);
        var sb = new StringBuilder();
        sb.AppendLine("<#");
        sb.AppendLine($"  {AgentSettings.ProductName} settings deployment script");
        if (!string.IsNullOrWhiteSpace(options.Description ?? doc.Description)) sb.AppendLine($"  {options.Description ?? doc.Description}");
        sb.AppendLine($"  Exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm} from {Environment.MachineName}.");
        sb.AppendLine();
        sb.AppendLine("  Run as SYSTEM or an administrator (Intune platform script, Win32 app install command, GPO startup script, RMM).");
        sb.AppendLine("  Idempotent: safe to run repeatedly. The service picks the new values up within one policy tick (default 60 s).");
        sb.AppendLine("#>");
        sb.AppendLine("[CmdletBinding(SupportsShouldProcess)]");
        sb.AppendLine("param(");
        sb.AppendLine($"    # Registry key to write. Default: the {(options.TargetLayer == SettingsLayer.Policy ? "policy" : "preference")} layer.");
        sb.AppendLine($"    [string]$RegistryPath = 'HKLM:\\{relative.Replace("\\", "\\")}',");
        sb.AppendLine($"    # Remove application entries that are not part of this script.");
        sb.AppendLine($"    # 1 = remove application entries that are not part of this script, 0 = keep them. (An integer, not a switch, so it");
        sb.AppendLine($"    # survives being passed through powershell.exe -File, which delivers every argument as text.)");
        sb.AppendLine($"    [ValidateSet(0, 1)][int]$ReplaceApps = {(options.ReplaceApps ? 1 : 0)}");
        sb.AppendLine(")");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();
        sb.AppendLine("# Re-launch in the 64-bit host when started from a 32-bit agent, so HKLM\\SOFTWARE is not redirected to WOW6432Node.");
        sb.AppendLine("if ($env:PROCESSOR_ARCHITEW6432 -eq 'AMD64' -and [IntPtr]::Size -eq 4) {");
        sb.AppendLine("    $relaunchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-RegistryPath', $RegistryPath, '-ReplaceApps', $ReplaceApps)");
        sb.AppendLine("    if ($WhatIfPreference) { $relaunchArgs += '-WhatIf' }");
        sb.AppendLine("    & \"$env:WINDIR\\SysNative\\WindowsPowerShell\\v1.0\\powershell.exe\" @relaunchArgs");
        sb.AppendLine("    exit $LASTEXITCODE");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function Set-Value([string]$Key, [string]$Name, $Value, [Microsoft.Win32.RegistryValueKind]$Kind) {");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $Key)) { New-Item -Path $Key -Force | Out-Null }");
        sb.AppendLine("    if ($PSCmdlet.ShouldProcess(\"$Key\\$Name\", 'Set')) { New-ItemProperty -LiteralPath $Key -Name $Name -Value $Value -PropertyType $Kind -Force | Out-Null }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("$apps = @(");
        sb.AppendLine(string.Join(",\r\n", doc.Apps.Keys.Select(a => $"    '{PsEscape(a)}'")));
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("Write-Host \"Writing Arkimentum AppMonitor settings to $RegistryPath\"");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $RegistryPath)) { New-Item -Path $RegistryPath -Force | Out-Null }");
        sb.AppendLine();
        sb.AppendLine("# ---- global settings");
        foreach (var def in SettingsSchema.Global)
        {
            if (doc.Global.TryGetValue(def.Name, out var v)) sb.AppendLine(PsSetLine("$RegistryPath", def, v));
        }
        sb.AppendLine();
        sb.AppendLine("# ---- applications");
        sb.AppendLine("if ($ReplaceApps -ne 0) {");
        sb.AppendLine("    if (Test-Path -LiteralPath \"$RegistryPath\\Apps\") {");
        sb.AppendLine("        Get-ChildItem -LiteralPath \"$RegistryPath\\Apps\" | Where-Object { $apps -notcontains $_.PSChildName } | ForEach-Object {");
        sb.AppendLine("            if ($PSCmdlet.ShouldProcess($_.PSPath, 'Remove app')) { Remove-Item -LiteralPath $_.PSPath -Recurse -Force }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    if (Test-Path -LiteralPath \"$RegistryPath\\AppList\") {");
        sb.AppendLine("        if ($PSCmdlet.ShouldProcess(\"$RegistryPath\\AppList\", 'Remove flat app list')) { Remove-Item -LiteralPath \"$RegistryPath\\AppList\" -Recurse -Force }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        foreach (var (appId, values) in doc.Apps)
        {
            var key = $"\"$RegistryPath\\Apps\\{PsEscapeDq(appId)}\"";
            sb.AppendLine();
            sb.AppendLine($"# {appId}");
            sb.AppendLine($"if (-not (Test-Path -LiteralPath {key})) {{ New-Item -Path {key} -Force | Out-Null }}");
            foreach (var def in SettingsSchema.App)
            {
                if (values.TryGetValue(def.Name, out var v)) sb.AppendLine(PsSetLine(key, def, v));
            }
        }
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Done.'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    private static string PsSetLine(string key, SettingDefinition def, SettingValue value)
    {
        var (literal, kind) = def.Kind switch
        {
            SettingKind.Bool => ((value.AsBool() is true ? "1" : "0"), "DWord"),
            SettingKind.Int => ((value.AsInt() ?? 0).ToString(CultureInfo.InvariantCulture), "DWord"),
            SettingKind.StringList => ("@(" + string.Join(", ", value.AsStringList().Select(s => $"'{PsEscape(s)}'")) + ")", "MultiString"),
            SettingKind.Path => ($"'{PsEscape(value.AsString() ?? "")}'", "ExpandString"),
            _ => ($"'{PsEscape(value.AsString() ?? "")}'", "String"),
        };
        return $"Set-Value {key} '{def.Name}' {literal} {kind}";
    }

    private static string PsEscape(string s) => s.Replace("'", "''");
    private static string PsEscapeDq(string s) => s.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");
}

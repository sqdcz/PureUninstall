using Microsoft.Win32;

namespace PureUninstall.Core.Models;

public enum AppKind { Win32, Msi, Uwp }

/// <summary>One installed application (desktop or store).</summary>
public sealed class AppEntry
{
    public string Id { get; set; } = "";                     // registry key name or PackageFullName
    public string DisplayName { get; set; } = "";
    public string? DisplayVersion { get; set; }
    public string? Publisher { get; set; }
    public string? InstallLocation { get; set; }
    public string? UninstallString { get; set; }
    public string? QuietUninstallString { get; set; }
    public string? DisplayIcon { get; set; }
    public long EstimatedSizeKb { get; set; }
    public DateTimeOffset? InstallDate { get; set; }
    public AppKind Kind { get; set; }
    public RegistryHive? Hive { get; set; }
    public RegistryView? View { get; set; }
    public string? RegistryKeyPath { get; set; }          // e.g. HKLM\SOFTWARE\...\Uninstall\<key>

    /// <summary>Actual measured size in bytes (filled by size scanner).</summary>
    public long? MeasuredSizeBytes { get; set; }

    public string KindText => Kind switch
    {
        AppKind.Msi => "MSI 安装",
        AppKind.Uwp => "应用商店",
        _ => "传统安装"
    };

    public string SizeText => MeasuredSizeBytes is > 0
        ? FormatBytes(MeasuredSizeBytes.Value)
        : EstimatedSizeKb > 0 ? FormatBytes(EstimatedSizeKb * 1024L) : "未知";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };
}

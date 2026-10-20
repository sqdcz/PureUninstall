using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>One backed-up item inside a manifest.</summary>
public sealed class BackupEntryInfo
{
    public ResidualKind Kind { get; set; }
    public string OriginalPath { get; set; } = "";
    public string? ValueName { get; set; }
    public string? BackupPath { get; set; }      // copy of file/dir or exported .reg
    public string? RegistryHive { get; set; }
    public string? RegistryView { get; set; }
    public bool BackupFailed { get; set; }
    public string? Error { get; set; }
}

/// <summary>Full backup record, stored as manifest.json next to the data.</summary>
public sealed class BackupManifest
{
    public string Id { get; set; } = "";
    public string AppName { get; set; } = "";
    public string? AppId { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<BackupEntryInfo> Items { get; set; } = new();
    public long TotalBytes { get; set; }

    public string CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText => TotalBytes > 0 ? AppEntry.FormatBytes(TotalBytes) : "";
}

/// <summary>Result of a restore run.</summary>
public sealed class RestoreResult
{
    public int Restored { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
}

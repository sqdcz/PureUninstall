using Microsoft.Win32;

namespace PureUninstall.Core.Models;

public enum ResidualKind
{
    Directory,
    File,
    RegistryKey,
    RegistryValue,
    ScheduledTask,
    Service,
    StartupEntry,
    FirewallRule,
    WmiInstance
}

/// <summary>A single leftover item found after (or instead of) an uninstall.</summary>
public sealed class ResidualItem
{
    public ResidualKind Kind { get; set; }
    public string Path { get; set; } = "";      // file path, registry path, task path, service name...
    public string? Detail { get; set; }            // value name, task action, service binary...
    public long SizeBytes { get; set; }
    public bool RequiresAdmin { get; set; }
    public bool Selected { get; set; } = true;

    // registry bookkeeping (set by registry scanners, used by the deleter)
    public RegistryHive? Hive { get; set; }
    public RegistryView? View { get; set; }
    public string? ValueName { get; set; }

    // backup bookkeeping (filled by BackupManager before deletion)
    public string? BackupPath { get; set; }

    public string KindText => Kind switch
    {
        ResidualKind.Directory => "文件夹",
        ResidualKind.File => "文件",
        ResidualKind.RegistryKey => "注册表项",
        ResidualKind.RegistryValue => "注册表值",
        ResidualKind.ScheduledTask => "计划任务",
        ResidualKind.Service => "服务",
        ResidualKind.StartupEntry => "启动项",
        ResidualKind.FirewallRule => "防火墙规则",
        ResidualKind.WmiInstance => "WMI 实例",
        _ => Kind.ToString()
    };

    public string SizeText => SizeBytes > 0 ? AppEntry.FormatBytes(SizeBytes) : "";
}

using Microsoft.Win32;

namespace PureUninstall.Core.Models;

public enum PersistenceKind
{
    RunKey,
    StartupFolder,
    Service,
    Task,
    WmiSubscription,
    WinlogonHook,
    IfeoDebugger,
    AppInitDll,
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// 一条「开机就会自己跑起来」的项。
///
/// 这类地方是银狐那类远控木马、以及流氓软件弹窗的主要落脚点，
/// 但它们同时也是正常软件的正常用法 —— 所以这里只做「可疑度」判断并给出依据，
/// 最终删不删由用户看着办。不喊「查杀」，是因为单靠这几个位置判断不了是不是病毒，
/// 那样只会给人假的安全感。
/// </summary>
public sealed class PersistenceItem
{
    public PersistenceKind Kind { get; init; }

    /// <summary>显示名（启动项名 / 服务名 / 任务名）。</summary>
    public string Name { get; init; } = "";

    /// <summary>实际会执行的命令行或目标文件。</summary>
    public string Command { get; init; } = "";

    /// <summary>注册表路径或磁盘路径，用于定位和删除。</summary>
    public string Location { get; init; } = "";

    public bool? IsSigned { get; init; }
    public string? Signer { get; init; }

    public RiskLevel Risk { get; set; }
    public List<string> Reasons { get; set; } = new();

    public bool RequiresAdmin { get; init; }

    /// <summary>系统自带组件（位于 Windows 目录）。默认折叠，不参与风险展示。</summary>
    public bool IsSystemComponent { get; init; }

    // ---- 删除所需：必须精确，删错就是灾难 ----
    public RegistryHive? Hive { get; init; }
    public RegistryView? View { get; init; }
    public string? ValueName { get; init; }

    /// <summary>转成删除器认识的类型；不支持删除的返回 null。</summary>
    public ResidualKind? DeletableKind => Kind switch
    {
        PersistenceKind.RunKey => ResidualKind.RegistryValue,
        PersistenceKind.WinlogonHook => ResidualKind.RegistryValue,
        PersistenceKind.IfeoDebugger => ResidualKind.RegistryKey,
        PersistenceKind.AppInitDll => ResidualKind.RegistryValue,
        PersistenceKind.StartupFolder => ResidualKind.File,
        PersistenceKind.Service => ResidualKind.Service,
        PersistenceKind.Task => ResidualKind.ScheduledTask,
        _ => null,   // WMI 订阅结构复杂，只展示不代删
    };

    public bool CanDelete => DeletableKind is not null;

    public string KindText => Kind switch
    {
        PersistenceKind.RunKey => "启动项",
        PersistenceKind.StartupFolder => "启动目录",
        PersistenceKind.Service => "服务",
        PersistenceKind.Task => "计划任务",
        PersistenceKind.WmiSubscription => "WMI 订阅",
        PersistenceKind.WinlogonHook => "登录劫持",
        PersistenceKind.IfeoDebugger => "调试器劫持",
        PersistenceKind.AppInitDll => "DLL 注入",
        _ => "其他",
    };

    public string RiskText => Risk switch
    {
        RiskLevel.High => "高",
        RiskLevel.Medium => "中",
        _ => "低",
    };

    public string SignerText => IsSigned switch
    {
        null => "未检查",
        false => "无签名",
        true => string.IsNullOrWhiteSpace(Signer) ? "有签名" : Shorten(Signer!),
    };

    private static string Shorten(string subject)
    {
        // CN=Microsoft Windows, OU=Microsoft Corporation, ... → 只留 CN
        foreach (var part in subject.Split(','))
        {
            var t = part.Trim();
            if (t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return t[3..];
        }
        return subject.Length > 40 ? subject[..40] + "…" : subject;
    }

    /// <summary>转成可删除项。</summary>
    public ResidualItem? ToResidual()
    {
        var kind = DeletableKind;
        if (kind is null) return null;

        return kind switch
        {
            ResidualKind.RegistryValue => new ResidualItem
            {
                Kind = ResidualKind.RegistryValue,
                Path = Location,
                Hive = Hive,
                View = View,
                ValueName = ValueName,
                Detail = Command,
            },
            ResidualKind.RegistryKey => new ResidualItem
            {
                Kind = ResidualKind.RegistryKey,
                Path = Location,
                Hive = Hive,
                View = View,
                Detail = Command,
            },
            ResidualKind.Service => new ResidualItem
            {
                Kind = ResidualKind.Service,
                Path = Name,          // 服务删除只认服务名
                Detail = Command,
            },
            ResidualKind.File => new ResidualItem
            {
                Kind = ResidualKind.File,
                Path = Location,
            },
            ResidualKind.ScheduledTask => new ResidualItem
            {
                Kind = ResidualKind.ScheduledTask,
                Path = Location,      // 形如 \Folder\TaskName
                Detail = Command,
            },
            _ => null,
        };
    }
}

using Microsoft.Win32;

namespace PureUninstall.Core.Models;

/// <summary>快照条目的类别。</summary>
public enum TraceItemKind
{
    Directory,
    File,
    Registry,
    Service,
    Task,
    Startup,
}

/// <summary>快照里的一个条目（一个目录、一个文件、一个注册表键、一个服务…）。</summary>
public sealed class TraceItem
{
    public TraceItemKind Kind { get; init; }

    /// <summary>
    /// 文件：完整路径；注册表：HKLM\SOFTWARE\...；服务：HKLM\SYSTEM\CurrentControlSet\Services\&lt;名字&gt;；
    /// 计划任务：磁盘上的 xml 路径。
    /// </summary>
    public string Path { get; init; } = "";

    public string? Detail { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>注册表类条目必须保留 hive 和视图，否则删除时可能写到 32/64 位错的位置。</summary>
    public RegistryHive? Hive { get; init; }
    public RegistryView? View { get; init; }

    /// <summary>注册表值的名字。启动项必须精确到值，不能把整个 Run 键删掉。</summary>
    public string? ValueName { get; init; }

    /// <summary>集合去重用的键：视图不同算两条，否则 32/64 位会互相覆盖。</summary>
    public string Key =>
        $"{(int)Kind}|{(Hive is null ? "-" : ((int)Hive).ToString())}|{(View is null ? "-" : ((int)View).ToString())}|{Path}|{ValueName}";

    public string KindText => Kind switch
    {
        TraceItemKind.Directory => "目录",
        TraceItemKind.File => "文件",
        TraceItemKind.Registry => "注册表",
        TraceItemKind.Service => "服务",
        TraceItemKind.Task => "计划任务",
        TraceItemKind.Startup => "启动项",
        _ => "其他",
    };

    public string SizeText => SizeBytes <= 0 ? "" : AppEntry.FormatBytes(SizeBytes);

    /// <summary>界面上第二行的说明文字。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Kind == TraceItemKind.Registry && View == RegistryView.Registry32) parts.Add("32 位视图");
            if (Kind == TraceItemKind.Startup && !string.IsNullOrEmpty(ValueName)) parts.Add("值名 " + ValueName);
            if (!string.IsNullOrEmpty(Detail)) parts.Add(Detail!);
            if (SizeBytes > 0) parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 一次追踪的结果：把「装前快照」和「装后快照」差出来。
/// 这就是那类工具「追踪安装」的真实原理——快照 + 差集，不需要任何第三方实现。
/// </summary>
public sealed class TraceSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }

    public int BeforeCount { get; set; }
    public int AfterCount { get; set; }

    public List<TraceItem> Added { get; set; } = new();

    public bool IsFinished => FinishedAt is not null;

    public long TotalBytes => Added.Where(a => a.Kind is TraceItemKind.File or TraceItemKind.Directory)
                                   .Sum(a => a.SizeBytes);

    public string StartedText => StartedAt.ToString("yyyy-MM-dd HH:mm");

    /// <summary>列表里显示的一行摘要。</summary>
    public string SummaryText
    {
        get
        {
            if (!IsFinished) return "进行中 · 装前快照已拍好";
            if (Added.Count == 0) return "没有检测到新增项";

            var bits = new List<string>();
            void Add(TraceItemKind kind, string label)
            {
                int n = Added.Count(a => a.Kind == kind);
                if (n > 0) bits.Add($"{label} {n}");
            }
            Add(TraceItemKind.Directory, "目录");
            Add(TraceItemKind.File, "文件");
            Add(TraceItemKind.Registry, "注册表");
            Add(TraceItemKind.Service, "服务");
            Add(TraceItemKind.Task, "任务");
            Add(TraceItemKind.Startup, "启动项");

            return $"新增 {Added.Count} 项 · " + string.Join(" · ", bits);
        }
    }
}

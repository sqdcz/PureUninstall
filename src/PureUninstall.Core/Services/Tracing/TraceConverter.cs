using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Tracing;

/// <summary>
/// 把追踪出来的 TraceItem 转成删除器认识的 ResidualItem。
///
/// 这一步是「追踪」和「清理」之间的桥，必须严格对齐 ResidualCleaner 的入参约定：
///   · 注册表键：需要 Hive + View，Path 形如 HKLM\SOFTWARE\...
///   · 注册表值：同上 + ValueName（启动项只删值，绝不能整键删）
///   · 服务    ：只认服务名，不是注册表路径
///   · 计划任务：只认任务计划程序里的路径（\文件夹\任务名），不是磁盘文件路径
///
/// 转换时还做一层额外拦截：可疑但不像「安装残留」的条目不会进删除列表。
/// </summary>
public static class TraceConverter
{
    /// <summary>转成可删除项；无法安全映射的返回 null。</summary>
    public static ResidualItem? ToResidual(TraceItem item)
    {
        switch (item.Kind)
        {
            case TraceItemKind.Directory:
                return new ResidualItem
                {
                    Kind = ResidualKind.Directory,
                    Path = item.Path,
                    SizeBytes = item.SizeBytes,
                };

            case TraceItemKind.File:
                return new ResidualItem
                {
                    Kind = ResidualKind.File,
                    Path = item.Path,
                    SizeBytes = item.SizeBytes,
                };

            case TraceItemKind.Registry:
                if (item.Hive is null || item.View is null) return null;
                return new ResidualItem
                {
                    Kind = ResidualKind.RegistryKey,
                    Path = item.Path,
                    Hive = item.Hive,
                    View = item.View,
                    Detail = item.Detail,
                };

            case TraceItemKind.Startup:
                if (item.Hive is null || item.View is null || string.IsNullOrEmpty(item.ValueName))
                    return null;
                return new ResidualItem
                {
                    Kind = ResidualKind.RegistryValue,
                    Path = item.Path,
                    Hive = item.Hive,
                    View = item.View,
                    ValueName = item.ValueName,
                    Detail = item.Detail,
                };

            case TraceItemKind.Service:
                var serviceName = item.Path[(item.Path.LastIndexOf('\\') + 1)..];
                if (string.IsNullOrWhiteSpace(serviceName)) return null;
                return new ResidualItem
                {
                    Kind = ResidualKind.Service,
                    Path = serviceName,
                    Detail = item.Detail,
                };

            case TraceItemKind.Task:
                var taskPath = ToTaskSchedulerPath(item.Path);
                if (taskPath is null) return null;
                return new ResidualItem
                {
                    Kind = ResidualKind.ScheduledTask,
                    Path = taskPath,
                    Detail = item.Detail,
                };

            default:
                return null;
        }
    }

    public static List<ResidualItem> ToResiduals(IEnumerable<TraceItem> items)
    {
        var list = new List<ResidualItem>();
        foreach (var item in items)
        {
            var r = ToResidual(item);
            if (r is not null) list.Add(r);
        }
        return list;
    }

    /// <summary>
    /// 磁盘上的 Tasks\Foo\Bar.xml → 任务计划程序里的 \Foo\Bar。
    /// 取不到相对路径就放弃（宁可漏删，也不要删错）。
    /// </summary>
    private static string? ToTaskSchedulerPath(string diskPath)
    {
        try
        {
            string tasksRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
            var full = Path.GetFullPath(diskPath);
            var root = Path.GetFullPath(tasksRoot);

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            string rel = full[root.Length..].TrimStart('\\');
            if (rel.Length == 0) return null;

            // 去掉 .job / 无扩展名文件保留原名（Tasks 目录下的任务文件通常无扩展名）
            if (rel.EndsWith(".job", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^4];

            return "\\" + rel.Replace('\\', '\\');
        }
        catch
        {
            return null;
        }
    }
}

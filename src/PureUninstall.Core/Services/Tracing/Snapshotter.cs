using Microsoft.Win32;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Tracing;

/// <summary>
/// 给系统拍「快照」：把关键位置的目录、文件、注册表键、服务、计划任务、启动项
/// 收进一个字典。装软件前后各拍一次，差集就是这次安装到底动了哪些地方。
///
/// 这是公开的通用手法（快照 + 差集），不依赖任何第三方软件的实现。
///
/// 三个设计取舍：
///  - 只下探 3 层：再深耗时指数上升，而安装程序的改动 99% 落在这三层内
///  - 每个根有数量上限，防止某个巨型目录把内存吃光
///  - 跳过 Temp / 缓存 / 日志这类时刻在变的位置，否则 diff 全是噪声
/// </summary>
public static class Snapshotter
{
    private const int MaxDepth = 3;
    private const int MaxItemsPerRoot = 60_000;

    /// <summary>这些片段出现在路径里就跳过 —— 它们一直在变，参与 diff 只会制造噪声。</summary>
    private static readonly string[] NoiseFragments =
    {
        @"\Temp\", @"\tmp\", @"\CrashDumps\", @"\D3DSCache\", @"\INetCache\",
        @"\WebCache\", @"\Recent\", @"\Cache\", @"\cache\",
        @"\IconCache\", @"\Packages\", @"\$Recycle.Bin\",
        @"\Windows\Prefetch\", @"\WER\", @"\SoftwareDistribution\",
        // 自己的数据目录必须排除：快照/日志/图标缓存都在写这里，
        // 不排除的话每次追踪都会把自己产生的文件当成"软件新增项"
        @"\PureUninstall\",
    };

    /// <summary>注册表：卸载登记项（32/64 位 + 用户级）。</summary>
    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] UninstallRoots =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser,    RegistryView.Default,   @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    /// <summary>注册表：开机自启。</summary>
    private static readonly (RegistryHive Hive, RegistryView View, string Path, string Label)[] RunRoots =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM Run (x86)"),
        (RegistryHive.CurrentUser,    RegistryView.Default,   @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU Run"),
        (RegistryHive.CurrentUser,    RegistryView.Default,   @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce"),
    };

    /// <summary>拍一张完整快照。Key 是 Kind|Path，便于集合运算。</summary>
    public static Dictionary<string, TraceItem> Capture(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var map = new Dictionary<string, TraceItem>(StringComparer.OrdinalIgnoreCase);

        progress?.Report("读取卸载登记项…");
        foreach (var (hive, view, path) in UninstallRoots)
            CollectSubKeys(map, hive, view, path, "卸载登记", ct);

        progress?.Report("读取 Windows 服务…");
        CollectSubKeys(map, RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SYSTEM\CurrentControlSet\Services", "服务", ct, valueName: "ImagePath");

        progress?.Report("读取启动项…");
        foreach (var (hive, view, path, label) in RunRoots)
            CollectValueNames(map, hive, view, path, label, ct);

        progress?.Report("读取计划任务…");
        CollectTasks(map, ct);

        progress?.Report("扫描文件系统…");
        foreach (var root in FileRoots())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"扫描 {Shorten(root)}…");
            CollectTree(map, root, ct);
        }

        progress?.Report($"快照完成，共 {map.Count} 项");
        return map;
    }

    /// <summary>差集：装后有、装前没有的条目，就是这次安装新增的。</summary>
    public static List<TraceItem> Diff(
        IReadOnlyDictionary<string, TraceItem> before,
        IReadOnlyDictionary<string, TraceItem> after)
    {
        var added = new List<TraceItem>();
        foreach (var kv in after)
        {
            if (!before.ContainsKey(kv.Key) && !IsNoise(kv.Value.Path))
                added.Add(kv.Value);
        }

        // 排序：目录在前（用户更关心「多出来一个文件夹」），同类按路径字母序
        return added
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- 采集器

    private static IEnumerable<string> FileRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] candidates =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c) || !Directory.Exists(c)) continue;
            if (seen.Add(c)) yield return c;
        }
    }

    /// <summary>枚举某个注册表根下的子键名（不递归，只取一层）。</summary>
    private static void CollectSubKeys(
        Dictionary<string, TraceItem> map,
        RegistryHive hive,
        RegistryView view,
        string path,
        string label,
        CancellationToken ct,
        string? valueName = null)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(path);
            if (key is null) return;

            string hiveText = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

            foreach (var name in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                string detail = label;
                if (valueName is not null)
                {
                    try
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub?.GetValue(valueName) is string v && v.Length > 0)
                            detail = v;
                    }
                    catch { /* 个别键无权限，忽略详情 */ }
                }

                var item = new TraceItem
                {
                    Kind = label == "服务" ? TraceItemKind.Service : TraceItemKind.Registry,
                    // 路径里不放 32/64 位标记，视图单独存字段——删除时必须靠它定位正确的注册表视图
                    Path = $@"{hiveText}\{path}\{name}",
                    Hive = hive,
                    View = view,
                    Detail = detail,
                };
                map[item.Key] = item;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // 无权限的根直接跳过，不影响其它位置
        }
    }

    /// <summary>枚举某个注册表位置下的值名（用于 Run / RunOnce）。</summary>
    private static void CollectValueNames(
        Dictionary<string, TraceItem> map,
        RegistryHive hive,
        RegistryView view,
        string path,
        string label,
        CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(path);
            if (key is null) return;

            string hiveText = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                ct.ThrowIfCancellationRequested();

                var item = new TraceItem
                {
                    Kind = TraceItemKind.Startup,
                    // 只记到 Run 键这一层，值名单独存——删除时只删那一个值，不能整键删除
                    Path = $@"{hiveText}\{path}",
                    Hive = hive,
                    View = view,
                    ValueName = name,
                    Detail = key.GetValue(name) as string ?? label,
                };
                map[item.Key] = item;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    /// <summary>计划任务在磁盘上就是一个 xml 文件，直接看目录更快更准。</summary>
    private static void CollectTasks(Dictionary<string, TraceItem> map, CancellationToken ct)
    {
        string tasksDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (!Directory.Exists(tasksDir)) return;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((tasksDir, 0));
        int guard = 0;

        while (queue.Count > 0 && guard++ < 4000)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { files = Array.Empty<string>(); }

            foreach (var f in files)
            {
                var item = new TraceItem
                {
                    Kind = TraceItemKind.Task,
                    Path = f,
                    Detail = "计划任务",
                };
                map[item.Key] = item;
            }

            if (depth >= 2) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var s in subs) queue.Enqueue((s, depth + 1));
        }
    }

    /// <summary>广度优先遍历一个目录，限制层数与条目数。</summary>
    private static void CollectTree(Dictionary<string, TraceItem> map, string root, CancellationToken ct)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        int added = 0;

        while (queue.Count > 0 && added < MaxItemsPerRoot)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();

            string[] dirs;
            string[] files;
            try
            {
                dirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                continue;
            }

            foreach (var d in dirs)
            {
                if (IsNoise(d)) continue;

                // 跳过 junction / 符号链接：%LOCALAPPDATA%\Application Data 这类兼容链接
                // 会指向别的目录，不只让同一个东西被记好几份，还会把无关内容扫进来
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                var item = new TraceItem { Kind = TraceItemKind.Directory, Path = d };
                map[item.Key] = item;
                added++;

                if (depth + 1 < MaxDepth)
                    queue.Enqueue((d, depth + 1));
            }

            foreach (var f in files)
            {
                if (IsNoise(f)) continue;
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                var item = new TraceItem { Kind = TraceItemKind.File, Path = f, SizeBytes = size };
                map[item.Key] = item;
                added++;
            }
        }
    }

    // ---------------------------------------------------------------- 工具

    private static bool IsNoise(string path)
    {
        foreach (var frag in NoiseFragments)
            if (path.Contains(frag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string Shorten(string path)
    {
        try
        {
            var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 2 ? path : $"{parts[0]}\\…\\{parts[^1]}";
        }
        catch
        {
            return path;
        }
    }
}

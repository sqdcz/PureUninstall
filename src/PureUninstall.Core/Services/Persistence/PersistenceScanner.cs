using System.Management;
using System.Xml.Linq;
using Microsoft.Win32;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services.Scanners;
using PureUninstall.Core.Services.Signatures;

namespace PureUninstall.Core.Services.Persistence;

/// <summary>
/// 全系统「会自动运行」的位置扫描。
///
/// 和 ResidualScanner 的区别：那个是「围着某个软件找残留」，
/// 这个是「不管哪个软件，把所有持久化落脚点摊开给人看」——
/// 木马、流氓软件的后台服务、弹窗推广，都靠这些位置活下来。
///
/// 采集完统一定风险：先快速采（注册表 + 目录遍历），再并行读签名，
/// 避免 300 多次签名检查串行拖成十几秒。
/// </summary>
public static class PersistenceScanner
{
    private readonly record struct Raw(
        PersistenceKind Kind,
        string Name,
        string Command,
        string Location,
        RegistryHive? Hive,
        RegistryView? View,
        string? ValueName,
        bool RequiresAdmin);

    /// <summary>
    /// 扫描全部持久化位置。
    /// <paramref name="fast"/> = true 时跳过数字签名检查（每个文件都要读证书，是主要耗时），
    /// 供实时监控轮询用 —— 监控只关心「多出了什么」，不需要每轮都验签。
    /// </summary>
    public static List<PersistenceItem> Scan(
        IProgress<string>? progress = null, CancellationToken ct = default, bool fast = false)
    {
        var raw = new List<Raw>();

        progress?.Report("扫描开机启动项…");
        ScanRunKeys(raw, ct);

        progress?.Report("扫描启动文件夹…");
        ScanStartupFolders(raw, ct);

        progress?.Report("扫描 Windows 服务…");
        ScanServices(raw, ct);

        progress?.Report("扫描计划任务…");
        ScanTasks(raw, ct);

        progress?.Report("扫描登录劫持…");
        ScanWinlogon(raw, ct);

        progress?.Report("扫描调试器劫持…");
        ScanIfeo(raw, ct);

        progress?.Report("扫描 DLL 注入…");
        ScanAppInit(raw, ct);

        progress?.Report("扫描 WMI 事件订阅…");
        ScanWmi(raw, ct);

        progress?.Report($"分析 {raw.Count} 项的签名与风险…");
        var items = Analyse(raw, ct, fast);

        // 叠加用户导入的特征库（没导入就是空操作）
        int hits = SignatureStore.Apply(items);
        if (hits > 0) progress?.Report($"特征库命中 {hits} 项");

        return items
            .OrderByDescending(p => p.Risk)
            .ThenBy(p => p.Kind)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- 采集

    private static readonly (RegistryHive Hive, RegistryView View, string Path, string Label)[] RunKeys =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM\\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM\\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM(32)\\Run"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU\\Run"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU\\RunOnce"),
    };

    private static void ScanRunKeys(List<Raw> raw, CancellationToken ct)
    {
        foreach (var (hive, view, path, label) in RunKeys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(path);
                if (key is null) continue;

                string hiveText = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    ct.ThrowIfCancellationRequested();
                    string cmd = key.GetValue(name)?.ToString() ?? "";
                    raw.Add(new Raw(PersistenceKind.RunKey, name, cmd,
                        $@"{hiveText}\{path}", hive, view, name,
                        hive == RegistryHive.LocalMachine));
                }
            }
            catch { }
        }
    }

    private static void ScanStartupFolders(List<Raw> raw, CancellationToken ct)
    {
        string[] folders =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };

        foreach (var dir in folders)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    raw.Add(new Raw(PersistenceKind.StartupFolder, Path.GetFileNameWithoutExtension(f),
                        ReadShortcutTarget(f) ?? f, f, null, null, null, false));
                else
                    raw.Add(new Raw(PersistenceKind.StartupFolder, Path.GetFileName(f), f, f, null, null, null, false));
            }
        }
    }

    /// <summary>读 .lnk 目标。用 WScript.Shell 比解析二进制省事得多。</summary>
    private static string? ReadShortcutTarget(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath as string ?? "";
            string args = shortcut.Arguments as string ?? "";
            return (target + " " + args).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void ScanServices(List<Raw> raw, CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return;

            foreach (var name in services.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var svc = services.OpenSubKey(name);
                    if (svc is null) continue;

                    int type = svc.GetValue("Type") is int t ? t : 0;
                    // Type 2/16/32 = 服务；1/8 = 驱动。驱动也算持久化，一并列出
                    string imagePath = svc.GetValue("ImagePath")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(imagePath)) continue;

                    string display = svc.GetValue("DisplayName")?.ToString() ?? name;
                    if (display.StartsWith("@", StringComparison.Ordinal))
                        display = name;   // 资源引用，取不到真实名称就用键名

                    raw.Add(new Raw(PersistenceKind.Service, name, imagePath,
                        $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}",
                        RegistryHive.LocalMachine, RegistryView.Registry64, null, true));
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanTasks(List<Raw> raw, CancellationToken ct)
    {
        string tasksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (!Directory.Exists(tasksDir)) return;

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((tasksDir, 0));
        int guard = 0;

        while (stack.Count > 0 && guard++ < 5000)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(tasksDir, f);
                string taskPath = "\\" + rel;
                raw.Add(new Raw(PersistenceKind.Task, rel, ReadTaskAction(f),
                    taskPath, null, null, null, false));
            }

            if (depth >= 2) continue;
            try
            {
                foreach (var d in Directory.GetDirectories(dir)) stack.Push((d, depth + 1));
            }
            catch { }
        }
    }

    /// <summary>从任务 XML 里取第一条动作（Exec 或 ComHandler）。</summary>
    private static string ReadTaskAction(string file)
    {
        try
        {
            var doc = XDocument.Load(file);
            var exec = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Exec");
            if (exec is not null)
            {
                string cmd = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value ?? "";
                string args = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value ?? "";
                return (cmd + " " + args).Trim();
            }

            var com = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ComHandler");
            if (com is not null)
            {
                string clsid = com.Elements().FirstOrDefault(e => e.Name.LocalName == "ClassId")?.Value ?? "";
                return "(COM 处理器) " + clsid;
            }
        }
        catch { }
        return "";
    }

    private static void ScanWinlogon(List<Raw> raw, CancellationToken ct)
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key is null) return;

            foreach (var valueName in new[] { "Shell", "Userinit" })
            {
                string val = key.GetValue(valueName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(val)) continue;

                bool normal = valueName == "Shell"
                    ? val.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                    : val.Contains(@"C:\Windows\system32\userinit.exe", StringComparison.OrdinalIgnoreCase);
                if (normal) continue;      // 默认值不列出

                raw.Add(new Raw(PersistenceKind.WinlogonHook, "Winlogon " + valueName, val,
                    $@"HKLM\{path}", RegistryHive.LocalMachine, RegistryView.Registry64, valueName, true));
            }
        }
        catch { }
    }

    private static void ScanIfeo(List<Raw> raw, CancellationToken ct)
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var ifeo = baseKey.OpenSubKey(path);
            if (ifeo is null) return;

            foreach (var exe in ifeo.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var sub = ifeo.OpenSubKey(exe);
                    string debugger = sub?.GetValue("Debugger")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(debugger)) continue;

                    raw.Add(new Raw(PersistenceKind.IfeoDebugger, exe, debugger,
                        $@"HKLM\{path}\{exe}", RegistryHive.LocalMachine, RegistryView.Registry64, null, true));
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanAppInit(List<Raw> raw, CancellationToken ct)
    {
        foreach (var (view, label) in new[] { (RegistryView.Registry64, "64 位"), (RegistryView.Registry32, "32 位") })
        {
            const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(path);
                string dlls = key?.GetValue("AppInit_DLLs")?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(dlls)) continue;

                raw.Add(new Raw(PersistenceKind.AppInitDll, "AppInit_DLLs " + label, dlls,
                    $@"HKLM\{path}", RegistryHive.LocalMachine, view, "AppInit_DLLs", true));
            }
            catch { }
        }
    }

    private static void ScanWmi(List<Raw> raw, CancellationToken ct)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\subscription");
            scope.Connect();

            // 命令型消费者：直接执行命令行，恶意程序最爱用
            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM CommandLineEventConsumer")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    string name = mo["Name"]?.ToString() ?? "(未命名)";
                    string cmd = mo["CommandLineTemplate"]?.ToString() ?? "";
                    raw.Add(new Raw(PersistenceKind.WmiSubscription, name, cmd,
                        @"\\.\root\subscription:CommandLineEventConsumer", null, null, null, true));
                }
            }

            // 脚本型消费者：只列名字和脚本前 200 字，避免整段脚本刷屏
            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM ActiveScriptEventConsumer")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    string name = mo["Name"]?.ToString() ?? "(未命名)";
                    string script = mo["ScriptText"]?.ToString() ?? "";
                    if (script.Length > 200) script = script[..200] + " …";
                    raw.Add(new Raw(PersistenceKind.WmiSubscription, name, script.Replace("\r", " ").Replace("\n", " "),
                        @"\\.\root\subscription:ActiveScriptEventConsumer", null, null, null, true));
                }
            }
        }
        catch
        {
            // WMI 不可用或被安全软件拦截，忽略
        }
    }

    // ---------------------------------------------------------------- 分析

    private static List<PersistenceItem> Analyse(List<Raw> raw, CancellationToken ct, bool fast = false)
    {
        var result = new PersistenceItem[raw.Count];

        Parallel.For(0, raw.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var r = raw[i];
            string? exe = RiskEvaluator.ExtractExecutable(r.Command);
            var (signed, signer) = fast ? ((bool?)null, (string?)null) : RiskEvaluator.GetSignature(exe);
            var (risk, reasons) = RiskEvaluator.Evaluate(r.Command, exe, signed, signer);

            result[i] = new PersistenceItem
            {
                Kind = r.Kind,
                Name = r.Name,
                Command = r.Command,
                Location = r.Location,
                Hive = r.Hive,
                View = r.View,
                ValueName = r.ValueName,
                IsSigned = signed,
                Signer = signer,
                Risk = risk,
                Reasons = reasons,
                RequiresAdmin = r.RequiresAdmin,
                IsSystemComponent = RiskEvaluator.IsSystemPath(exe),
            };
        });

        return result.ToList();
    }
}

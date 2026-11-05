using System.Diagnostics;
using Microsoft.Win32;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;
using PureUninstall.Core.Services.Scanners;
using PureUninstall.Core.Services.Tracing;

// ── 持久化扫描：把全系统的自启动落脚点摊开看（只读，不删任何东西）====
if (args.Length > 0 && args[0] == "--persist-test")
{
    Console.WriteLine("=== 持久化位置扫描（只读） ===\n");
    var sw = Stopwatch.StartNew();
    var items = PureUninstall.Core.Services.Persistence.PersistenceScanner.Scan(
        new Progress<string>(m => Console.WriteLine("  … " + m)));
    sw.Stop();

    Console.WriteLine($"\n共 {items.Count} 项，耗时 {sw.Elapsed.TotalSeconds:F1}s");
    foreach (var g in items.GroupBy(i => i.Risk).OrderByDescending(g => g.Key))
        Console.WriteLine($"  {g.Key,-6} {g.Count(),4} 项");

    Console.WriteLine("\n—— 高风险 ——");
    foreach (var i in items.Where(i => i.Risk == RiskLevel.High).Take(15))
    {
        Console.WriteLine($"  [{i.KindText}] {i.Name}");
        Console.WriteLine($"      命令: {Trim(i.Command, 110)}");
        Console.WriteLine($"      签名: {i.SignerText}   位置: {Trim(i.Location, 80)}");
        foreach (var r in i.Reasons) Console.WriteLine($"      · {r}");
    }

    Console.WriteLine("\n—— 中风险（前 12 条）——");
    foreach (var i in items.Where(i => i.Risk == RiskLevel.Medium).Take(12))
        Console.WriteLine($"  [{i.KindText}] {i.Name}  |  {Trim(i.Command, 80)}  |  {i.SignerText}");

    var byKind = items.GroupBy(i => i.KindText).OrderByDescending(g => g.Count());
    Console.WriteLine("\n—— 按类型 ——");
    foreach (var g in byKind) Console.WriteLine($"  {g.Key,-10} {g.Count(),4} 项");

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    return 0;
}

// ── 安装追踪端到端测试：造几个假改动，验证快照/比对/转换能不能正确抓到 ====
if (args.Length > 0 && args[0] == "--trace-test")
{
    Console.WriteLine("=== 安装追踪 · 端到端测试 ===");
    Console.WriteLine("（只在 HKCU 和 %LOCALAPPDATA% 下造测试数据，测完自动清理）\n");

    string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PU_TraceTest");
    string regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PU_TraceTest";
    string regPath2 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PU_TraceTest2";
    string runPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    string runValue = "PU_TraceTest_Startup";
    int failures = 0;

    void Cleanup()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(regPath2, throwOnMissingSubKey: false); } catch { }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(runPath, writable: true);
            k?.DeleteValue(runValue, throwOnMissingValue: false);
        }
        catch { }
    }

    Cleanup();   // 先清一遍上次的残留

    Console.WriteLine("[1/5] 拍装前快照…");
    var sw = Stopwatch.StartNew();
    var before = Snapshotter.Capture();
    sw.Stop();
    Console.WriteLine($"      完成：{before.Count} 项，耗时 {sw.Elapsed.TotalSeconds:F1}s");
    if (before.Count < 100) { Console.WriteLine("      !! 条目数异常偏少"); failures++; }
    Console.WriteLine();

    // 关键验证：什么都没改的情况下连续拍两次，diff 必须为 0
    // 不为 0 说明快照本身不稳定，那追踪结果就不可信
    Console.WriteLine("[1.5/5] 稳定性验证：连续两次快照（不改任何东西）");
    var snap2 = Snapshotter.Capture();
    var selfDiff = Snapshotter.Diff(before, snap2);
    Console.WriteLine($"      两次快照：{before.Count} 项 vs {snap2.Count} 项，差异 {selfDiff.Count} 项");
    if (selfDiff.Count > 0)
    {
        foreach (var d in selfDiff.Take(10)) Console.WriteLine($"         ? [{d.KindText}] {d.Path}");
        Console.WriteLine("      !! 快照不稳定，需要排查");
        failures++;
    }
    Console.WriteLine();

    Console.WriteLine("[2/5] 模拟安装：1 目录 + 2 文件 + 1 卸载登记项 + 1 启动项");
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "a.txt"), "trace test");
    Directory.CreateDirectory(Path.Combine(root, "sub"));
    File.WriteAllText(Path.Combine(root, "sub", "b.dll"), new string('x', 2048));
    using (var k = Registry.CurrentUser.CreateSubKey(regPath))
    {
        k.SetValue("DisplayName", "PU 追踪测试");
        k.SetValue("DisplayVersion", "1.0");
    }
    using (var k = Registry.CurrentUser.OpenSubKey(runPath, writable: true))
        k?.SetValue(runValue, Path.Combine(root, "a.txt"));
    Console.WriteLine("      已写入");
    Console.WriteLine();

    Console.WriteLine("[3/5] 拍装后快照并比对…");
    sw.Restart();
    var after = Snapshotter.Capture();
    var diff = Snapshotter.Diff(before, after);
    sw.Stop();
    Console.WriteLine($"      装后 {after.Count} 项 / 新增 {diff.Count} 项 / 耗时 {sw.Elapsed.TotalSeconds:F1}s");
    foreach (var d in diff.Take(20))
        Console.WriteLine($"      + [{d.KindText,-6}] {d.Path}{(d.ValueName is null ? "" : "  (值 " + d.ValueName + ")")}");
    if (diff.Count > 20) Console.WriteLine($"      … 另有 {diff.Count - 20} 项");
    Console.WriteLine();

    bool okDir = diff.Any(d => d.Kind == TraceItemKind.Directory &&
                               d.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
    bool okFile = diff.Any(d => d.Kind == TraceItemKind.File &&
                                d.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
    bool okReg = diff.Any(d => d.Kind == TraceItemKind.Registry &&
                               d.Path.Contains("PU_TraceTest", StringComparison.OrdinalIgnoreCase));
    bool okRun = diff.Any(d => d.Kind == TraceItemKind.Startup && d.ValueName == runValue);

    Console.WriteLine($"[4/5] 抓取校验：目录={okDir} 文件={okFile} 注册表={okReg} 启动项={okRun}");
    foreach (var (name, ok) in new[] { ("目录", okDir), ("文件", okFile), ("注册表", okReg), ("启动项", okRun) })
        if (!ok) { Console.WriteLine($"      !! 没抓到{name}"); failures++; }

    // 转换：把 diff 转成删除器认识的格式，并核对关键字段
    var residuals = TraceConverter.ToResiduals(diff);
    Console.WriteLine($"      转换：{diff.Count} 项 → {residuals.Count} 个可删除项");
    var regItem = residuals.FirstOrDefault(r => r.Kind == ResidualKind.RegistryKey && r.Path.Contains("PU_TraceTest"));
    var runItem = residuals.FirstOrDefault(r => r.Kind == ResidualKind.RegistryValue && r.ValueName == runValue);
    var dirItem = residuals.FirstOrDefault(r => r.Kind == ResidualKind.Directory && r.Path.Equals(root, StringComparison.OrdinalIgnoreCase));

    void Check(string name, bool ok)
    {
        Console.WriteLine($"      {(ok ? "OK  " : "FAIL")} {name}");
        if (!ok) failures++;
    }
    Check("注册表项带 Hive/View", regItem is not null && regItem.Hive is not null && regItem.View is not null);
    Check("启动项精确到值名（不能整键删）", runItem is not null && !string.IsNullOrEmpty(runItem.ValueName));
    Check("目录路径原样保留", dirItem is not null);
    Check("没有重复路径（junction 不应重复记录）",
          diff.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == diff.Count);
    Console.WriteLine();

    // ── 落盘链路：StartAsync → FinishAsync → 读回 → 序列化完整性 ──
    Console.WriteLine("[5/6] 落盘链路：StartAsync / FinishAsync / 读回");
    string traceId = "";
    try
    {
        var sess = await TraceStore.StartAsync("冒烟测试追踪", null, null);
        traceId = sess.Id;

        // 开始追踪之后再造一点改动，用于验证 diff 与字段落盘
        File.WriteAllText(Path.Combine(root, "after-start.txt"), "changed after start");
        using (var k2 = Registry.CurrentUser.CreateSubKey(regPath2)) k2.SetValue("DisplayName", "PU 追踪测试2");
        Check($"装前快照已落盘（{sess.BeforeCount} 项）", sess.BeforeCount > 100 &&
              File.Exists(Path.Combine(TraceStore.Root, traceId, "before.snap")));

        var fin = await TraceStore.FinishAsync(traceId, null);
        Check($"装后比对完成（新增 {fin.Added.Count} 项，应为 2）", fin.IsFinished && fin.Added.Count == 2);

        // 诊断：这段时间系统本身的变动有多少（追踪期间用户装了别的软件就会这样）
        Console.WriteLine("      —— 新增项前 12 条（看噪声来源）——");
        foreach (var a in fin.Added.Take(12))
            Console.WriteLine($"         [{a.KindText,-6}] {a.Path}");
        var byRoot = fin.Added
            .GroupBy(a => a.Path.Split('\\').Skip(3).FirstOrDefault() ?? "?")
            .OrderByDescending(g => g.Count()).Take(6);
        Console.WriteLine("      —— 按目录分布 ——");
        foreach (var g in byRoot)
            Console.WriteLine($"         {g.Count(),5} 项  …\\{g.Key}");

        var loaded = await TraceStore.LoadAsync(traceId);
        Check("session.json 反序列化后条目没丢",
              loaded is not null && loaded.Added.Count == fin.Added.Count);
        Check("反序列化后 Hive/View 字段还在",
              loaded is not null && loaded.Added.Any(a => a.Kind == TraceItemKind.Registry && a.Hive is not null));

        var all = await TraceStore.ListAsync();
        Check("列表能读到这条记录", all.Any(s => s.Id == traceId));

        loaded!.Name = "冒烟测试追踪-改名";
        await TraceStore.SaveAsync(loaded);
        var reloaded = await TraceStore.LoadAsync(traceId);
        Check("改名后能写回并读回", reloaded?.Name == "冒烟测试追踪-改名");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"      !! 抛异常：{ex.Message}");
        failures++;
    }
    finally
    {
        if (traceId.Length > 0) TraceStore.Delete(traceId);
        Check("追踪记录已删除", !Directory.Exists(Path.Combine(TraceStore.Root, traceId)));
    }
    Console.WriteLine();

    Console.WriteLine("[6/6] 清理测试数据");
    Cleanup();
    bool cleaned = !Directory.Exists(root) && Registry.CurrentUser.OpenSubKey(regPath) is null;
    Check("测试数据已清理干净", cleaned);
    Console.WriteLine();

    Console.WriteLine(failures == 0 ? "TRACE TEST OK" : $"TRACE TEST FAILED ({failures} 项)");
    return failures == 0 ? 0 : 1;
}

if (args.Length > 0 && args[0] != "--backup-test" && args[0] != "--parse-test")
{
    // scan mode: PureUninstall.Smoke <app name substring>
    var query = string.Join(' ', args);
    var all = InstalledApps.EnumerateWin32().Concat(UwpApps.Enumerate()).ToList();
    var matches = all.Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"=== 扫描模式：'{query}' 匹配到 {matches.Count} 个 ===");
    foreach (var m in matches)
        Console.WriteLine($"  - {m.DisplayName} [{m.KindText}] {m.RegistryKeyPath ?? "(UWP)"}");
    if (matches.Count == 0) return 1;

    var target = matches[0];
    Console.WriteLine();
    Console.WriteLine($"=== 扫描「{target.DisplayName}」的残留（只扫不删）===");
    var sw = Stopwatch.StartNew();
    var progress = new Progress<string>(s => Console.WriteLine($"  … {s}"));
    var residuals = await ResidualScanner.ScanAsync(target, includeAggressive: true, progress);
    sw.Stop();

    foreach (var r in residuals)
    {
        var size = r.SizeBytes > 0 ? $"  ({r.SizeText})" : "";
        var detail = r.Detail is null ? "" : $"  <- {r.Detail}";
        var admin = r.RequiresAdmin ? "  [需管理员]" : "";
        Console.WriteLine($"  [{r.KindText,-8}] {r.Path}{detail}{size}{admin}");
    }
    Console.WriteLine($"共 {residuals.Count} 项，耗时 {sw.Elapsed.TotalSeconds:F1}s，未删除任何东西");
    return 0;
}

if (args.Length > 0 && args[0] == "--parse-test")
{
    // ParseCommand tests against real-world uninstall strings
    var failures = 0;
    void Check(string cmd, string expectFile, string expectArgs)
    {
        var (file, args) = Uninstaller.ParseCommand(cmd);
        if (file == expectFile && args == expectArgs)
        {
            Console.WriteLine($"PASS: {cmd}");
        }
        else
        {
            failures++;
            Console.WriteLine($"FAIL: {cmd}");
            Console.WriteLine($"   got file='{file}' args='{args}'");
            Console.WriteLine($"   expected file='{expectFile}' args='{expectArgs}'");
        }
    }

    Check(@"""C:\Program Files\Arduino IDE\Uninstall Arduino IDE.exe"" /allusers",
        @"C:\Program Files\Arduino IDE\Uninstall Arduino IDE.exe", "/allusers");
    Check(@"D:\Program Files\bilibili\卸载哔哩哔哩.exe",
        @"D:\Program Files\bilibili\卸载哔哩哔哩.exe", "");
    Check("msiexec /X {GUID}", "msiexec", "/X {GUID}");
    Check(@"C:\Program Files\Git\unins000.exe /SILENT", @"C:\Program Files\Git\unins000.exe", "/SILENT");
    Check(@"""D:\FeverGames\FeverGamesLauncher.exe"" fevergames://uninstall/?gameId=131",
        @"D:\FeverGames\FeverGamesLauncher.exe", "fevergames://uninstall/?gameId=131");
    Check(@"C:\Windows\SysWOW64\msiexec.exe /X {GUID}", @"C:\Windows\SysWOW64\msiexec.exe", "/X {GUID}");

    Console.WriteLine(failures == 0 ? "PARSE TEST: ALL PASS" : $"PARSE TEST: {failures} FAILURES");
    return failures == 0 ? 0 : 1;
}

if (args.Length > 0 && args[0] == "--backup-test")
{
    // backup/restore round-trip test on registry values only (idempotent, safe)
    var query = args.Length > 1 ? args[1] : "Arduino";
    var all = InstalledApps.EnumerateWin32().Concat(UwpApps.Enumerate()).ToList();
    var target = all.FirstOrDefault(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
    if (target is null) { Console.WriteLine($"no app matches '{query}'"); return 1; }

    var residuals = await ResidualScanner.ScanAsync(target, includeAggressive: false);
    foreach (var r in residuals)
        r.Selected = r.Kind == ResidualKind.RegistryValue;
    var selected = residuals.Where(r => r.Selected).ToList();
    Console.WriteLine($"=== 备份测试：{target.DisplayName}，备份 {selected.Count} 个注册表值 ===");

    var manifest = await BackupManager.CreateAsync(target, residuals);
    Console.WriteLine($"备份 ID: {manifest.Id}");
    Console.WriteLine($"条目: {manifest.Items.Count}, 失败: {manifest.Items.Count(i => i.BackupFailed)}, 大小: {manifest.SizeText}");
    foreach (var e in manifest.Items.Where(i => i.BackupFailed))
        Console.WriteLine($"  失败: {e.OriginalPath} -> {e.Error}");

    var backups = BackupManager.ListBackups();
    Console.WriteLine($"备份列表共 {backups.Count} 个:");
    foreach (var b in backups)
        Console.WriteLine($"  {b.Id} | {b.AppName} | {b.Items.Count} 项 | {b.SizeText}");

    Console.WriteLine("还原中（同一份 .reg 重新导入，幂等安全）…");
    var rr = await BackupManager.RestoreAsync(manifest.Id);
    Console.WriteLine($"还原: 成功 {rr.Restored}, 失败 {rr.Failed}");
    foreach (var err in rr.Errors) Console.WriteLine($"  错误: {err}");
    return rr.Failed == 0 && manifest.Items.Count(i => i.BackupFailed) == 0 ? 0 : 1;
}

Console.WriteLine("=== PureUninstall smoke test ===");
Console.WriteLine($"Admin: {AdminHelper.IsAdministrator()}");

var apps = InstalledApps.EnumerateWin32();
Console.WriteLine($"Win32/MSI entries: {apps.Count}");
foreach (var g in apps.GroupBy(a => a.Kind))
    Console.WriteLine($"  {g.Key}: {g.Count()}");

var uwp = UwpApps.Enumerate();
Console.WriteLine($"UWP apps: {uwp.Count}");

Console.WriteLine();
Console.WriteLine("--- top 12 by estimated size ---");
foreach (var a in apps.Concat(uwp)
                      .Where(a => a.EstimatedSizeKb > 0 || a.MeasuredSizeBytes is > 0)
                      .OrderByDescending(a => a.EstimatedSizeKb)
                      .Take(12))
{
    Console.WriteLine($"{a.SizeText,10}  [{a.KindText}] {a.DisplayName}" +
        (string.IsNullOrEmpty(a.DisplayVersion) ? "" : $" v{a.DisplayVersion}") +
        (string.IsNullOrEmpty(a.Publisher) ? "" : $"  ({a.Publisher})"));
}

Console.WriteLine();
Console.WriteLine("--- sample entries ---");
foreach (var a in apps.Where(a => a.UninstallString is not null).Take(10))
{
    Console.WriteLine($"  [{a.KindText}] {a.DisplayName}");
    Console.WriteLine($"      uninstall: {a.UninstallString}");
    if (a.QuietUninstallString is not null)
        Console.WriteLine($"      quiet:     {a.QuietUninstallString}");
}

Console.WriteLine();
Console.WriteLine("SMOKE OK");
return 0;

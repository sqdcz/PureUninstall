using System.Diagnostics;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;
using PureUninstall.Core.Services.Scanners;

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

using PureUninstall.Core.Models;
using PureUninstall.Core.Services.Scanners;

namespace PureUninstall.Core.Services;

/// <summary>Runs all residual scanners and merges the results.</summary>
public static class ResidualScanner
{
    public static async Task<List<ResidualItem>> ScanAsync(
        AppEntry app,
        bool includeAggressive,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var items = new List<ResidualItem>();

        progress?.Report("扫描文件和文件夹…");
        items.AddRange(await Task.Run(() => FileScanner.Scan(app, includeAggressive), ct));

        progress?.Report("扫描注册表…");
        items.AddRange(await Task.Run(() => RegistryScanner.Scan(app, includeAggressive), ct));

        progress?.Report("扫描计划任务…");
        items.AddRange(await Task.Run(() => TaskSchedulerScanner.Scan(app), ct));

        progress?.Report("扫描服务…");
        items.AddRange(await Task.Run(() => ServiceScanner.Scan(app), ct));

        progress?.Report("扫描启动项…");
        items.AddRange(await Task.Run(() => StartupScanner.Scan(app), ct));

        progress?.Report("扫描防火墙规则…");
        items.AddRange(await Task.Run(() => FirewallScanner.Scan(app), ct));

        progress?.Report("扫描 WMI 事件订阅…");
        items.AddRange(await Task.Run(() => WmiScanner.Scan(app), ct));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return items.Where(i => seen.Add($"{i.Kind}|{i.Path}|{i.Detail}|{i.ValueName}")).ToList();
    }
}

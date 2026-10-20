using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>Options for a batch uninstall run.</summary>
public sealed class BatchOptions
{
    public bool Quiet { get; set; } = true;
    public bool AggressiveScan { get; set; }
    public bool BackupBeforeDelete { get; set; } = true;
    public bool DeleteResiduals { get; set; } = true;
    public bool SkipUninstaller { get; set; }
    public TimeSpan UninstallTimeout { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Runs the full flow for a list of apps: uninstall -> scan residuals ->
/// backup -> delete residuals. Sequential, so uninstaller UIs never fight
/// each other.
/// </summary>
public static class BatchUninstaller
{
    public static async Task<List<BatchItemResult>> RunAsync(
        IReadOnlyList<AppEntry> apps,
        BatchOptions options,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<BatchItemResult>();
        int done = 0;

        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            var result = new BatchItemResult { App = app };
            progress?.Report(new BatchProgress(done, apps.Count, app.DisplayName, "正在启动卸载程序…"));

            // 1. run the app's own uninstaller (UWP goes through PowerShell)
            if (options.SkipUninstaller)
            {
                result.Note = "强制删除模式（跳过卸载程序）";
            }
            else if (app.Kind == AppKind.Uwp)
            {
                result.Outcome = await UwpApps.RemoveAsync(app, ct);
            }
            else
            {
                result.Outcome = await Uninstaller.RunAsync(
                    app, options.Quiet, options.UninstallTimeout, ct,
                    log: m => progress?.Report(new BatchProgress(done, apps.Count, app.DisplayName, m)));
            }

            // 2. scan residuals (also runs when uninstall failed, so the user can force-clean)
            progress?.Report(new BatchProgress(done, apps.Count, app.DisplayName, "扫描残留…"));
            var residuals = await ResidualScanner.ScanAsync(app, options.AggressiveScan, null, ct);
            result.Residuals.AddRange(residuals);

            // 3. backup what we are about to delete
            var deletable = residuals.Where(r => r.Selected).ToList();
            if (options.BackupBeforeDelete && deletable.Count > 0)
            {
                progress?.Report(new BatchProgress(done, apps.Count, app.DisplayName, "备份残留…"));
                try
                {
                    var manifest = await BackupManager.CreateAsync(app, residuals, null, ct);
                    result.BackupId = manifest.Id;
                }
                catch (Exception ex)
                {
                    result.Note = $"备份失败（{ex.Message}），已跳过残留删除";
                    deletable.Clear();
                }
            }

            // 4. delete residuals
            if (options.DeleteResiduals && deletable.Count > 0)
            {
                progress?.Report(new BatchProgress(done, apps.Count, app.DisplayName, "清理残留…"));
                var clean = await ResidualCleaner.DeleteAsync(residuals, null, ct);
                result.RemovedCount = clean.Removed;
                if (clean.Failed > 0)
                    result.Note = $"{clean.Failed} 项清理失败（可能被占用或需要管理员）";
            }

            results.Add(result);
            progress?.Report(new BatchProgress(done, apps.Count, app.DisplayName, result.StatusText));
        }

        return results;
    }
}

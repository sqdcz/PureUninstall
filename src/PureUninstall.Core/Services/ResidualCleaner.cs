using Microsoft.Win32;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services.Scanners;

namespace PureUninstall.Core.Services;

public sealed class CleanResult
{
    public int Removed { get; set; }
    public int Failed { get; set; }
    public List<ResidualItem> FailedItems { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Deletes selected residual items. Every path goes through a final safety
/// check before anything is removed.
/// </summary>
public static class ResidualCleaner
{
    public static Task<CleanResult> DeleteAsync(
        IReadOnlyList<ResidualItem> items,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Delete(items, progress, ct), ct);

    private static CleanResult Delete(
        IReadOnlyList<ResidualItem> items,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new CleanResult();

        foreach (var item in items.Where(i => i.Selected))
        {
            ct.ThrowIfCancellationRequested();
            var ok = false;
            string? error = null;
            try
            {
                ok = item.Kind switch
                {
                    ResidualKind.Directory => DeleteDirectory(item.Path),
                    ResidualKind.File or ResidualKind.StartupEntry => DeleteFile(item.Path),
                    ResidualKind.RegistryKey => DeleteRegistryKey(item),
                    ResidualKind.RegistryValue => DeleteRegistryValue(item),
                    ResidualKind.ScheduledTask => TaskSchedulerScanner.Delete(item),
                    ResidualKind.Service => ServiceScanner.Delete(item.Path),
                    ResidualKind.FirewallRule => FirewallScanner.Delete(item),
                    ResidualKind.WmiInstance => WmiScanner.Delete(item),
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
            }

            if (ok)
            {
                result.Removed++;
                progress?.Report($"已删除 [{item.KindText}] {item.Path}");
            }
            else
            {
                result.Failed++;
                result.FailedItems.Add(item);
                if (error is not null) result.Errors.Add($"{item.Path}: {error}");
                progress?.Report($"删除失败 [{item.KindText}] {item.Path}{(error is null ? "" : " - " + error)}");
            }
        }

        return result;
    }

    private static bool DeleteDirectory(string path)
    {
        if (!FileScanner.IsSafeCandidate(path)) return false;
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteFile(string path)
    {
        if (!FileScanner.IsSafeCandidate(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteRegistryKey(ResidualItem item)
    {
        if (item.Hive is not RegistryHive hive || item.View is not RegistryView view) return false;
        var parts = item.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false; // refuse to delete hive roots or hive\SOFTWARE
        var subPath = string.Join('\\', parts.Skip(1));
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            baseKey.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteRegistryValue(ResidualItem item)
    {
        if (item.Hive is not RegistryHive hive || item.View is not RegistryView view) return false;
        if (string.IsNullOrEmpty(item.ValueName)) return false;
        var parts = item.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        var subPath = string.Join('\\', parts.Skip(1));
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subPath, writable: true);
            if (key is null) return false;
            key.DeleteValue(item.ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

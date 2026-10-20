using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>
/// Backs up residual items before deletion (registry keys/values exported to
/// .reg via reg.exe, files/directories copied into the backup folder) and can
/// restore them later.
/// </summary>
public static class BackupManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureUninstall", "Backups");

    public static Task<BackupManifest> CreateAsync(
        AppEntry app,
        IReadOnlyList<ResidualItem> items,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Create(app, items, progress, ct), ct);

    private static BackupManifest Create(
        AppEntry app,
        IReadOnlyList<ResidualItem> items,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeName = MakeFileSystemSafe(app.DisplayName);
        var id = $"{stamp}_{safeName}";
        var dir = Path.Combine(BackupRoot, id);
        Directory.CreateDirectory(dir);

        var manifest = new BackupManifest
        {
            Id = id,
            AppName = app.DisplayName,
            AppId = app.Id,
            CreatedAt = DateTime.Now,
        };

        var selected = items.Where(i => i.Selected).ToList();
        int done = 0;

        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report($"备份中 {done}/{selected.Count}: {item.Path}");

            var info = new BackupEntryInfo
            {
                Kind = item.Kind,
                OriginalPath = item.Path,
                ValueName = item.ValueName,
                RegistryHive = item.Hive?.ToString(),
                RegistryView = item.View?.ToString(),
            };

            try
            {
                switch (item.Kind)
                {
                    case ResidualKind.Directory:
                        if (Directory.Exists(item.Path))
                        {
                            var dest = Path.Combine(dir, "files", done.ToString());
                            CopyDirectory(item.Path, dest);
                            info.BackupPath = dest;
                            manifest.TotalBytes += MeasureDir(dest);
                        }
                        break;

                    case ResidualKind.File:
                    case ResidualKind.StartupEntry:
                        if (File.Exists(item.Path))
                        {
                            var destDir = Path.Combine(dir, "files", done.ToString());
                            Directory.CreateDirectory(destDir);
                            var dest = Path.Combine(destDir, Path.GetFileName(item.Path));
                            File.Copy(item.Path, dest);
                            info.BackupPath = dest;
                            manifest.TotalBytes += new FileInfo(dest).Length;
                        }
                        break;

                    case ResidualKind.RegistryKey:
                    case ResidualKind.RegistryValue:
                        var regFile = Path.Combine(dir, "registry", $"{done}.reg");
                        info.BackupPath = ExportRegistry(item, regFile) ? regFile : null;
                        if (info.BackupPath is not null && File.Exists(regFile))
                            manifest.TotalBytes += new FileInfo(regFile).Length;
                        break;

                    // scheduled tasks / services / firewall / wmi: recorded in manifest only
                }
            }
            catch (Exception ex)
            {
                info.BackupFailed = true;
                info.Error = ex.Message;
            }

            manifest.Items.Add(info);
        }

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        return manifest;
    }

    private static bool ExportRegistry(ResidualItem item, string outFile)
    {
        if (item.Hive is not RegistryHive hive) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        var psi = new ProcessStartInfo("reg.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("export");
        psi.ArgumentList.Add(item.Path);
        psi.ArgumentList.Add(outFile);
        psi.ArgumentList.Add("/y");
        if (item.View == RegistryView.Registry32)
            psi.ArgumentList.Add("/reg:32");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(60_000);
            return proc.ExitCode == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ImportRegistry(BackupEntryInfo entry)
    {
        if (entry.BackupPath is null || !File.Exists(entry.BackupPath)) return false;

        var psi = new ProcessStartInfo("reg.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("import");
        psi.ArgumentList.Add(entry.BackupPath);
        if (entry.RegistryView == nameof(RegistryView.Registry32))
            psi.ArgumentList.Add("/reg:32");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(60_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- listing / restore / delete ----

    public static List<BackupManifest> ListBackups()
    {
        var list = new List<BackupManifest>();
        if (!Directory.Exists(BackupRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(BackupRoot))
        {
            try
            {
                var manifestFile = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestFile)) continue;
                var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestFile));
                if (manifest is not null) list.Add(manifest);
            }
            catch
            {
                // skip broken backup
            }
        }
        return list.OrderByDescending(m => m.CreatedAt).ToList();
    }

    public static Task<RestoreResult> RestoreAsync(string backupId, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Restore(backupId, progress, ct), ct);

    private static RestoreResult Restore(string backupId, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new RestoreResult();
        var dir = Path.Combine(BackupRoot, backupId);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"备份不存在: {backupId}");

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")))
            ?? throw new InvalidDataException("manifest.json 损坏");

        // registry first (recreate parent keys), then files
        foreach (var entry in manifest.Items.Where(e => e.BackupPath is not null && e.BackupPath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"还原注册表: {entry.OriginalPath}");
            if (ImportRegistry(entry)) result.Restored++;
            else { result.Failed++; result.Errors.Add($"注册表还原失败: {entry.OriginalPath}"); }
        }

        foreach (var entry in manifest.Items.Where(e => e.Kind is ResidualKind.Directory or ResidualKind.File or ResidualKind.StartupEntry && e.BackupPath is not null))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"还原文件: {entry.OriginalPath}");
            try
            {
                if (entry.Kind == ResidualKind.Directory && Directory.Exists(entry.BackupPath))
                {
                    CopyDirectory(entry.BackupPath, entry.OriginalPath);
                    result.Restored++;
                }
                else if ((entry.Kind is ResidualKind.File or ResidualKind.StartupEntry) && File.Exists(entry.BackupPath))
                {
                    var targetDir = Path.GetDirectoryName(entry.OriginalPath);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                    File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                    result.Restored++;
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"备份数据缺失: {entry.OriginalPath}");
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{entry.OriginalPath}: {ex.Message}");
            }
        }

        return result;
    }

    public static bool DeleteBackup(string backupId)
    {
        var dir = Path.Combine(BackupRoot, backupId);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- helpers ----

    private static string MakeFileSystemSafe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            if (invalid.Contains(ch) || ch == ' ') sb.Append('_');
            else sb.Append(ch);
        }
        var safe = sb.ToString().Trim('_');
        return safe.Length == 0 ? "app" : safe[..Math.Min(safe.Length, 60)];
    }

    private static void CopyDirectory(string source, string dest)
    {
        var dir = new DirectoryInfo(source);
        if (!dir.Exists) return;
        Directory.CreateDirectory(dest);
        foreach (var file in dir.EnumerateFiles())
        {
            try { file.CopyTo(Path.Combine(dest, file.Name), overwrite: true); } catch { /* skip locked */ }
        }
        foreach (var sub in dir.EnumerateDirectories())
        {
            try { CopyDirectory(sub.FullName, Path.Combine(dest, sub.Name)); } catch { /* skip */ }
        }
    }

    private static long MeasureDir(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                try { total += new FileInfo(f).Length; } catch { /* skip */ }
            }
        }
        catch { /* ignore */ }
        return total;
    }
}

using System.Diagnostics;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>
/// Runs a program's own uninstaller (quiet when available) and waits for its
/// whole process tree to finish before returning.
/// </summary>
public static class Uninstaller
{
    /// <summary>Splits an UninstallString into file name + arguments, honoring quotes.</summary>
    public static (string FileName, string Arguments) ParseCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return (string.Empty, string.Empty);

        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end > 1)
            {
                var file = command[1..end];
                var args = command[(end + 1)..].Trim();
                return (file, args);
            }
        }

        // Unquoted command whose path itself may contain spaces - extremely common
        // for localized software (e.g. "D:\Program Files\bilibili\卸载哔哩哔哩.exe").
        // Walk the spaces and take the longest prefix that is a plausible file.
        var searchFrom = 0;
        while (true)
        {
            var sp = command.IndexOf(' ', searchFrom);
            if (sp < 0) break;
            var candidate = command[..sp];
            if (LooksLikeFile(candidate))
                return (candidate, command[(sp + 1)..].Trim());
            searchFrom = sp + 1;
        }

        return (command, string.Empty);
    }

    private static bool LooksLikeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            // bare command token like "msiexec" or "rundll32" - no path separators
            return !path.Contains('/') && !path.Contains('\\')
                && path.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        if (ext.ToLowerInvariant() is not (".exe" or ".bat" or ".cmd" or ".msi" or ".vbs" or ".ps1" or ".com"))
            return false;

        try { return File.Exists(path); } catch { return false; }
    }

    private static bool IsMsiexec(string file) =>
        Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);

    public static async Task<UninstallOutcome> RunAsync(
        AppEntry app,
        bool quiet,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? log = null)
    {
        var command = quiet
            ? (string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.UninstallString : app.QuietUninstallString)
            : app.UninstallString;

        if (string.IsNullOrWhiteSpace(command))
            return new UninstallOutcome
            {
                Started = false,
                Error = "该条目没有卸载命令（可能是已卸载干净的残留条目，请用「强制删除」）"
            };

        var (file, args) = ParseCommand(command);
        if (file.Length == 0)
            return new UninstallOutcome { Started = false, Error = "卸载命令解析失败" };

        if (quiet && IsMsiexec(file) && !args.Contains("/q", StringComparison.OrdinalIgnoreCase))
            args += " /qn /norestart";

        log?.Invoke($"启动卸载程序: {file} {args}");

        var ext = Path.GetExtension(file).ToLowerInvariant();
        var shellExec = ext is not (".exe" or ".msi"); // bat/cmd/vbs/url/unknown need the shell

        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = shellExec,
        };
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                psi.WorkingDirectory = dir;
        }
        catch
        {
            // ignore bad paths
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new UninstallOutcome { Started = false, Error = $"启动卸载程序失败: {ex.Message}" };
        }

        if (proc is null)
            return new UninstallOutcome { Started = true, Completed = false, Error = "无法监控卸载进程（协议/脚本调用）" };

        var sw = Stopwatch.StartNew();
        bool treeDone;
        try
        {
            treeDone = await ProcessTree.WaitForTreeAsync(proc.Id, timeout, ct, log);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* best effort */ }
            proc.Dispose();
            return new UninstallOutcome { Started = true, Cancelled = true, Error = "已取消" };
        }
        sw.Stop();

        if (!treeDone)
        {
            log?.Invoke("卸载超时，强制结束进程树");
            try { proc.Kill(true); } catch { /* best effort */ }
        }

        var exitCode = -1;
        try { if (proc.HasExited) exitCode = proc.ExitCode; } catch { /* access denied etc. */ }
        proc.Dispose();

        return new UninstallOutcome
        {
            Started = true,
            Completed = treeDone,
            TreeExited = treeDone,
            ExitCode = exitCode,
            Duration = sw.Elapsed,
            Error = treeDone ? null : "卸载超时（已强制结束）",
        };
    }
}

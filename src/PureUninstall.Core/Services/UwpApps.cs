using System.Diagnostics;
using System.Text.Json;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>
/// Enumerates UWP (Microsoft Store) apps via the Appx PowerShell module.
/// </summary>
public static class UwpApps
{
    private const string PsCommand =
        "Get-AppxPackage | Select-Object Name,PackageFullName," +
        "@{n='Version';e={$_.Version.ToString()}},Publisher,InstallLocation," +
        "@{n='Architecture';e={$_.Architecture.ToString()}} " +
        "| ConvertTo-Json -Depth 3 -Compress";

    public static List<AppEntry> Enumerate()
    {
        var result = new List<AppEntry>();
        string json;
        try
        {
            json = RunPowerShell(PsCommand);
        }
        catch
        {
            return result; // UWP enumeration is best-effort
        }
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                AddFrom(root, result);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    AddFrom(el, result);
            }
        }
        catch
        {
            // malformed JSON - skip
        }
        return result;
    }

    private static void AddFrom(JsonElement el, List<AppEntry> result)
    {
        var fullName = el.TryGetProperty("PackageFullName", out var fn) ? fn.GetString() : null;
        var name = el.TryGetProperty("Name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(name)) return;

        result.Add(new AppEntry
        {
            Id = fullName,
            DisplayName = name,
            DisplayVersion = el.TryGetProperty("Version", out var v) ? v.GetString() : null,
            Publisher = el.TryGetProperty("Publisher", out var p) ? p.GetString() : null,
            InstallLocation = el.TryGetProperty("InstallLocation", out var il) && il.ValueKind == JsonValueKind.String
                ? il.GetString()
                : null,
            Kind = AppKind.Uwp,
        });
    }

    /// <summary>Removes a UWP app via Remove-AppxPackage.</summary>
    public static async Task<UninstallOutcome> RemoveAsync(AppEntry app, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"Remove-AppxPackage -Package '{app.Id.Replace("'", "''")}' -ErrorAction Stop");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return new UninstallOutcome { Started = false, Error = "无法启动 PowerShell" };
            await proc.WaitForExitAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync();
            return new UninstallOutcome
            {
                Started = true,
                Completed = true,
                TreeExited = true,
                ExitCode = proc.ExitCode,
                Error = proc.ExitCode == 0 ? null : (string.IsNullOrWhiteSpace(stderr) ? $"Remove-AppxPackage 退出码 {proc.ExitCode}" : stderr.Trim()),
            };
        }
        catch (OperationCanceledException)
        {
            return new UninstallOutcome { Started = true, Cancelled = true, Error = "已取消" };
        }
        catch (Exception ex)
        {
            return new UninstallOutcome { Started = false, Error = ex.Message };
        }
    }

    private static string RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(120_000);
        return output;
    }
}

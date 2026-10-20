using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>
/// Enumerates UWP (Microsoft Store) apps via the Appx PowerShell module.
/// </summary>
public static class UwpApps
{
    // Get-AppxPackage 的 Name 是包标识（如 18496Starpine.Screenbox），不是用户看到的名称。
    // 友好名称要用「开始菜单里的条目」来补：Get-StartApps 的 AppID 形如
    // <PackageFamilyName>!<AppId>，按 ! 前半段与包做映射即可。
    // 顺带过滤掉没有开始菜单条目的包（框架包 / 依赖包 / 资源包），
    // 这些本来也不该出现在"卸载软件"列表里。
    private const string PsCommand =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
        "$m=@{};" +
        "try{Get-StartApps|ForEach-Object{" +
        "if($_.AppID){$p=$_.AppID.Split('!')[0];" +
        "if($p -and -not $m.ContainsKey($p)){$m[$p]=$_.Name}}}}catch{};" +
        "Get-AppxPackage|ForEach-Object{$dn=$m[$_.PackageFamilyName];" +
        "if($dn){[pscustomobject]@{" +
        "Name=$_.Name;PackageFullName=$_.PackageFullName;" +
        "Version=$_.Version.ToString();Publisher=$_.Publisher;" +
        "InstallLocation=$_.InstallLocation;DisplayName=$dn}}}" +
        "|ConvertTo-Json -Depth 3 -Compress";

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

        var friendly = el.TryGetProperty("DisplayName", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString()?.Trim()
            : null;

        result.Add(new AppEntry
        {
            Id = fullName,
            DisplayName = string.IsNullOrWhiteSpace(friendly) ? name : friendly,
            DisplayVersion = el.TryGetProperty("Version", out var v) ? v.GetString() : null,
            Publisher = el.TryGetProperty("Publisher", out var p) ? FriendlyPublisher(p.GetString()) : null,
            InstallLocation = el.TryGetProperty("InstallLocation", out var il) && il.ValueKind == JsonValueKind.String
                ? il.GetString()
                : null,
            Kind = AppKind.Uwp,
        });
    }

    /// <summary>
    /// 包发布者是一长串 DN："CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, ..."，
    /// 直接显示太丑，只取组织名（O=，没有则 CN=）。
    /// 少数包（尤其个人开发者上传的）CN 本身就是一串 GUID，显示出来毫无意义，直接不显示。
    /// </summary>
    private static string? FriendlyPublisher(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string? name = null;
        var parts = raw.Split(',');
        foreach (var key in new[] { "O=", "CN=" })
        {
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > key.Length &&
                    trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    name = trimmed[key.Length..].Trim();
                    break;
                }
            }
            if (name is not null) break;
        }

        name ??= raw.Trim();
        return Guid.TryParse(name, out _) ? null : name;
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
            // 应用名/厂商名含中文，必须按 UTF-8 解码（脚本里已把 OutputEncoding 设成 UTF-8）
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

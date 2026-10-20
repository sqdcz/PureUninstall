using System.Globalization;
using Microsoft.Win32;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>
/// Enumerates classic Win32/MSI installed programs from the registry
/// (same source Geek Uninstaller reads: Uninstall keys under HKLM/HKCU).
/// </summary>
public static class InstalledApps
{
    private const string UninstallSubPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<AppEntry> EnumerateWin32()
    {
        var result = new List<AppEntry>();
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallSubPath);
            if (uninstall is null) continue;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var sub = uninstall.OpenSubKey(subName);
                    if (sub is null) continue;
                    var entry = ReadEntry(sub, subName, hive, view);
                    if (entry is not null) result.Add(entry);
                }
                catch
                {
                    // unreadable key - skip
                }
            }
        }
        return result;
    }

    private static AppEntry? ReadEntry(RegistryKey key, string keyName, RegistryHive hive, RegistryView view)
    {
        var displayName = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        // system components are hidden by Windows itself
        if (key.GetValue("SystemComponent") is int sysComp && sysComp == 1) return null;

        var hiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

        DateTimeOffset? installDate = null;
        if (key.GetValue("InstallDate") is string dateStr
            && dateStr.Length == 8
            && DateTime.TryParseExact(dateStr, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            installDate = new DateTimeOffset(dt);
        }

        return new AppEntry
        {
            Id = keyName,
            DisplayName = Sanitize(displayName),
            DisplayVersion = GetString(key, "DisplayVersion"),
            Publisher = GetString(key, "Publisher"),
            InstallLocation = GetString(key, "InstallLocation"),
            UninstallString = GetString(key, "UninstallString"),
            QuietUninstallString = GetString(key, "QuietUninstallString"),
            DisplayIcon = GetString(key, "DisplayIcon"),
            EstimatedSizeKb = key.GetValue("EstimatedSize") is int es && es > 0 ? es : 0,
            InstallDate = installDate,
            Hive = hive,
            View = view,
            RegistryKeyPath = $@"{hiveName}\{UninstallSubPath}\{keyName}",
            Kind = key.GetValue("WindowsInstaller") is int wi && wi == 1 ? AppKind.Msi : AppKind.Win32,
        };
    }

    private static string? GetString(RegistryKey key, string name)
    {
        var raw = key.GetValue(name) as string;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        try { trimmed = Environment.ExpandEnvironmentVariables(trimmed); } catch { /* keep raw */ }
        return Sanitize(trimmed);
    }

    /// <summary>
    /// Some publishers write binary junk into string registry values (seen in the wild:
    /// e.g. a game launcher stores "蛋仔派对\0&lt;binary blob&gt;" in DisplayName).
    /// Truncate at the first NUL, then strip control chars and private-use-area chars.
    /// </summary>
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var nul = s.IndexOf('\0');
        if (nul >= 0) s = s.Substring(0, nul);
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsControl(ch)) continue;
            if (ch >= '\uE000' && ch <= '\uF8FF') continue; // private use area
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
